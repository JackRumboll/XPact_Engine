// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FMallocBinnedX.h -- XPact's FMallocBinned3-descendant allocator.
// =====================================================================
//
// XCore-4a Rev 3, Section 4 (Memory + Allocator), Section 4.5
// (Divergences from UE), Section 17.1 A1 acceptance (110% UE Binned3
// throughput).
//
// XPact's allocator descends from UE's Binned3 design but ships ONE
// tuned codepath, with no Binned2 fallback paths, no legacy console
// overrides, and no per-platform allocator dispatch. The single
// allocator works on all three target platforms (Win64 / Linux /
// Android-ARM64). Section 4.5 row 5 ("UE Binned3 carries Binned2
// fallback paths + per-platform overrides for legacy console support
// ... FMallocBinned-X ships one tuned codepath; Binned2 fallback is
// removed").
//
// UE source citations (pattern adoption; we STUDIED but did NOT copy):
//   * UE Core HAL/MallocBinned3.h:67-83 (allocator architecture
//     comment: "MallocBinned3 supports two types of allocations - large
//     and small pool allocation"). XPact mirrors the small/large
//     split.
//   * UE Core HAL/MallocBinned3.h:39 (UE_MB3_BASE_PAGE_SIZE = 4096).
//     XPact uses 4 KiB on all targets (Win64/Linux/Android-ARM64).
//   * UE Core HAL/MallocBinned3.h:71-72 ("By default each Pool reserves
//     1 GB of address space"). XPact mirrors the 1 GiB per-bin VM
//     reservation.
//   * UE Core HAL/MallocBinnedCommon.cpp:30-102 (BinnedCommonSmallBinSizes
//     tables 4k/8k/12k/16k/20k/24k/28k). XPact adopts a similar but
//     denser geometric progression; see kBinSizeTable below for the
//     XPact-specific layout.
//   * UE Core HAL/MallocBinnedCommon.h:187-208 (FBundleNode: 8-byte
//     in-place free-list node). XPact's FFreeBlock in
//     Private/HAL/FTLSBinCache.h mirrors this; see that header.
//   * UE Core HAL/MallocBinned3.h:127-173 (FPoolTable: BinSize +
//     BlockSize + per-bin mutex + per-bin bit-tree). XPact's
//     FPoolTable below mirrors the shape; the bit-tree is simplified
//     to a free-list head pointer because we don't share UE's
//     OS-page-tracking goals (we keep the VM reservation pinned at
//     1 GiB per bin and grow/shrink the committed region without
//     fragmentation).
//
// DELIBERATE DIVERGENCES:
//   * Per-allocation header (4 bytes) carries the FMemTag (2 bytes) +
//     a 2-byte BinIndex (so Free can find the bin without a global
//     hashmap lookup). UE carries no per-allocation tag and resolves
//     bin index via the global VM-range layout
//     (HAL/MallocBinned3.h:181-184 PoolIndexFromPtr). XPact's header
//     allows the per-tag accounting to be O(1) at Free time.
//   * Single global mutex for large allocations rather than UE's
//     PoolHashBucket + per-bucket mutex (HAL/MallocBinned3.h:81-83).
//     The single mutex is acceptable because large allocations are
//     rare and the contention is low; the simpler structure costs
//     less in cache and code size.
//   * No runtime CVar for bundle size, bundle count, etc. (UE's
//     MallocBinnedCommon.h:71-86). Section 4.5 row 3: "Bin sizes are
//     tunable via Stat instrumentation, not via runtime allocator
//     switching." The values are compile-time constants tuned by the
//     Section 17.1 A1 perf bar.
//
// FAST-PATH ALLOCATION FLOW (small bin):
//   1. Caller: FMemory::Malloc(Size, Align, Tag).
//   2. Facade dispatches to FMallocBinnedX::Malloc.
//   3. Allocator computes BinIndex from Size+Align via SizeToBinIndex
//      (a small lookup table; constant time).
//   4. Allocator pulls a free block from the calling thread's
//      FTLSBinCache.FreeListHead[BinIndex]; if non-empty:
//        a. Pop the head; write the 4-byte header (Tag + BinIndex).
//        b. Update per-tag bytes counter.
//        c. Return the user pointer (header + 4).
//   5. If the cache is empty for this bin:
//        a. Acquire the central pool's per-bin mutex.
//        b. Pull a bundle of N blocks from the central free list.
//           (If the central free list is also empty, commit a new
//            slab of pages within the bin's VM reservation.)
//        c. Release the mutex.
//        d. Push N-1 blocks into the cache; return one to the caller.
//   6. Cross-thread Free: the calling thread sees a block whose
//      header BinIndex maps to a different OwnerThreadId than its
//      own. The block is routed to the owner's MPSC reclaim queue
//      (the simple atomic-linked-list in this Phase 1b implementation).
//
// VM RESERVATION LAYOUT:
//   Per the spec body, 1 GiB reserved per bin, contiguous, page-
//   committed on first touch. Total reserved VM: 56 bins * 1 GiB =
//   56 GiB. This is reservation-only; physical RAM is committed
//   page-by-page on first write. The 1 GiB / bin reservation is
//   chosen to match UE's UE_MB3_MAX_MEMORY_PER_POOL_SIZE_MB = 1024;
//   keeping the same constant makes the per-bin index arithmetic
//   identical and simplifies cross-validation against UE benchmarks.
//
// LARGE ALLOCATIONS (>= 16 KiB):
//   Direct VirtualAlloc / mmap via FPlatformMemory::ReserveVirtual
//   + CommitVirtual. Tracked in a global FLargeAllocList (hash by
//   user pointer) for Free to find. Phase 1b uses std::unordered_map
//   under a global mutex; Phase 1c swaps to a custom intrusive hash
//   table if benchmarks indicate the std map is a bottleneck.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XCoreDefines.h"
#include "Macros/XPactMacros.h"
#include "HAL/FMemTag.h"
#include "HAL/FOOMPolicy.h"
#include "HAL/TBoundedMpscQueue.h"
#include "Private/HAL/FTLSBinCache.h"

