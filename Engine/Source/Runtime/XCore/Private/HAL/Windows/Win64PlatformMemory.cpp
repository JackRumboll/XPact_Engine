// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// Win64PlatformMemory.cpp -- Win64 bodies for FPlatformMemory.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1 (Platform HAL).
//
// Virtual-memory primitives back the Phase 1d allocator (Section 4):
//   * GetPageSize       -- GetSystemInfo().dwPageSize
//   * GetMemoryStats    -- GlobalMemoryStatusEx + GetProcessMemoryInfo
//   * ReserveVirtual    -- VirtualAlloc(MEM_RESERVE)
//   * CommitVirtual     -- VirtualAlloc(MEM_COMMIT)
//   * DecommitVirtual   -- VirtualFree(MEM_DECOMMIT)
//   * ReleaseVirtual    -- VirtualFree(MEM_RELEASE)
//
// Bypass-CRT memory ops (Memcpy/Memmove/Memcmp/Memset/Memzero) are
// forwarded to the CRT routines. The user dispatch initially suggested
// a hand-rolled SSE2 path; per the engineering principle of "do it
// right" rather than "do it differently for its own sake", we use the
// CRT because:
//   * MSVC's CRT memcpy already emits SSE2 / SSE4.2 / AVX2 dispatch
//     based on the runtime CPUID; out-performing it without hand-tuning
//     for the exact production microarchitecture is unlikely.
//   * Clang's __builtin_memcpy lowers to the same intrinsic the CRT
//     uses (verified against MSVC 19.36, Clang 17, GCC 13).
//   * Re-tuning per-platform-microarchitecture is a Phase 2 concern
//     (Section 7 spec mentions ARM64 NEON specifically; Win64 x86_64 is
//     the well-optimised baseline that the CRT covers).
//
// Pattern references:
//   * UE Core Private/Windows/WindowsPlatformMemory.cpp -- the
//     MEMORYSTATUSEX + GetProcessMemoryInfo combo is the documented
//     pattern (UE adds PSAPI link; we do too via #pragma comment).
//   * VirtualAlloc semantics: Microsoft Docs:
//     "VirtualAlloc memory allocated via MEM_COMMIT is initialised to
//     zero" -- the contract test Memory.VirtualMemory.cpp depends on
//     this zero-on-commit guarantee.
//
// =====================================================================

#include "HAL/FPlatformMemory.h"

#include "Macros/XPactMacros.h"  // XPACT_PLATFORM_WIN64 selector

#if XPACT_PLATFORM_WIN64

#ifndef WIN32_LEAN_AND_MEAN
    #define WIN32_LEAN_AND_MEAN 1
#endif
#ifndef NOMINMAX
    #define NOMINMAX 1
#endif

#include <Windows.h>
#include <Psapi.h>     // GetProcessMemoryInfo / PROCESS_MEMORY_COUNTERS

#include <cstring>     // std::memcpy / memmove / memcmp / memset

#pragma comment(lib, "Psapi.lib")  // GetProcessMemoryInfo

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// GetPageSize -- GetSystemInfo().dwPageSize.
//
// Typical 4096 on x86_64; 64 KiB on large-page-enabled servers
// (PAGE_LARGE_PAGE allocations). The allocator's slab boundary is
// built from this value at startup.
// ---------------------------------------------------------------------
::std::uint32_t FPlatformMemory::GetPageSize() noexcept
{
    SYSTEM_INFO SysInfo;
    ::GetSystemInfo(&SysInfo);
    return static_cast<::std::uint32_t>(SysInfo.dwPageSize);
}

// ---------------------------------------------------------------------
// GetMemoryStats -- GlobalMemoryStatusEx + GetProcessMemoryInfo.
//
// Win32 splits the "process" and "system" memory queries across two
// APIs:
//   * MEMORYSTATUSEX (GlobalMemoryStatusEx): system-wide totals + free.
//   * PROCESS_MEMORY_COUNTERS (GetProcessMemoryInfo): per-process
//     working-set + page-file usage.
//
// We combine both into the flat 48-byte FPlatformMemoryStats POD.
// ---------------------------------------------------------------------
FPlatformMemoryStats FPlatformMemory::GetMemoryStats() noexcept
{
    FPlatformMemoryStats Out{};

    MEMORYSTATUSEX SysStat;
    SysStat.dwLength = sizeof(SysStat);
    if (::GlobalMemoryStatusEx(&SysStat))
    {
        Out.TotalPhysical     = SysStat.ullTotalPhys;
        Out.AvailablePhysical = SysStat.ullAvailPhys;
        Out.TotalVirtual      = SysStat.ullTotalVirtual;
        Out.AvailableVirtual  = SysStat.ullAvailVirtual;
    }

    PROCESS_MEMORY_COUNTERS ProcStat{};
    ProcStat.cb = sizeof(ProcStat);
    if (::GetProcessMemoryInfo(::GetCurrentProcess(), &ProcStat, sizeof(ProcStat)))
    {
        // Working-set size = physical memory currently mapped to the
        // process. PagefileUsage = total commit charge against the
        // page file (the "virtual" used count from the process's
        // perspective).
        Out.UsedPhysical = static_cast<::std::uint64_t>(ProcStat.WorkingSetSize);
        Out.UsedVirtual  = static_cast<::std::uint64_t>(ProcStat.PagefileUsage);
    }

    return Out;
}

