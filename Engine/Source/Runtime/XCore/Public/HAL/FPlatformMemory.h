// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FPlatformMemory.h -- abstract platform virtual-memory + bypass-CRT
// memory operations surface.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1 (Platform HAL). The allocator (Section 4,
// Phase 1d) consumes these primitives; downstream callers should not.
//
// Pattern reference: UE Core `GenericPlatformMemory.h:86-183` declares
// `FGenericPlatformMemoryConstants` (held inside `FPlatformMemoryStats`
// as a base) carrying TotalPhysical/PageSize/etc. XPact's surface is
// tighter:
//   - FPlatformMemoryStats is flat (no base struct; the bucketing /
//     pool / forked-page sub-structs in UE are deferred to higher
//     layers).
//   - Adds explicit reserve/commit/decommit/release VM primitives
//     (UE has these on `FPlatformMemory` per-OS but the surface is not
//     uniform across UE's 28 platforms; XPact unifies the three
//     supported platforms).
//   - Adds bypass-CRT Memcpy/Memmove/Memcmp/Memset/Memzero -- per
//     Section 7.6 row 3 (sort of); UE Core uses libc memcpy directly
//     in many hot paths, which is a known cache-stress problem on
//     ARM64 (Bionic's memcpy is slower than the NEON-optimised path
//     XPact will ship in Phase 1b).
//
// Abstract surface only -- NO .cpp bodies in Phase 1a.
//
// =====================================================================

#include <cstddef>
#include <cstdint>

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// FPlatformMemoryStats -- platform memory utilisation snapshot.
//
// Per Section 7.1 spec: a flat POD struct of six uint64 fields. UE's
// FGenericPlatformMemoryStats (GenericPlatformMemory.h:132-183) has
// many more fields including peak-used, memory-pressure status,
// platform-specific stats, and forked-page-allocation tables. XPact
// keeps only the six values the allocator (Section 4) and the
// diagnostic surface (Section 4.1 DumpUsageReport) need; the others
// are deferred to higher layers.
//
// ABI lock: 48 bytes (6 * 8); useful for C# layout-identical struct.
// ---------------------------------------------------------------------

struct FPlatformMemoryStats
{
    uint64_t TotalPhysical;      // bytes of physical RAM installed
    uint64_t TotalVirtual;       // bytes of virtual address space
    uint64_t AvailablePhysical;  // bytes of physical RAM available
    uint64_t AvailableVirtual;   // bytes of virtual address space available
    uint64_t UsedPhysical;       // bytes of physical RAM used by this process
    uint64_t UsedVirtual;        // bytes of virtual address space used by this process
};

static_assert(sizeof(FPlatformMemoryStats) == 48,
              "FPlatformMemoryStats ABI lock: 6 * 8 = 48 bytes");
static_assert(alignof(FPlatformMemoryStats) == 8,
              "FPlatformMemoryStats alignment lock: 8 bytes");

// ---------------------------------------------------------------------
// FPlatformMemory -- abstract platform virtual-memory + bypass-CRT
// memory operations surface.
//
// Class shape mirrors FPlatformMisc. All methods are static-only,
// noexcept, no virtuals. The per-OS .cpp bodies in Phase 1b implement
// the platform-specific calls.
//
// Used by:
//   - FMallocBinned-X (Section 4; Phase 1d) for ReserveVirtual /
//     CommitVirtual / DecommitVirtual / ReleaseVirtual.
//   - Stat dump report (Section 4.1) for GetMemoryStats.
//   - Container resize paths (Section 5) for Memcpy/Memmove via the
//     bypass-CRT route.
// ---------------------------------------------------------------------

class FPlatformMemory
{
public:
    // -----------------------------------------------------------------
    // Page size of the underlying VM system.
    //
    // Phase 1b implementation:
    //   - Win64: GetSystemInfo().dwPageSize (typically 4096; can be
    //            64 KiB on large-page-enabled servers)
    //   - Linux: sysconf(_SC_PAGESIZE) (typically 4096 on x86_64;
    //            16 KiB on ARM64 servers)
    //   - Android: sysconf(_SC_PAGESIZE) (4096 on Quest 3 Snapdragon
    //              XR2 Gen 2)
    //
    // The allocator's slab boundary alignment is built from this value.
    // -----------------------------------------------------------------
    static uint32_t GetPageSize() noexcept;