#include <atomic>
#include <mutex>

namespace XCore::HAL
{
    // -----------------------------------------------------------------
    // kBinSizeTable -- the per-bin size table.
    //
    // 56 bins from 32 B to 16 KiB. The progression mirrors UE Binned3's
    // density but is tuned for the engine's expected workload
    // (TArray<float> + TMap<int32, FString> + FMath::FVector being the
    // hot ones; small power-of-two + 16-byte-stride bins cover most).
    //
    // The table is constexpr so the SizeToBinIndex table can be
    // computed at compile time (Phase 1b uses a runtime loop; Phase
    // 1c could pre-compute a constexpr 256-byte lookup table for
    // sizes <= 8 KiB).
    //
    // UE source citation: MallocBinnedCommon.cpp:30-102 has
    // BinnedCommonSmallBinSizes4k through BinnedCommonSmallBinSizes28k
    // (52 sizes total); we extended past 28 KiB to cover the 32 KiB
    // / 64 KiB / 16 KiB-boundary cases more densely. Cross-validation
    // against UE Binned3's bench harness in Phase 1c will confirm the
    // table is within the 110% throughput envelope.
    // -----------------------------------------------------------------
    inline constexpr ::uint32 kBinSizeTable[kBinCount] =
    {
        // All bin sizes are multiples of 16 so user pointers at
        // block + kUserOffset (= 16) are always 16-aligned regardless
        // of which bin's slab the block came from.
        //
        // The progression is denser than power-of-two at the small
        // end (32, 48, 64, 80, 96, ...) to minimize internal
        // fragmentation, then thins to power-of-two-and-half at
        // larger sizes.
            32,    48,    64,    80,    96,   112,   128,   144,
           160,   176,   192,   208,   224,   240,   256,   288,
           320,   352,   384,   416,   448,   480,   512,   576,
           640,   704,   768,   832,   896,   960,  1024,  1152,
          1280,  1408,  1536,  1664,  1792,  1920,  2048,  2304,
          2560,  2816,  3072,  3328,  3584,  3840,  4096,  4608,
          5120,  6144,  7168,  8192,  9216, 10240, 12288, 16384,
    };

    static_assert(kBinSizeTable[0]              == 32,    "Smallest bin = 32 B");
    static_assert(kBinSizeTable[kBinCount - 1]  == 16384, "Largest bin = 16 KiB");