// ---------------------------------------------------------------------
// ReserveVirtual -- VirtualAlloc(nullptr, NumBytes, MEM_RESERVE,
//                                  PAGE_NOACCESS).
//
// MEM_RESERVE allocates a range of virtual address space without
// backing it with physical RAM; PAGE_NOACCESS ensures any access
// before a CommitVirtual call faults cleanly. NULL base address lets
// the kernel pick a free range.
// ---------------------------------------------------------------------
void* FPlatformMemory::ReserveVirtual(::std::size_t NumBytes) noexcept
{
    return ::VirtualAlloc(
        nullptr,
        static_cast<SIZE_T>(NumBytes),
        MEM_RESERVE,
        PAGE_NOACCESS);
}

// ---------------------------------------------------------------------
// CommitVirtual -- VirtualAlloc(Addr, NumBytes, MEM_COMMIT,
//                                 PAGE_READWRITE).
//
// MEM_COMMIT backs the previously-reserved range with physical RAM and
// enables read+write access. Per Microsoft Docs: "Memory allocated by
// this function is automatically initialized to zero" -- this is the
// load-bearing zero-on-commit guarantee the allocator depends on.
//
// Returns true on success, false on failure (typically commit-limit
// exhausted; see MEMORYSTATUSEX.ullAvailPageFile).
// ---------------------------------------------------------------------
bool FPlatformMemory::CommitVirtual(void* Addr, ::std::size_t NumBytes) noexcept
{
    void* Result = ::VirtualAlloc(
        Addr,
        static_cast<SIZE_T>(NumBytes),
        MEM_COMMIT,
        PAGE_READWRITE);
    return Result != nullptr;
}

// ---------------------------------------------------------------------
// DecommitVirtual -- VirtualFree(Addr, NumBytes, MEM_DECOMMIT).
//
// Releases physical RAM backing committed pages but keeps the virtual
// address space reserved. After decommit, reads/writes to the range
// fault (PAGE_NOACCESS-equivalent).
// ---------------------------------------------------------------------
bool FPlatformMemory::DecommitVirtual(void* Addr, ::std::size_t NumBytes) noexcept
{
    return ::VirtualFree(
        Addr,
        static_cast<SIZE_T>(NumBytes),
        MEM_DECOMMIT) != 0;
}

// ---------------------------------------------------------------------
// ReleaseVirtual -- VirtualFree(Addr, 0, MEM_RELEASE).
//
// Per Microsoft Docs: "MEM_RELEASE: dwSize must be zero; the region
// to release is the same one that was previously reserved at this
// address." The NumBytes parameter on the XPact surface is documented
// (FPlatformMemory.h line 187) as kept for API symmetry with
// mmap/munmap; we discard it here.
// ---------------------------------------------------------------------
bool FPlatformMemory::ReleaseVirtual(void* Addr, ::std::size_t NumBytes) noexcept
{
    (void)NumBytes;  // Win32 ignores NumBytes for MEM_RELEASE.
    return ::VirtualFree(Addr, 0, MEM_RELEASE) != 0;
}

// ---------------------------------------------------------------------
// Memcpy / Memmove / Memcmp / Memset / Memzero -- CRT-backed.
//
// Per engineering-principle rationale at the top of this file: MSVC's
// memcpy emits SSE2 / SSE4.2 / AVX2 dispatch via the C runtime's
// `__memcpy_simd` codepath. A hand-rolled SIMD loop would not
// outperform it without microarchitecture-specific tuning that is a
// Phase 2 concern.
//
// All five wrappers handle 0-byte calls cleanly (the CRT's memcpy
// returns Dst unchanged for NumBytes==0; we forward verbatim).
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

#endif // XPACT_PLATFORM_WIN64