    // -----------------------------------------------------------------
    // Snapshot of platform memory statistics.
    //
    // Phase 1b implementation:
    //   - Win64: GlobalMemoryStatusEx + GetProcessMemoryInfo
    //   - Linux: sysinfo + /proc/self/statm
    //   - Android: same as Linux
    //
    // Thread-safe (Section 7.2). Returns by value; the FPlatformMemory-
    // Stats POD is 48 bytes and is move/copy-trivially-cheap.
    // -----------------------------------------------------------------
    static FPlatformMemoryStats GetMemoryStats() noexcept;

    // -----------------------------------------------------------------
    // ReserveVirtual -- reserve a range of virtual address space
    // without committing physical RAM.
    //
    // Phase 1b implementation:
    //   - Win64: VirtualAlloc(NULL, NumBytes, MEM_RESERVE,
    //                          PAGE_NOACCESS)
    //   - Linux: mmap(NULL, NumBytes, PROT_NONE,
    //                   MAP_PRIVATE | MAP_ANONYMOUS, -1, 0)
    //   - Android: same as Linux
    //
    // Returns: base address of the reserved range, or nullptr on
    // failure. The reserved range MUST be released via ReleaseVirtual
    // (NOT free() / VirtualFree-with-MEM_RELEASE-and-Size=0; the size
    // and pairing is explicit at the XPact surface, unlike Win32's
    // implicit-tracking pattern).
    //
    // Used by FMallocBinned-X to reserve per-bin VM ranges at startup.
    // -----------------------------------------------------------------
    static void* ReserveVirtual(size_t NumBytes) noexcept;

    // -----------------------------------------------------------------
    // CommitVirtual -- commit (back with physical RAM) pages within a
    // previously-reserved range.
    //
    // Phase 1b implementation:
    //   - Win64: VirtualAlloc(Addr, NumBytes, MEM_COMMIT, PAGE_READWRITE)
    //   - Linux: mprotect(Addr, NumBytes, PROT_READ | PROT_WRITE)
    //            (Linux's mmap with PROT_NONE then mprotect to enable
    //            access is the equivalent of Win32's reserve+commit)
    //   - Android: same as Linux
    //
    // Returns: true on success, false on failure (typically commit
    // limit exhausted; see GetMemoryStats AvailableVirtual).
    //
    // Addr + NumBytes MUST lie within a previously-reserved range. The
    // address MUST be page-aligned (caller's responsibility; the
    // allocator's slab logic always passes page-aligned values).
    // -----------------------------------------------------------------
    static bool CommitVirtual(void* Addr, size_t NumBytes) noexcept;

    // -----------------------------------------------------------------
    // DecommitVirtual -- release physical RAM backing committed pages,
    // but keep the virtual address space reserved.
    //
    // Phase 1b implementation:
    //   - Win64: VirtualFree(Addr, NumBytes, MEM_DECOMMIT)
    //   - Linux: madvise(Addr, NumBytes, MADV_DONTNEED) +
    //            mprotect(Addr, NumBytes, PROT_NONE)
    //   - Android: same as Linux
    //
    // After decommit, reads/writes to the range trap (Win32) or
    // re-zero-fault (Linux/Android). The allocator decommits idle
    // slabs to give RAM back to the OS while keeping the VM range
    // reserved for future use.
    // -----------------------------------------------------------------
    static bool DecommitVirtual(void* Addr, size_t NumBytes) noexcept;