    // Compile-time check: every bin size must be a multiple of 16 so
    // user pointers at block + kUserOffset are always 16-aligned. This
    // is the constraint kUserOffset's documentation names.
    namespace Detail
    {
        constexpr bool AllBinsAre16Aligned()
        {
            for (::SIZE_T I = 0; I < kBinCount; ++I)
            {
                if ((kBinSizeTable[I] & 15u) != 0u)
                {
                    return false;
                }
            }
            return true;
        }
    }
    static_assert(Detail::AllBinsAre16Aligned(),
                  "Every bin size must be a multiple of 16 so the user pointer at "
                  "block + kUserOffset (= 16) is 16-aligned across all bins.");

    // -----------------------------------------------------------------
    // kPerBinVMReservation -- bytes reserved per bin (Section 4.1
    // spec body: "1 GB reserved per bin via FPlatformMemory::
    // ReserveVirtual").
    //
    // UE source citation: MallocBinned3.h:32 UE_MB3_MAX_MEMORY_PER_POOL_SIZE_MB
    // = 1024. Same value; same arithmetic.
    // -----------------------------------------------------------------
    inline constexpr ::SIZE_T kPerBinVMReservation = ::SIZE_T(1) * 1024 * 1024 * 1024;  // 1 GiB

    // -----------------------------------------------------------------
    // kLargeAllocThreshold -- the boundary above which allocations
    // skip the binned path and go through ReserveVirtual + CommitVirtual
    // directly.
    //
    // Section 4.1: "Large allocs (>= 16 KiB) go through
    // FPlatformMemory::ReserveVirtual + CommitVirtual directly." The
    // 16 KiB boundary is the same as the largest bin (kBinSizeTable
    // top entry); a 16 KiB alloc itself fits in the largest bin, but
    // a 16 KiB + 1 alloc goes through the large-alloc path.
    // -----------------------------------------------------------------
    inline constexpr ::SIZE_T kLargeAllocThreshold = 16384;

    // -----------------------------------------------------------------
    // FBlockHeader -- the per-allocation header.
    //
    // Data fields (Section 4.1: "Per-allocation header carries the
    // FMemTag (2 bytes) for GetAllocatedBytes accounting").
    //
    // The spec body's literal "2 bytes for FMemTag" is preserved;
    // Phase 1b additionally carries the user-requested size as the
    // 4-byte UserSize field so Free can subtract the exact
    // requested-size delta from per-tag accounting (the Section 4.6
    // "MallocFreeRoundTrip ends in GetAllocatedBytes(tag)==0" test
    // requires exact-size accounting). Without UserSize, Free would
    // have to subtract the bin-rounded size and round-trip leaves
    // non-zero residue per (Size - (BinSize - kUserOffset)).
    //
    // Layout (8 bytes):
    //
    //   [0..1]  FMemTag  Tag       (2 bytes)
    //   [2..3]  uint16   BinIndex  (2 bytes)  -- 0..kBinCount-1 for
    //                                            small bins; kBinCount
    //                                            (= sentinel) for large
    //                                            allocs.
    //   [4..7]  uint32   UserSize  (4 bytes)  -- the user's requested
    //                                            size at Malloc time.
    //                                            Capped at 4 GiB; the
    //                                            large-alloc path uses
    //                                            the sidecar map for
    //                                            sizes > 4 GiB
    //                                            (theoretical only).
    //
    // PLACEMENT IN BLOCK (Phase 1b):
    //
    //   Block start (slab-carved, BinSize-aligned, always 16-aligned):
    //     offsets [0..7]    padding (becomes FFreeBlock::Next when free)
    //     offsets [8..15]   the 8-byte FBlockHeader
    //     offsets [16..BinSize-1]   user data (Align <= 16 guaranteed)
    //
    //   Free path: UserPtr - sizeof(FBlockHeader) reaches offset 8;
    //              read header; convert to slab-carved-block pointer
    //              via (UserPtr - kUserOffset).
    //
    // The 16-byte block prefix is unchanged from the 4-byte-header
    // version; only the header itself widened from 4 to 8 bytes,
    // shrinking the padding band from 12 to 8 bytes. The user pointer
    // alignment is still kUserOffset = 16 ⇒ 16-aligned.
    //
    // Phase 1c optimisation: switch to UE's PoolIndexFromPtr scheme
    // where the bin index is recovered from the user pointer's VM-
    // range location, with the tag + size held in a sidecar table.
    // This eliminates the 16-byte intra-block overhead.
    // TODO(Phase 1c): PoolIndexFromPtr-style tag recovery.
    //
    // ABI lock: 8 bytes for the header struct.
    // -----------------------------------------------------------------
    struct FBlockHeader
    {
        FMemTag  Tag;       // 2 bytes
        ::uint16 BinIndex;  // 2 bytes; kBinCount sentinel = large alloc
        ::uint32 UserSize;  // 4 bytes; the user's requested size
    };

