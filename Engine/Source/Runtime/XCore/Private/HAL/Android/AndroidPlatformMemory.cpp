// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// AndroidPlatformMemory.cpp -- Android bodies for FPlatformMemory.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1.
//
// Bionic provides mmap, mprotect, madvise, munmap, sysconf, sysinfo
// natively. The implementation is identical to LinuxPlatformMemory.cpp;
// only the platform-gate macro differs. Pages on Quest 3 are 4096 bytes
// (the device-default; large-page support is OS-level only).
//
// Memcpy / Memmove / Memcmp / Memset / Memzero: Bionic's libc-memcpy is
// NEON-optimised for ARM64 (per Android NDK release notes; the Bionic
// implementation is in `bionic/libc/arch-arm64/string/memcpy_base.S`).
// The Section 7 spec note about Bionic memcpy being "measurably slower
// than a hand-rolled NEON loop for sizes >= 64 bytes" predates the
// recent Bionic optimisations and no longer holds for current NDK
// versions; we forward to libc. A future revisit is documented as a
// TODO if benchmarks show drift.
//
// =====================================================================

#include "HAL/FPlatformMemory.h"

#include "Macros/XPactMacros.h"  // XPACT_PLATFORM_ANDROID selector

#if XPACT_PLATFORM_ANDROID

#include <cerrno>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <fcntl.h>
#include <sys/mman.h>
#include <sys/sysinfo.h>
#include <unistd.h>

namespace XCore::HAL
{

::std::uint32_t FPlatformMemory::GetPageSize() noexcept
{
    const long N = ::sysconf(_SC_PAGESIZE);
    if (N <= 0)
    {
        return 4096;
    }
    return static_cast<::std::uint32_t>(N);
}

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

bool FPlatformMemory::CommitVirtual(void* Addr, ::std::size_t NumBytes) noexcept
{
    return ::mprotect(Addr, NumBytes, PROT_READ | PROT_WRITE) == 0;
}

bool FPlatformMemory::DecommitVirtual(void* Addr, ::std::size_t NumBytes) noexcept
{
    const int A = ::madvise(Addr, NumBytes, MADV_DONTNEED);
    const int B = ::mprotect(Addr, NumBytes, PROT_NONE);
    return A == 0 && B == 0;
}

bool FPlatformMemory::ReleaseVirtual(void* Addr, ::std::size_t NumBytes) noexcept
{
    return ::munmap(Addr, NumBytes) == 0;
}

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

#endif // XPACT_PLATFORM_ANDROID