    // -----------------------------------------------------------------
    // ReleaseVirtual -- fully release a previously-reserved range.
    //
    // Phase 1b implementation:
    //   - Win64: VirtualFree(Addr, 0, MEM_RELEASE) -- Win32's release
    //            requires the original reserve base and Size=0
    //   - Linux: munmap(Addr, NumBytes)
    //   - Android: same as Linux
    //
    // After release, the virtual address range is no longer reserved
    // and may be re-used by a subsequent ReserveVirtual.
    //
    // Note: Win32's VirtualFree with MEM_RELEASE ignores the Size
    // argument (must be 0); we still take NumBytes at the XPact surface
    // to keep the API symmetric with mmap/munmap.
    // -----------------------------------------------------------------
    static bool ReleaseVirtual(void* Addr, size_t NumBytes) noexcept;

    // -----------------------------------------------------------------
    // Bypass-CRT memory operations.
    //
    // Phase 1b implementation:
    //   - Win64: SSE2 / SSE4.2 path (compiler intrinsics; the loop body
    //            is a __movsq / __movsd-style hand-rolled fast-path).
    //   - Linux: same as Win64 for x86_64; the calling convention
    //            differs but the kernel of the routine is identical.
    //   - Android (ARM64): NEON-optimised path; the Bionic libc memcpy
    //            on Quest 3 is measurably slower than a hand-rolled
    //            NEON loop for sizes >= 64 bytes (the L1-line-sized
    //            blocks the engine's hot paths copy).
    //
    // ABI: NumBytes is `size_t`; the routine handles 0-byte calls
    // safely (returns Dst unchanged for Memcpy/Memmove/Memset).
    //
    // The "bypass-CRT" name is deliberate: every byte-level memory op
    // in XCore-4a goes through these wrappers, NOT through libc's
    // memcpy directly. This keeps the engine's memory-op cost a single
    // call-site indirection that can be re-tuned per platform without
    // touching downstream code.
    // -----------------------------------------------------------------

    // Copy NumBytes from Src to Dst; ranges MUST NOT overlap. Returns Dst.
    static void* Memcpy(void* Dst, const void* Src, size_t NumBytes) noexcept;

    // Copy NumBytes from Src to Dst; ranges MAY overlap. Returns Dst.
    // Handles forward and backward copy direction internally.
    static void* Memmove(void* Dst, const void* Src, size_t NumBytes) noexcept;

    // Compare NumBytes of A and B. Returns 0 if equal, <0 if A < B
    // (lexicographically by unsigned byte value), >0 if A > B.
    static int Memcmp(const void* A, const void* B, size_t NumBytes) noexcept;

    // Set NumBytes of Dst to (byte)Value. Returns Dst.
    // `Val` is `int` per ANSI memset's signature; the low 8 bits are
    // the byte value, the upper bits are ignored.
    static void* Memset(void* Dst, int Val, size_t NumBytes) noexcept;

    // Set NumBytes of Dst to zero. Returns Dst.
    // Distinct from Memset(Dst, 0, NumBytes) only for clarity at the
    // call site (zero-fill is the common case; named API documents
    // intent). Phase 1b body may dispatch to the same code path.
    static void* Memzero(void* Dst, size_t NumBytes) noexcept;
};

} // namespace XCore::HAL

// =====================================================================
// TODO(Phase 1b):
//   - Implement FPlatformMemory::GetPageSize per OS (GetSystemInfo /
//     sysconf).
//   - Implement FPlatformMemory::GetMemoryStats per OS
//     (GlobalMemoryStatusEx / sysinfo).
//   - Implement FPlatformMemory::ReserveVirtual per OS (VirtualAlloc
//     with MEM_RESERVE / mmap with PROT_NONE).
//   - Implement FPlatformMemory::CommitVirtual per OS (VirtualAlloc
//     with MEM_COMMIT / mprotect with PROT_READ|PROT_WRITE).
//   - Implement FPlatformMemory::DecommitVirtual per OS (VirtualFree
//     with MEM_DECOMMIT / madvise+mprotect).
//   - Implement FPlatformMemory::ReleaseVirtual per OS (VirtualFree
//     with MEM_RELEASE / munmap).
//   - Implement FPlatformMemory::Memcpy/Memmove/Memcmp/Memset/Memzero
//     per OS; x86_64 uses SSE2 fast-path; ARM64 uses NEON.
// =====================================================================