    static_assert(sizeof(FBlockHeader)  == 8, "FBlockHeader ABI lock: 8 bytes (Tag 2 + BinIndex 2 + UserSize 4)");
    static_assert(alignof(FBlockHeader) == 4, "FBlockHeader ABI lock: 4-byte alignment");

    // -----------------------------------------------------------------
    // kUserOffset -- the fixed offset from block start to user pointer.
    //
    // 16 bytes ensures the user pointer is always 16-aligned (since
    // every kBinSizeTable bin size is a multiple of 8 and slabs start
    // at page boundaries, every block start is at least 8-aligned;
    // for bin sizes that are multiples of 16 the block start is 16-
    // aligned; we choose UserOffset = 16 to guarantee 16-aligned
    // user pointers regardless of the bin's block-start alignment).
    //
    // Bins whose sizes are not multiples of 16 still produce 16-
    // aligned user pointers: the block start is 8-aligned, the user
    // offset adds 16 bytes, so user = block + 16 which is 8 + 16 =
    // 24 mod-16 = 8 — wait, that's NOT 16-aligned.
    //
    // The fix: use kBinSizeTable values that are ALL multiples of 16.
    // The kBinSizeTable in FMallocBinnedX.h is updated to honour this
    // constraint (the smallest bin = 32; every subsequent bin is a
    // multiple of 16). The "40", "48"-style 8-aligned bins from UE
    // are dropped in favour of 16-aligned bins; this trades some
    // density for layout simplicity.
    //
    // The static_assert below pins the constraint.
    // -----------------------------------------------------------------
    inline constexpr ::SIZE_T kUserOffset = 16;

    // -----------------------------------------------------------------
    // kLargeAllocBinIndex -- the sentinel BinIndex value used in a
    // FBlockHeader for a large-alloc block.
    //
    // Set to kBinCount (= 56) so it does not collide with any
    // legitimate small-bin index (0..kBinCount-1). Free routes the
    // block to the large-alloc path when it sees this value.
    // -----------------------------------------------------------------
    inline constexpr ::uint16 kLargeAllocBinIndex = kBinCount;

    // -----------------------------------------------------------------
    // FPoolTable -- per-bin central pool state.
    //
    // UE source citation: MallocBinned3.h:127-173 FPoolTable. XPact
    // simplifies: no FBitTree (we use a single freelist + a
    // committed-bytes high-water-mark); no per-block FPoolInfoSmall
    // (large alloc tracking is centralised in g_largeAllocMap).
    // -----------------------------------------------------------------
    struct FPoolTable
    {
        // The bin's reserved VM range. Base address from
        // FPlatformMemory::ReserveVirtual(kPerBinVMReservation).
        // Pages are committed on first touch (slab-by-slab below).
        void* VMBase;

        // High watermark for committed pages within VMBase. Pages
        // [VMBase, VMBase + CommittedBytes) are CommitVirtual-backed;
        // pages [VMBase + CommittedBytes, VMBase + kPerBinVMReservation)
        // are reserved-only.
        ::SIZE_T CommittedBytes;

        // Free-list head for this bin's central pool. Allocated blocks
        // that were freed back to the central pool live here; the TLS
        // cache pulls bundles from this list and pushes flushed
        // bundles back.
        FFreeBlock* CentralFreeListHead;

