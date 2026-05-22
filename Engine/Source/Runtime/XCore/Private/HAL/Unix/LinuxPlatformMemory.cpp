// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// LinuxPlatformMemory.cpp -- Linux bodies for FPlatformMemory.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1 (Platform HAL).
//
// Pattern references:
//   * sysconf(_SC_PAGESIZE) for page size.
//   * sysinfo(2) + /proc/self/statm for memory stats. The statm file is
//     POSIX-blessed and exposes 7 fields (size, resident, shared, ...).
//     We read the first two: total pages + resident pages.
//   * mmap(MAP_PRIVATE|MAP_ANONYMOUS|PROT_NONE) for ReserveVirtual.
//     PROT_NONE means the range is reserved but not accessible -- the
//     CommitVirtual call mprotects to PROT_READ|PROT_WRITE to enable
//     access. This matches the Win64 ReserveVirtual/CommitVirtual
//     split semantics.
//   * mprotect(PROT_NONE) + madvise(MADV_DONTNEED) for DecommitVirtual.
//     madvise releases physical RAM; mprotect blocks access so a
//     future read traps cleanly.
//   * munmap(2) for ReleaseVirtual.
//
// Memcpy/Memmove/Memcmp/Memset/Memzero: forwarded to libc. glibc's
// memcpy uses SSE2/AVX2 dispatch on x86_64 -- equivalent codegen to
// MSVC's CRT (Section 7 spec note about CRT being SIMD-optimised
// applies to glibc too).
//
// =====================================================================

#include "HAL/FPlatformMemory.h"

#include "Macros/XPactMacros.h"  // XPACT_PLATFORM_LINUX selector

#if XPACT_PLATFORM_LINUX

#include <cerrno>
#include <cstdint>
#include <cstdio>           // fopen / fscanf for /proc/self/statm
#include <cstring>          // memcpy, memmove, memcmp, memset
#include <fcntl.h>
#include <sys/mman.h>       // mmap, mprotect, munmap, madvise
#include <sys/sysinfo.h>    // sysinfo
#include <sys/types.h>
#include <unistd.h>         // sysconf

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// GetPageSize -- sysconf(_SC_PAGESIZE).
//
// POSIX-standard page-size query. Typical 4096 on x86_64; 16 KiB on
// ARM64 servers (which XPact's Linux target does not run on, but the
// value still reads correctly).
// ---------------------------------------------------------------------
::std::uint32_t FPlatformMemory::GetPageSize() noexcept
{
    const long N = ::sysconf(_SC_PAGESIZE);
    if (N <= 0)
    {
        return 4096;  // documented-impossible kernel state; safe default.
    }
    return static_cast<::std::uint32_t>(N);
}

// ---------------------------------------------------------------------
// GetMemoryStats -- sysinfo + /proc/self/statm.
//
// sysinfo gives system-wide totals (total RAM, total swap, free RAM,
// free swap). /proc/self/statm gives per-process page counts (size,
// resident, shared, text, lib, data, dt).
//
// The fields we populate:
//   * TotalPhysical     = sysinfo.totalram * sysinfo.mem_unit
//   * TotalVirtual      = sysinfo.totalram + sysinfo.totalswap
//   * AvailablePhysical = sysinfo.freeram + sysinfo.bufferram
//   * AvailableVirtual  = sysinfo.freeram + sysinfo.freeswap
//   * UsedPhysical      = statm.resident * PageSize
//   * UsedVirtual       = statm.size * PageSize
// ---------------------------------------------------------------------
FPlatformMemoryStats FPlatformMemory::GetMemoryStats() noexcept
{
    FPlatformMemoryStats Out{};

    struct sysinfo Si;
    if (::sysinfo(&Si) == 0)
    {
        const ::std::uint64_t Unit = static_cast<::std::uint64_t>(Si.mem_unit);
        Out.TotalPhysical     = static_cast<::std::uint64_t>(Si.totalram) * Unit;
        Out.AvailablePhysical = (static_cast<::std::uint64_t>(Si.freeram) +
                                 static_cast<::std::uint64_t>(Si.bufferram)) * Unit;
        Out.TotalVirtual      = (static_cast<::std::uint64_t>(Si.totalram) +
                                 static_cast<::std::uint64_t>(Si.totalswap)) * Unit;
        Out.AvailableVirtual  = (static_cast<::std::uint64_t>(Si.freeram) +
                                 static_cast<::std::uint64_t>(Si.freeswap)) * Unit;
    }

    // /proc/self/statm: "size resident shared text lib data dt"
    // All in pages.
    FILE* StatmFp = ::fopen("/proc/self/statm", "r");
    if (StatmFp != nullptr)
    {
        unsigned long Size = 0;
        unsigned long Resident = 0;
        const int Got = ::fscanf(StatmFp, "%lu %lu", &Size, &Resident);
        ::fclose(StatmFp);
        if (Got >= 2)
        {
            const ::std::uint64_t PageSize = static_cast<::std::uint64_t>(GetPageSize());
            Out.UsedVirtual  = static_cast<::std::uint64_t>(Size)     * PageSize;
            Out.UsedPhysical = static_cast<::std::uint64_t>(Resident) * PageSize;
        }
    }

    return Out;
}