        // Cross-thread reclaim list head. Phase 1b's Treiber-stack
        // path is RETAINED as the fallback for queue-full conditions
        // (Section 8.1 fix B-C4: "the caller decides the back-
        // pressure policy"). Free first attempts to TryEnqueue into
        // the bounded MPSC queue below; on failure (queue full),
        // falls through to the Treiber stack which trades latency
        // for guaranteed forward progress.
        //
        // In normal operation, the consumer (owner thread) drains
        // CrossThreadReclaimQueue every allocation-touch, so the
        // queue stays nearly empty and TryEnqueue almost always
        // succeeds.
        ::std::atomic<FFreeBlock*> CrossThreadReclaimHead;

        // Bounded MPSC reclaim queue (Section 8.1 fix B-C4; landed
        // in Phase 1c). Producers (any thread Freeing into this bin)
        // TryEnqueue freed FFreeBlock*; consumer (the bin owner via
        // DrainCrossThreadReclaim) drains. Capacity 4096 per bin --
        // ample headroom for typical workloads; queue-full fallback
        // is the Treiber stack above.
        //
        // The queue is constructed in-place via the FPoolTable's
        // copy/move-deletion -- TBoundedMpscQueue is non-movable,
        // and FPoolTable lives in a constinit array, so the queue
        // is default-constructed at static-storage-duration time
        // (its constructor is trivial: zero-initialises the slot
        // sequences via the constexpr ctor).
        //
        // 4096 * sizeof(FFreeBlock*) = 32 KiB per bin per queue;
        // 56 bins = 1.75 MiB total. Acceptable for the engine's
        // memory footprint.
        static constexpr ::SIZE_T kReclaimQueueCapacity = 4096;
        TBoundedMpscQueue<FFreeBlock*, kReclaimQueueCapacity>
            CrossThreadReclaimQueue;

        // Per-bin mutex protecting CentralFreeListHead +
        // CommittedBytes. Cross-thread reclaim list uses lock-free
        // CAS / the bounded MPSC queue; the mutex protects the
        // central pool only.
        //
        // std::mutex is RETAINED here in Phase 1c (NOT swapped to
        // FCriticalSection) because the allocator's FMallocBinnedX
        // default constructor is constexpr (required for the
        // constinit g_Allocator at static-storage-duration time),
        // and FCriticalSection's constructor calls Initialize-
        // CriticalSection / pthread_mutex_init which is NOT
        // constexpr. C++20's std::mutex default ctor IS constexpr,
        // so it can live in the constinit FPoolTable array. The
        // TODO from Phase 1b is therefore RESOLVED to "keep std::
        // mutex" rather than swap.
        //
        // The trade: std::mutex on MSVC is ~80 bytes vs FCritical-
        // Section's 64 bytes; the size cost is acceptable.
        ::std::mutex Mutex;

        // The bin's allocation size in bytes (== kBinSizeTable[BinIndex]).
        // Cached here for fast lookup from the slab-grow path.
        ::uint32 BinSize;

        // The bin's index in kBinSizeTable. Cached for cross-reference.
        ::uint32 BinIndex;
    };

    // -----------------------------------------------------------------
    // FMallocBinnedX -- the central allocator class.
    //
    // One global instance (g_Allocator in FMemory.cpp), constinit-
    // initialised so it is ready before any user-tier static
    // constructor runs.
    //
    // Methods mirror the FMemory facade surface but are not exported
    // beyond the XCore-4a module (this header lives in Private/).
    // -----------------------------------------------------------------
    class FMallocBinnedX
    {
    public:
        // Constructor: trivial; the heavy lifting (VM reservation,
        // pool-table init) happens in __Init so the constinit static
        // can be zero-initialised at static-storage-duration time
        // without invoking the OS.
        constexpr FMallocBinnedX() noexcept = default;

        // Non-copyable / non-movable; one instance per process.
        FMallocBinnedX(const FMallocBinnedX&)            = delete;
        FMallocBinnedX& operator=(const FMallocBinnedX&) = delete;
        FMallocBinnedX(FMallocBinnedX&&)                 = delete;
        FMallocBinnedX& operator=(FMallocBinnedX&&)      = delete;

        // ============================================================
        // Core allocator API (called by FMemory.cpp facade).
        // ============================================================

        void* Malloc(::SIZE_T Size, ::SIZE_T Align, FMemTag Tag) noexcept;
        void* Realloc(void* Ptr, ::SIZE_T NewSize, ::SIZE_T Align, FMemTag Tag) noexcept;
        void  Free(void* Ptr) noexcept;

        // Per-tag accounting accessor.
        [[nodiscard]] ::uint64 GetAllocatedBytes(FMemTag Tag) const noexcept;

        // Engine init / shutdown hooks (called from FMemory::__Init /
        // __Shutdown).
        void Init() noexcept;
        void Shutdown() noexcept;

        // ============================================================
        // Diagnostic helpers (called by FLeakTracker + DumpUsageReport).
        // ============================================================

        // Returns the bin index for the given allocation size, or
        // kLargeAllocBinIndex if the size requires the large-alloc
        // path. Used by the unit tests + by FLeakTracker for per-tag
        // accounting cross-checks.
        [[nodiscard]] static ::uint16 SizeToBinIndex(::SIZE_T Size, ::SIZE_T Align) noexcept;

        // Returns the bin's actual block size in bytes (kBinSizeTable
        // entry). Used by the perf benchmarks for fragmentation
        // analysis.
        [[nodiscard]] static ::uint32 BinIndexToBinSize(::uint16 BinIndex) noexcept;

        // ============================================================
        // PoolIndexFromPtr -- VM-range bin-index recovery (Phase 1g
        // partial landing of Fix B's MAJOR #1).
        // ============================================================
        //
        // Given a user pointer (or any pointer into a bin's slab),
        // returns the bin index whose 1 GiB VM reservation contains
        // it. Returns kLargeAllocBinIndex (= kBinCount) if the pointer
        // is outside every bin's VM reservation; the caller then
        // routes to the large-alloc map.
        //
        // Phase 1g uses this for defense-in-depth validation against
        // the FBlockHeader BinIndex (catches header corruption / alien
        // pointers passed to Free).
        //
        // TODO(Phase 2): once the spec migrates to header-less blocks
        // (per the §4 dispatch goal "eliminate the 8-byte FBlockHeader
        // overhead"), PoolIndexFromPtr replaces the header read on
        // every Free path. The tag + UserSize fields still need a
        // home: either a per-page sidecar table (one entry per page,
        // covering all blocks on that page when they share a tag) or
        // a hash table keyed by user pointer. The choice depends on
        // the measured allocation-pattern distribution; Phase 2
        // benchmarks decide. For Phase 1g we ship the helper +
        // validation hook so the full swap can land incrementally
        // without destabilising the current test suite.
        [[nodiscard]] ::uint16 PoolIndexFromPtr(const void* UserPtr) const noexcept;

        // ============================================================
        // Leak-tracker hook (Section 12).
        // ============================================================

        // The leak tracker installs a callback that is called on every
        // Malloc / Free. Phase 1b: function-pointer-based to avoid
        // requiring std::function. Phase 1c may swap to a richer
        // interface.
        using FMallocHook = void (*)(void* Ptr, ::SIZE_T Size, FMemTag Tag);
        using FFreeHook   = void (*)(void* Ptr);

        // The two hooks. Set by FLeakTracker::__Init / cleared by
        // __Shutdown. Reading is lock-free (relaxed atomic load);
        // setting is also lock-free (relaxed atomic store). The
        // hook is called BEFORE the per-tag accounting update so
        // FLeakTracker can capture the un-double-counted state.
        void SetMallocHook(FMallocHook Hook) noexcept;
        void SetFreeHook(FFreeHook Hook)     noexcept;

    private:
        // ============================================================
        // Private helpers.
        // ============================================================

        // Slow path: pull a fresh bundle from the central pool (or
        // commit new slab pages if the central pool is empty). Called
        // from MallocSmall when the TLS cache is empty.
        [[nodiscard]] FFreeBlock* PullBundleFromCentral(::uint32 BinIndex) noexcept;

        // Slow path: flush a bundle back to the central pool when the
        // TLS cache exceeds the per-bin cap. Called from FreeSmall.
        void FlushBundleToCentral(::uint32 BinIndex, FFreeBlock* Head, ::uint32 Count) noexcept;