// ---------------------------------------------------------------------
// ReserveVirtual -- mmap with PROT_NONE.
//
// The PROT_NONE flag means the range is reserved (counted against the
// process's address space) but inaccessible. A subsequent CommitVirtual
// call uses mprotect to enable PROT_READ|PROT_WRITE.
//
// MAP_PRIVATE|MAP_ANONYMOUS: anonymous-private-mapping (the standard
// pattern for memory-only allocations; no file backing).
//
// Returns nullptr on failure (mmap returns MAP_FAILED == (void*)-1; we
// normalise to nullptr per the spec contract).
// ---------------------------------------------------------------------
void* FPlatformMemory::ReserveVirtual(::std::size_t NumBytes) noexcept
{
    void* Addr = ::mmap(
        nullptr,
        NumBytes,
        PROT_NONE,
        MAP_PRIVATE | MAP_ANONYMOUS,
        -1,
        0);
    return (Addr == MAP_FAILED) ? nullptr : Addr;
}

// ---------------------------------------------------------------------
// CommitVirtual -- mprotect to PROT_READ|PROT_WRITE.
//
// The previously-reserved (PROT_NONE) range is enabled for read+write.
// Pages are populated on first-access (Linux's standard demand-paging
// behaviour); zero-fill is guaranteed by MAP_ANONYMOUS semantics --
// the zero-on-commit contract matches Win64 VirtualAlloc.
//
// Returns true on success, false on failure (typically commit-limit
// exhausted; observable via /proc/sys/vm/overcommit_memory + OOM-killer
// thresholds).
// ---------------------------------------------------------------------
bool FPlatformMemory::CommitVirtual(void* Addr, ::std::size_t NumBytes) noexcept
{
    return ::mprotect(Addr, NumBytes, PROT_READ | PROT_WRITE) == 0;
}

// ---------------------------------------------------------------------
// DecommitVirtual -- madvise(MADV_DONTNEED) + mprotect(PROT_NONE).
//
// MADV_DONTNEED tells the kernel "I don't need these pages; release
// physical RAM". Subsequent reads see zero-filled pages (zero-fault
// behaviour). mprotect(PROT_NONE) makes the range inaccessible so a
// stray read after decommit faults cleanly.
//
// Note: MADV_DONTNEED on Linux releases physical RAM but does NOT
// release the swap-backed pages. The mprotect ensures the range can
// no longer be accessed; the kernel will trap on read.
// ---------------------------------------------------------------------
bool FPlatformMemory::DecommitVirtual(void* Addr, ::std::size_t NumBytes) noexcept
{
    const int A = ::madvise(Addr, NumBytes, MADV_DONTNEED);
    const int B = ::mprotect(Addr, NumBytes, PROT_NONE);
    return A == 0 && B == 0;
}

// ---------------------------------------------------------------------
// ReleaseVirtual -- munmap(Addr, NumBytes).
//
// Releases both the physical RAM and the virtual address range.
// ---------------------------------------------------------------------
bool FPlatformMemory::ReleaseVirtual(void* Addr, ::std::size_t NumBytes) noexcept
{
    return ::munmap(Addr, NumBytes) == 0;
}

// ---------------------------------------------------------------------
// Memcpy / Memmove / Memcmp / Memset / Memzero -- libc-backed.
// ---------------------------------------------------------------------
void* FPlatformMemory::Memcpy(void* Dst, const void* Src, ::std::size_t NumBytes) noexcept
{
    return ::std::memcpy(Dst, Src, NumBytes);
}

void* FPlatformMemory::Memmove(void* Dst, const void* Src, ::std::size_t NumBytes) noexcept
{
    return ::std::memmove(Dst, Src, NumBytes);
}

int FPlatformMemory::Memcmp(const void* A, const void* B, ::std::size_t NumBytes) noexcept
{
    return ::std::memcmp(A, B, NumBytes);
}

void* FPlatformMemory::Memset(void* Dst, int Val, ::std::size_t NumBytes) noexcept
{
    return ::std::memset(Dst, Val, NumBytes);
}

void* FPlatformMemory::Memzero(void* Dst, ::std::size_t NumBytes) noexcept
{
    return ::std::memset(Dst, 0, NumBytes);
}

} // namespace XCore::HAL

#endif // XPACT_PLATFORM_LINUX