    public:
        // ============================================================
        // Thread-exit drain hook (Fix B's MAJOR #2 / Phase 1g).
        //
        // Called from FTLSBinCache::CrossThreadFlushOnExit when a
        // thread terminates with non-empty per-bin free-lists. The
        // exiting thread's blocks are routed to the central pool's
        // reclaim queue / Treiber-stack fallback per bin so the
        // blocks are not leaked. The interface is engine-internal
        // (prefixed with __) and used only by the TLS cache.
        //
        // The Head/Count pair describes the exiting thread's per-bin
        // free list. The function takes ownership of the Head chain;
        // post-call the head pointer must NOT be used by the caller.
        // ============================================================
        void __ThreadExitFlushBundle(::uint32 BinIndex, FFreeBlock* Head, ::uint32 Count) noexcept;

    private:

        // Drain cross-thread reclaim list for the given bin. Called
        // by MallocSmall before pulling from the central pool, so
        // any deferred frees are reclaimed first.
        void DrainCrossThreadReclaim(::uint32 BinIndex) noexcept;

        // Large-alloc path: direct ReserveVirtual + CommitVirtual.
        [[nodiscard]] void* MallocLarge(::SIZE_T Size, ::SIZE_T Align, FMemTag Tag) noexcept;
        void                FreeLarge(void* HeaderPtr) noexcept;

        // The OOM handler. Called when an allocation fails internally
        // (VM commit returns false; ReserveVirtual returns nullptr).
        // Returns nullptr under ReturnNull; aborts otherwise.
        [[nodiscard]] void* HandleOOM(::SIZE_T RequestedSize, FMemTag Tag) noexcept;

        // Per-tag accounting update.
        void AddTagBytes(FMemTag Tag, ::int64 Delta) noexcept;

        // ============================================================
        // Private state.
        // ============================================================

        // The per-bin pool tables. 56 entries; ~256 bytes per entry =
        // ~14 KiB total (well within L1).
        //
        // Initialised at Init() (PreStaticInit phase). The constinit
        // static allows the array to be zero-initialised at static-
        // storage-duration time; Init() then performs the VM
        // reservations.
        FPoolTable m_pools[kBinCount];

        // Per-tag byte counters. Two-step lookup:
        //   * Engine slot range (tag < kMemTagEngineSlotCount):
        //     direct index into m_tagBytes[uint32(Tag)].
        //   * Plugin / User slot range: hash to a secondary table
        //     (Phase 1c follow-up; for Phase 1b, plugin/user-tag
        //     allocations are counted into m_tagBytes[Generic] with
        //     a Dev-only warning).
        //
        // Each counter is std::atomic<int64> to allow lock-free
        // multi-threaded updates. int64 (signed) so a temporary
        // mis-account (rare; usually a test fixture bug) shows up
        // as negative rather than wrapping to a huge positive value.
        ::std::atomic<::int64> m_tagBytes[kMemTagEngineSlotCount];

        // Large-alloc map. Hash from user-pointer to the
        // ReserveVirtual-returned base + the original size + tag.
        // Phase 1b uses a global mutex + std::unordered_map; Phase
        // 1c will swap.
        //
        // The map is intentionally NOT a member of FMallocBinnedX
        // directly (std::unordered_map is not constinit-friendly).
        // It is a function-local static initialised lazily on first
        // large alloc; see FMallocBinnedX.cpp for the
        // GetLargeAllocMap() accessor.

        // Hook function pointers (relaxed atomic so calls are lock-
        // free; the hook itself is responsible for its own
        // thread-safety).
        ::std::atomic<FMallocHook> m_mallocHook;
        ::std::atomic<FFreeHook>   m_freeHook;

        // Init guard. Set true at the end of Init() so callers can
        // sanity-check that they're not allocating before Init has
        // run. relaxed atomic to keep the hot path cheap.
        ::std::atomic<bool> m_initialized;

        // Friend the FMemory facade so it can reach private state if
        // needed (e.g., to set the leak hook from FLeakTracker
        // without exposing the hook pointer to user code).
        friend class FMemory;
    };

    // -----------------------------------------------------------------
    // The single global allocator instance. Declared extern so the
    // FMemory facade can dispatch to it without an indirection. The
    // definition is in FMemory.cpp as a constinit static.
    // -----------------------------------------------------------------
    extern FMallocBinnedX g_Allocator;

} // namespace XCore::HAL
