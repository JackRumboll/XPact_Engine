// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FMallocBinnedX.cpp -- the central allocator body.
// =====================================================================
//
// XCore-4a Rev 3, Section 4. See FMallocBinnedX.h for the architecture
// header and the UE source citations.
//
// Phase 1g Round 2 (PoolIndexFromPtr swap): the per-allocation
// FBlockHeader is gone; the bin index + tag are recovered out-of-band
// via GetPoolMetadata (binary search across the sorted PoolMetadataTable)
// + the per-pool TagSideTable. User pointer == block start; zero
// intra-block overhead while the block is live.
//
// =====================================================================

#include "Private/HAL/FMallocBinnedX.h"
#include "Private/HAL/FTLSBinCache.h"

#include "Macros/XCoreTypes.h"
#include "Macros/XCoreDefines.h"
#include "Macros/XPactMacros.h"
#include "Macros/XAssertionMacros.h"
#include "HAL/FMemory.h"        // for FMemory::IsNoAllocScopeActive
#include "HAL/FMemTag.h"
#include "HAL/FMutex.h"        // Phase 1g fix M-8: FMutex replaces std::mutex at non-constinit sites
#include "HAL/FOOMPolicy.h"
#include "HAL/FPlatformMemory.h"

#include <atomic>
#include <cstring>           // ::std::memcpy / memset
#include <mutex>             // still required: constinit FPoolTable mutex (FMallocBinnedX.h:370-383) needs std::mutex's constexpr ctor; FMutex's ctor is non-constexpr
#include <unordered_map>     // large-alloc map (Phase 1b)

namespace XCore::HAL
{
    // =====================================================================
    // Phase 1g Round 2 internal state -- PoolMetadataTable + TLS MRU cache.
    // =====================================================================
    //
    // PoolMetadataTable: kBinCount pointers to per-pool FPoolMetadata,
    // sorted by PoolBaseAddr ascending. Populated at Init(); read-only
    // thereafter. GetPoolMetadata binary-searches this table to recover
    // the bin / tag for any user pointer.
    //
    // The table is XCONSTINIT zero-initialised (all nullptr) so a
    // GetPoolMetadata call BEFORE Init() returns nullptr (the "outside
    // any bin" sentinel), which is the safe behaviour for pre-Init
    // allocations (none should occur per the PreStaticInit phase ladder,
    // but defense-in-depth covers test-fixture surprises).
    //
    // The table is engine-internal; tests reach it via the static
    // GetPoolMetadata accessor on FMallocBinnedX.
    // =====================================================================

    namespace
    {
        XCONSTINIT FPoolMetadata* g_PoolMetadataTable[kBinCount] = {};
        XCONSTINIT ::std::atomic<bool> g_PoolMetadataTableSorted{ false };

        // Thread-local most-recently-used pool cache. The temporal
        // locality of allocations / frees is high (consecutive Frees in
        // a container destruction loop, consecutive Mallocs in a
        // container grow loop), so caching the last successful
        // GetPoolMetadata hit gives an O(1) fast path for the common
        // case before falling to binary search.
        //
        // The cache is read/written by exactly one thread; no atomics
        // needed for the pointer itself.
        //
        // Epoch invalidation (Phase 1g Round 3 audit MINOR-1 close-out):
        // a Shutdown -> Init cycle in a test harness re-creates pool VM
        // ranges at potentially different addresses; the MRU cache from
        // before the cycle would dereference into released VM. To close
        // this, g_PoolEpoch is bumped at every Init/Shutdown, every
        // FPoolMetadata carries its own Epoch field stamped at Init,
        // and the MRU check requires both the TLS epoch and the
        // metadata's epoch to match the current global epoch. The
        // bumping itself is atomic with release ordering; the MRU
        // check uses acquire ordering on the global epoch load. The
        // TLS epoch starts at 0; the global epoch starts at 1 after
        // the first Init, so a fresh thread sees a non-matching epoch
        // on its first GetPoolMetadata call and falls through to the
        // binary-search path (which writes the correct epoch to TLS).
        XCONSTINIT ::std::atomic<::uint32> g_PoolEpoch{ 0 };
        thread_local FPoolMetadata* g_TlsLastPoolMetadata = nullptr;
        thread_local ::uint32       g_TlsLastPoolEpoch    = 0;
    } // anonymous

    // =====================================================================
    // Large-alloc map (Phase 1b implementation).
    //
    // Maps user-pointer to the original ReserveVirtual base + size + tag.
    // Free uses this map to find the right ReleaseVirtual arguments for
    // a large alloc.
    //
    // Phase 1b: std::unordered_map under a global mutex. Phase 1c will
    // swap to a custom intrusive hash table if benchmarks show this is
    // a bottleneck.
    //
    // Phase 1g Round 2 note: the large-alloc path no longer carries an
    // intra-block FBlockHeader (the field was redundant -- the sidecar
    // FLargeAllocRecord already records Tag + UserSize + VMBase + VMSize).
    // The user pointer for a large alloc is the page-aligned (Align-
    // honouring) head of the OS-allocated VM range; FreeLarge looks it
    // up directly in the map.
    // =====================================================================

    namespace
    {
        struct FLargeAllocRecord
        {
            void*    VMBase;     // ReserveVirtual return; for Release
            ::SIZE_T VMSize;     // bytes reserved (page-aligned, >= Size + Align slack)
            ::SIZE_T UserSize;   // requested allocation size
            FMemTag  Tag;        // for accounting at Free
        };

        // The map is a function-local static so it is initialized on first
        // call -- avoids any static-init ordering hazards with
        // FMallocBinnedX::g_Allocator (which is constinit).
        ::std::unordered_map<void*, FLargeAllocRecord>& GetLargeAllocMap() noexcept
        {
            static ::std::unordered_map<void*, FLargeAllocRecord> Map;
            return Map;
        }

        // Phase 1g fix M-8: FMutex replaces std::mutex here. This is a
        // function-local Meyers singleton -- NOT constinit -- so the
        // FMutex's non-constexpr constructor is fine. The principled
        // choice between FMutex and std::mutex is:
        //
        //   * FPoolTable (FMallocBinnedX.h:370-383) retains std::mutex
        //     because its constructor is constexpr-required for
        //     constinit (the pool table is reachable at PreStaticInit;
        //     swapping to FMutex would require FMutex's ctor to be
        //     constexpr, which is not feasible because the platform
        //     SRWLock / pthread_mutex_t is initialised via a runtime
        //     OS call inside the ctor body).
        //
        //   * Non-constinit FMallocBinnedX-internal singletons (this
        //     one) use FMutex per engineering-principles "absolute
        //     integration with our system" -- we want every part of
        //     XPact that can use the engine's own primitives to do so,
        //     and only fall back to std:: where a hard correctness
        //     constraint (here: constinit) forces it.
        ::XCore::HAL::FMutex& GetLargeAllocMutex() noexcept
        {
            static ::XCore::HAL::FMutex Mutex;
            return Mutex;
        }

        // ---------------- helpers ----------------

        // Round up to multiple of Alignment (Alignment must be power of two).
        [[nodiscard]] XPACT_FORCEINLINE ::SIZE_T RoundUpToAlign(::SIZE_T Size, ::SIZE_T Alignment) noexcept
        {
            return (Size + Alignment - 1) & ~(Alignment - 1);
        }

        // Round up to the next multiple of PageSize (queried from Platform HAL).
        [[nodiscard]] ::SIZE_T RoundUpToPage(::SIZE_T Size) noexcept
        {
            const ::SIZE_T PageSize = ::XCore::HAL::FPlatformMemory::GetPageSize();
            return RoundUpToAlign(Size, PageSize);
        }

        // Sort the PoolMetadataTable by PoolBaseAddr ascending. Called
        // once at the end of Init() (after every pool's VM is reserved
        // + metadata initialised). Uses a simple insertion sort because
        // the table is small (kBinCount = 56) and the comparator is a
        // simple pointer compare.
        void SortPoolMetadataTable() noexcept
        {
            for (::SIZE_T I = 1; I < kBinCount; ++I)
            {
                FPoolMetadata* Key = g_PoolMetadataTable[I];
                if (Key == nullptr)
                {
                    continue;  // unused slot; should not normally occur after Init
                }
                ::SIZE_T J = I;
                while (J > 0)
                {
                    FPoolMetadata* Prev = g_PoolMetadataTable[J - 1];
                    if (Prev == nullptr)
                    {
                        // Bubble null slots to the end (unsorted region)
                        break;
                    }
                    if (reinterpret_cast<::UPTRINT>(Prev->PoolBaseAddr) <=
                        reinterpret_cast<::UPTRINT>(Key->PoolBaseAddr))
                    {
                        break;
                    }
                    g_PoolMetadataTable[J] = Prev;
                    --J;
                }
                g_PoolMetadataTable[J] = Key;
            }
            g_PoolMetadataTableSorted.store(true, ::std::memory_order_release);
        }

        // Compute the side-table byte size for a bin's VM range, given
        // the pool size and bin size. The side-table is one uint16
        // (FMemTag) per block; the block count is bounded by the
        // user-data area capacity, which is itself a function of the
        // side-table size (recursive). The closed-form solution:
        //
        //   N_max = (PoolSize - kPoolMetadataReservedHeader) /
        //           (BinSize + sizeof(uint16))
        //
        // Then side-table bytes = N_max * sizeof(uint16), page-rounded
        // up. The user-data area starts at the page-aligned boundary
        // beyond the side-table.
        //
        // The recursive division converges in one step because both
        // BinSize and sizeof(uint16) are constants.
        [[nodiscard]] ::SIZE_T ComputeSideTableBytes(::SIZE_T PoolSize, ::SIZE_T BinSize) noexcept
        {
            const ::SIZE_T AvailableForBlocks = PoolSize - kPoolMetadataReservedHeader;
            const ::SIZE_T NMax               = AvailableForBlocks / (BinSize + sizeof(::uint16));
            const ::SIZE_T SideTableBytes     = NMax * sizeof(::uint16);
            return SideTableBytes;
        }

        // Compute the user-data area base offset within a pool's VM
        // range: kPoolMetadataReservedHeader + ComputeSideTableBytes,
        // page-rounded up. The user-data area itself starts at this
        // offset; blocks within it are BinSize-spaced.
        [[nodiscard]] ::SIZE_T ComputeUserDataAreaOffset(::SIZE_T PoolSize, ::SIZE_T BinSize) noexcept
        {
            const ::SIZE_T SideTableBytes = ComputeSideTableBytes(PoolSize, BinSize);
            const ::SIZE_T RawOffset      = kPoolMetadataReservedHeader + SideTableBytes;
            return RoundUpToPage(RawOffset);
        }

        // Compute the max-blocks count for a pool given its bin size
        // and computed user-data area offset. Clamped to the side-
        // table's actual capacity (which can be smaller than
        // UserDataAreaSize / BinSize when page-alignment of the
        // header grows the available user-data area beyond what the
        // recursive ComputeSideTableBytes formula assumed).
        //
        // Without the clamp, allocations past the side-table
        // capacity would write past the side-table's committed
        // bytes -- buffer overflow into the user-data region's
        // first bytes. The clamp prevents that by treating the
        // pool as "full" once we exhaust the side-table.
        [[nodiscard]] ::SIZE_T ComputeMaxBlocks(::SIZE_T PoolSize, ::SIZE_T BinSize, ::SIZE_T UserDataAreaOffset) noexcept
        {
            const ::SIZE_T UserDataAreaSize    = PoolSize - UserDataAreaOffset;
            const ::SIZE_T BlocksByUserArea    = UserDataAreaSize / BinSize;
            const ::SIZE_T SideTableEntries    = ComputeSideTableBytes(PoolSize, BinSize) / sizeof(::uint16);
            return (BlocksByUserArea < SideTableEntries) ? BlocksByUserArea : SideTableEntries;
        }

        // Given a user pointer and the FPoolMetadata for its pool,
        // compute the block index: blocks are BinSize-spaced starting
        // at UserDataAreaBase. Used by Malloc / Free to index into
        // the side-table.
        //
        // PRECONDITION: the slab-commit path uses gapless slabs (slab
        // size == LCM(BinSize, PageSize)) so blocks are tightly packed
        // across slab boundaries. Without that, page-aligned slabs
        // whose size is not a multiple of BinSize would introduce
        // gaps and cause block-index collisions across slabs.
        [[nodiscard]] XPACT_FORCEINLINE ::SIZE_T BlockIndexFromUserPtr(const FPoolMetadata* Meta, const void* UserPtr) noexcept
        {
            const ::UPTRINT P    = reinterpret_cast<::UPTRINT>(UserPtr);
            const ::UPTRINT Base = reinterpret_cast<::UPTRINT>(Meta->UserDataAreaBase);
            return static_cast<::SIZE_T>((P - Base) / Meta->BinSize);
        }

        // Greatest-common-divisor (Euclid). Used by ComputeSlabSize to
        // compute LCM(BinSize, PageSize) for the gapless slab commit.
        [[nodiscard]] constexpr ::SIZE_T GcdSize(::SIZE_T A, ::SIZE_T B) noexcept
        {
            while (B != 0)
            {
                const ::SIZE_T T = B;
                B = A % B;
                A = T;
            }
            return A;
        }

        // Compute the gapless slab size for a bin: the LCM of BinSize
        // and PageSize. The slab is then both page-aligned (legal
        // for CommitVirtual) AND a whole-multiple of BinSize (so
        // consecutive slabs concatenate without block-index gaps).
        //
        // For BinSize that divides PageSize evenly (power-of-2 bins
        // <= PageSize), LCM == PageSize and the slab is one page.
        // For BinSize > PageSize, LCM >= BinSize. For non-power-of-2
        // bins (48, 80, 96, ...) the slab is multiple pages: e.g.,
        // BinSize=48 -> LCM=12288 (3 pages, 256 blocks).
        //
        // We scale up small base-slab counts so the commit overhead
        // (one OS call) amortises over a reasonable block count. The
        // base LCM yields at least 1 block; for the largest bin
        // (16 KiB) base LCM = 16384 (4 pages, 1 block). Scale up to
        // >= kMinBlocksPerSlab blocks per slab by multiplying.
        [[nodiscard]] ::SIZE_T ComputeSlabSize(::SIZE_T BinSize, ::SIZE_T PageSize) noexcept
        {
            constexpr ::SIZE_T kMinBlocksPerSlab = 16;

            const ::SIZE_T G        = GcdSize(BinSize, PageSize);
            const ::SIZE_T BaseLCM  = (BinSize / G) * PageSize;
            const ::SIZE_T BaseBlks = BaseLCM / BinSize;
            if (BaseBlks >= kMinBlocksPerSlab)
            {
                return BaseLCM;
            }
            // Scale up: how many BaseLCM multiples to reach the floor?
            const ::SIZE_T Multiplier = (kMinBlocksPerSlab + BaseBlks - 1) / BaseBlks;
            return BaseLCM * Multiplier;
        }
    } // anonymous

    // =====================================================================
    // GetPoolMetadata -- binary-search the PoolMetadataTable.
    //
    // Hot-path fast path: check the TLS most-recently-used cache first.
    // Slow path: binary search over the sorted table (kBinCount = 56,
    // log2(56) ~= 6 compares).
    //
    // Returns nullptr if the pointer is outside every bin's VM range
    // (large alloc / alien pointer); the caller routes accordingly.
    // =====================================================================
    const FPoolMetadata* FMallocBinnedX::GetPoolMetadata(const void* UserPtr) noexcept
    {
        // Fast path: TLS MRU cache. Consecutive allocations / frees in
        // a container loop hit the same pool; >80% of calls under
        // workload short-circuit here.
        //
        // Epoch check (audit MINOR-1 close-out): we require both the
        // TLS-cached epoch AND the metadata's own Epoch field to match
        // the current global epoch. After a Shutdown/Init cycle the
        // global epoch has been bumped; any stale TLS pointer fails
        // the check and falls through to the binary-search slow path,
        // which writes a fresh (pointer, epoch) pair.
        const ::uint32 CurrentEpoch = g_PoolEpoch.load(::std::memory_order_acquire);
        FPoolMetadata* MRU = g_TlsLastPoolMetadata;
        if (MRU != nullptr && g_TlsLastPoolEpoch == CurrentEpoch && MRU->Epoch == CurrentEpoch)
        {
            const ::UPTRINT P    = reinterpret_cast<::UPTRINT>(UserPtr);
            const ::UPTRINT Base = reinterpret_cast<::UPTRINT>(MRU->PoolBaseAddr);
            if (P >= Base && P < (Base + MRU->PoolSize))
            {
                return MRU;
            }
        }

        // Slow path: binary search. The table is sorted ascending by
        // PoolBaseAddr; we seek the entry whose [base, base + size)
        // covers UserPtr.
        if (!g_PoolMetadataTableSorted.load(::std::memory_order_acquire))
        {
            // Init hasn't completed; cannot binary-search. Linear
            // fallback covers the brief Init window (pre-sort) and
            // any edge case where Shutdown has run.
            const ::UPTRINT P = reinterpret_cast<::UPTRINT>(UserPtr);
            for (::SIZE_T I = 0; I < kBinCount; ++I)
            {
                FPoolMetadata* M = g_PoolMetadataTable[I];
                if (M == nullptr)
                {
                    continue;
                }
                const ::UPTRINT Base = reinterpret_cast<::UPTRINT>(M->PoolBaseAddr);
                if (P >= Base && P < (Base + M->PoolSize))
                {
                    g_TlsLastPoolMetadata = M;
                    g_TlsLastPoolEpoch    = CurrentEpoch;
                    return M;
                }
            }
            return nullptr;
        }

        const ::UPTRINT P = reinterpret_cast<::UPTRINT>(UserPtr);
        ::int32 Lo = 0;
        ::int32 Hi = static_cast<::int32>(kBinCount) - 1;
        while (Lo <= Hi)
        {
            const ::int32 Mid = Lo + ((Hi - Lo) >> 1);
            FPoolMetadata* M  = g_PoolMetadataTable[Mid];
            if (M == nullptr)
            {
                // Defensive: should not occur after a successful Init.
                // Treat null entry as "no pool at this slot" and shrink
                // the search window by skipping it.
                Hi = Mid - 1;
                continue;
            }
            const ::UPTRINT Base = reinterpret_cast<::UPTRINT>(M->PoolBaseAddr);
            if (P < Base)
            {
                Hi = Mid - 1;
            }
            else if (P >= (Base + M->PoolSize))
            {
                Lo = Mid + 1;
            }
            else
            {
                g_TlsLastPoolMetadata = M;
                g_TlsLastPoolEpoch    = CurrentEpoch;
                return M;
            }
        }
        return nullptr;
    }

    // =====================================================================
    // SizeToBinIndex / BinIndexToBinSize static helpers.
    //
    // Phase 1g Round 2: kUserOffset is gone (user pointer == block start),
    // so size mapping is now Size + Align-slack-only rather than Size +
    // 16. Align <= 16 is honoured by bin-size 16-alignment. Align > 16
    // routes to the large-alloc path (where the explicit alignment
    // computation in MallocLarge applies the requested alignment).
    // =====================================================================

    ::uint16 FMallocBinnedX::SizeToBinIndex(::SIZE_T Size, ::SIZE_T Align) noexcept
    {
        // Align > 16 cannot be satisfied by the small-bin path because
        // every bin's user-block starts at the BinSize-spaced grid
        // (BinSize is a multiple of 16, hence each block is 16-aligned
        // but not necessarily 32-aligned). Larger alignments go through
        // MallocLarge.
        if (Align > 16)
        {
            return kLargeAllocBinIndex;
        }

        if (Size > kLargeAllocThreshold)
        {
            return kLargeAllocBinIndex;
        }

        // Linear search over kBinSizeTable. Phase 1c may swap to a
        // pre-computed lookup table (256-byte LUT for sizes <= 8 KiB);
        // for Phase 1b a 56-step compare loop is acceptable.
        for (::uint16 I = 0; I < kBinCount; ++I)
        {
            if (static_cast<::SIZE_T>(kBinSizeTable[I]) >= Size)
            {
                return I;
            }
        }

        // Should not reach here: Size <= kLargeAllocThreshold but
        // exceeds the largest bin. The kBinSizeTable top entry IS
        // kLargeAllocThreshold so this branch fires only if the table
        // and the threshold disagree (a build-time bug).
        return kLargeAllocBinIndex;
    }

    ::uint32 FMallocBinnedX::BinIndexToBinSize(::uint16 BinIndex) noexcept
    {
        if (BinIndex >= kBinCount)
        {
            return 0;  // sentinel for "not a small bin"
        }
        return kBinSizeTable[BinIndex];
    }

    // =====================================================================
    // FMallocBinnedX::Init -- VM reservation + pool-table setup.
    // =====================================================================
    //
    // For each of the 56 bins:
    //   1. ReserveVirtual(1 GiB) for the bin's VM range.
    //   2. CommitVirtual the metadata page (the FPoolMetadata struct
    //      at offset 0 of the VM range).
    //   3. Initialise the FPoolMetadata: PoolBaseAddr, UserDataAreaBase,
    //      BinSize, PoolSize, MaxBlocks, TagSideTable pointer,
    //      BinIndex.
    //   4. Publish the metadata pointer into the global PoolMetadata-
    //      Table[BinIndex].
    //
    // After all 56 pools are initialised, sort the PoolMetadataTable
    // by PoolBaseAddr ascending so GetPoolMetadata's binary search works.
    // =====================================================================

    void FMallocBinnedX::Init() noexcept
    {
        // Already initialized? Idempotent (Section 1.5 invariant:
        // PreStaticInit hooks may be called once; defensive double-call
        // protection here).
        if (m_initialized.load(::std::memory_order_acquire))
        {
            return;
        }

        // Bump the global pool epoch (audit MINOR-1 close-out). Stamped
        // into every FPoolMetadata below; the MRU cache in
        // GetPoolMetadata requires the metadata's Epoch field AND the
        // TLS-cached epoch to both match this value. After a
        // Shutdown -> Init cycle the new epoch differs from the old
        // one stored in any pre-cycle TLS cache, so all stale MRU
        // pointers correctly miss and fall through to binary search
        // (which then writes the fresh epoch).
        const ::uint32 NewEpoch = g_PoolEpoch.fetch_add(1, ::std::memory_order_release) + 1;

        // Reserve + setup each per-bin VM range.
        for (::uint32 I = 0; I < kBinCount; ++I)
        {
            FPoolTable& Pool = m_pools[I];

            Pool.BinSize  = kBinSizeTable[I];
            Pool.BinIndex = I;
            Pool.VMBase   = ::XCore::HAL::FPlatformMemory::ReserveVirtual(kPerBinVMReservation);

            if (Pool.VMBase == nullptr)
            {
                // VM reservation failure during Init is unrecoverable.
                ::XCore::HAL::AbortWithMessage(
                    "FMallocBinnedX::Init: ReserveVirtual failed for bin",
                    __FILE__, __LINE__);
            }

            // Commit the first page so we can write the FPoolMetadata
            // header. The metadata occupies the first 64 bytes; the
            // remainder of that first page is the start of the side
            // table.
            const ::SIZE_T PageSize = ::XCore::HAL::FPlatformMemory::GetPageSize();
            if (!::XCore::HAL::FPlatformMemory::CommitVirtual(Pool.VMBase, PageSize))
            {
                ::XCore::HAL::AbortWithMessage(
                    "FMallocBinnedX::Init: CommitVirtual(metadata page) failed for bin",
                    __FILE__, __LINE__);
            }

            // Compute the pool's layout: side-table size, user-data
            // area offset (page-aligned past metadata + side-table),
            // and max-block count.
            const ::SIZE_T UserDataAreaOffset =
                ComputeUserDataAreaOffset(kPerBinVMReservation, Pool.BinSize);
            const ::SIZE_T MaxBlocks =
                ComputeMaxBlocks(kPerBinVMReservation, Pool.BinSize, UserDataAreaOffset);

            // Place the FPoolMetadata at offset 0 of the VM range. The
            // first page (PageSize bytes) covers the 64-byte header
            // plus the first (PageSize - 64) bytes of the side-table.
            FPoolMetadata* Meta = reinterpret_cast<FPoolMetadata*>(Pool.VMBase);
            Meta->PoolBaseAddr     = Pool.VMBase;
            Meta->UserDataAreaBase = reinterpret_cast<::uint8*>(Pool.VMBase) + UserDataAreaOffset;
            Meta->BinSize          = Pool.BinSize;
            Meta->PoolSize         = kPerBinVMReservation;
            Meta->MaxBlocks        = MaxBlocks;
            Meta->TagSideTable     = reinterpret_cast<::uint16*>(
                                        reinterpret_cast<::uint8*>(Pool.VMBase) + kPoolMetadataReservedHeader);
            Meta->BinIndex         = static_cast<::uint16>(I);
            for (auto& B : Meta->_pad) { B = 0; }
            Meta->Epoch            = NewEpoch;  // audit MINOR-1 close-out

            // The first PageSize bytes of the VM range are now
            // committed. Of those, kPoolMetadataReservedHeader (64)
            // are the metadata; the remaining (PageSize - 64) are
            // valid side-table bytes. Track that initial commit.
            Pool.Metadata               = Meta;
            Pool.UserCommittedBytes     = 0;
            Pool.SideTableCommittedBytes = PageSize - kPoolMetadataReservedHeader;
            Pool.CentralFreeListHead    = nullptr;
            Pool.CrossThreadReclaimHead.store(nullptr, ::std::memory_order_relaxed);

            // Initialise the bounded MPSC reclaim queue (Section 8.1
            // fix B-C4 swap-in landed in Phase 1c). The Vyukov
            // invariant (slot[i].seq = i) is set here at Init()-time;
            // the queue's default constructor leaves slot sequences
            // at zero (constexpr-required) which would corrupt the
            // algorithm's empty-vs-full distinction without this
            // call.
            Pool.CrossThreadReclaimQueue.Initialize();

            // Publish into the PoolMetadataTable. Slot index = bin
            // index pre-sort; the post-loop sort step reorders by
            // PoolBaseAddr.
            g_PoolMetadataTable[I] = Meta;
        }

        // Zero-init per-tag accounting (atomic stores are no-ops on
        // already-zero memory but document the intent).
        for (::SIZE_T I = 0; I < kMemTagEngineSlotCount; ++I)
        {
            m_tagBytes[I].store(0, ::std::memory_order_relaxed);
        }

        // No hooks installed yet.
        m_mallocHook.store(nullptr, ::std::memory_order_relaxed);
        m_freeHook.store(nullptr, ::std::memory_order_relaxed);

        // Sort the PoolMetadataTable by PoolBaseAddr ascending so the
        // GetPoolMetadata binary search is well-defined. This MUST
        // happen after all pools are reserved + metadata initialised.
        SortPoolMetadataTable();

        m_initialized.store(true, ::std::memory_order_release);
    }

    // =====================================================================
    // FMallocBinnedX::Shutdown -- release VM, validate accounting.
    // =====================================================================

    void FMallocBinnedX::Shutdown() noexcept
    {
        if (!m_initialized.load(::std::memory_order_acquire))
        {
            return;
        }

        // Mark the PoolMetadataTable as no-longer-sorted FIRST so a
        // concurrent GetPoolMetadata that races shutdown sees the
        // linear-scan fallback (safer if entries are being nulled).
        g_PoolMetadataTableSorted.store(false, ::std::memory_order_release);

        // Bump the global pool epoch (audit MINOR-1 close-out). Any
        // surviving TLS MRU cache from this Init session now mismatches
        // the global epoch and falls through to the binary-search slow
        // path on the next GetPoolMetadata call -- which will see the
        // nulled g_PoolMetadataTable entries and return nullptr (large-
        // alloc / alien-pointer code path), not dereference the
        // about-to-be-released VM. The fetch_add is release-ordered so
        // other threads' next acquire-load sees the new epoch
        // before the ReleaseVirtual calls below land.
        g_PoolEpoch.fetch_add(1, ::std::memory_order_release);

        // Release all per-bin VM reservations.
        for (::uint32 I = 0; I < kBinCount; ++I)
        {
            FPoolTable& Pool = m_pools[I];
            if (Pool.VMBase != nullptr)
            {
                ::XCore::HAL::FPlatformMemory::ReleaseVirtual(Pool.VMBase, kPerBinVMReservation);
                Pool.VMBase                  = nullptr;
                Pool.Metadata                = nullptr;
                Pool.UserCommittedBytes      = 0;
                Pool.SideTableCommittedBytes = 0;
                Pool.CentralFreeListHead     = nullptr;
                Pool.CrossThreadReclaimHead.store(nullptr, ::std::memory_order_relaxed);

                // Drain any remaining elements from the bounded MPSC
                // queue. The blocks live in the bin's VM range
                // (which we just released), so we don't free them
                // individually -- the VM release reclaimed the
                // memory wholesale. The TryDequeue calls below
                // simply move the queue's internal cursors so the
                // queue's destructor (when FMallocBinnedX itself is
                // destroyed) doesn't try to revisit them.
                FFreeBlock* Drained = nullptr;
                while (Pool.CrossThreadReclaimQueue.TryDequeue(Drained))
                {
                    // Discard; memory already released.
                    (void)Drained;
                }
            }

            // Clear the table slot too. Order doesn't matter for the
            // unsorted-flag races above.
            g_PoolMetadataTable[I] = nullptr;
        }

        // Release all outstanding large allocations.
        {
            ::XCore::HAL::FScopedMutexLock Lock(GetLargeAllocMutex());
            auto& Map = GetLargeAllocMap();
            for (auto& Entry : Map)
            {
                ::XCore::HAL::FPlatformMemory::ReleaseVirtual(Entry.second.VMBase, Entry.second.VMSize);
            }
            Map.clear();
        }

        m_initialized.store(false, ::std::memory_order_release);
    }

    // =====================================================================
    // FMallocBinnedX::HandleOOM -- the OOM policy dispatcher.
    // =====================================================================

    void* FMallocBinnedX::HandleOOM(::SIZE_T RequestedSize, FMemTag Tag) noexcept
    {
        const FOOMPolicy Policy = ::XCore::HAL::GetActiveOOMPolicy();
        switch (Policy)
        {
            case FOOMPolicy::ReturnNull:
                return nullptr;
            case FOOMPolicy::PanicSnapshot:
                // TODO(Phase 1c): emit the panic snapshot before aborting.
                //   * Write per-tag bytes from m_tagBytes to stderr.
                //   * Write FPlatformMemory::GetMemoryStats summary.
                //   * Write top-N leak buckets from FLeakTracker::CaptureReport.
                // For Phase 1b we abort with a tagged diagnostic and
                // document the snapshot path as a follow-up.
                {
                    char Buf[256];
                    ::std::snprintf(Buf, sizeof(Buf),
                                    "OOM in FMallocBinnedX (PanicSnapshot policy): tag=%s requested=%llu",
                                    GetMemTagName(Tag),
                                    static_cast<unsigned long long>(RequestedSize));
                    ::XCore::HAL::AbortWithMessage(Buf, __FILE__, __LINE__);
                }
                return nullptr;  // unreachable
            case FOOMPolicy::Abort:
            default:
                {
                    char Buf[256];
                    ::std::snprintf(Buf, sizeof(Buf),
                                    "OOM in FMallocBinnedX (Abort policy): tag=%s requested=%llu",
                                    GetMemTagName(Tag),
                                    static_cast<unsigned long long>(RequestedSize));
                    ::XCore::HAL::AbortWithMessage(Buf, __FILE__, __LINE__);
                }
                return nullptr;  // unreachable
        }
    }

    // =====================================================================
    // Per-tag accounting.
    //
    // Phase 1g Round 2 invariant: Malloc adds BinSize (== Meta->BinSize
    // == kBinSizeTable[BinIndex]) to the per-tag counter; Free subtracts
    // the same BinSize (recovered via GetPoolMetadata). The round-trip
    // cancels exactly because both add and subtract use the bin-rounded
    // size from the same source (the FPoolMetadata, which is immutable
    // post-Init). Per-tag bytes reflect bin-rounded capacity, not the
    // user's requested size; this is the §17.1 A1 "GetAllocatedBytes
    // (tag) == 0 after all-free" semantics.
    // =====================================================================

    void FMallocBinnedX::AddTagBytes(FMemTag Tag, ::int64 Delta) noexcept
    {
        const ::uint16 TagIndex = static_cast<::uint16>(Tag);
        if (TagIndex < kMemTagEngineSlotCount)
        {
            m_tagBytes[TagIndex].fetch_add(Delta, ::std::memory_order_relaxed);
        }
        else
        {
            // Plugin/User tags (Phase 1c TODO: secondary hash table).
            // For Phase 1b, account into slot 0 (Generic) so Dev builds
            // can still observe the total. The XBT Phase 1c scanner
            // will warn on plugin/user allocations once the table is
            // wired.
            m_tagBytes[0].fetch_add(Delta, ::std::memory_order_relaxed);
        }
    }

    ::uint64 FMallocBinnedX::GetAllocatedBytes(FMemTag Tag) const noexcept
    {
        const ::uint16 TagIndex = static_cast<::uint16>(Tag);
        if (TagIndex < kMemTagEngineSlotCount)
        {
            const ::int64 Val = m_tagBytes[TagIndex].load(::std::memory_order_relaxed);
            return Val < 0 ? 0 : static_cast<::uint64>(Val);
        }
        return 0;
    }

    // =====================================================================
    // PullBundleFromCentral / FlushBundleToCentral / DrainCrossThreadReclaim
    // =====================================================================

    void FMallocBinnedX::DrainCrossThreadReclaim(::uint32 BinIndex) noexcept
    {
        FPoolTable& Pool = m_pools[BinIndex];

        // Step 1: drain the bounded MPSC queue (Phase 1c fast path).
        // We accumulate the drained blocks into a local list, then
        // splice onto the central free list under Pool.Mutex.
        //
        // The drain loop runs single-consumer; no synchronisation
        // beyond TryDequeue's internal atomics. We bound the drain
        // count to the queue's capacity to avoid pathological
        // looping if producers are racing to refill the queue
        // (though that's a hot-path correctness concern, not a
        // drain-loop concern -- the queue's invariant is that
        // dequeue eventually catches up).
        FFreeBlock* QueueHead = nullptr;
        ::SIZE_T DrainCount = 0;
        const ::SIZE_T MaxDrain = FPoolTable::kReclaimQueueCapacity;
        FFreeBlock* Drained = nullptr;
        while (DrainCount < MaxDrain &&
               Pool.CrossThreadReclaimQueue.TryDequeue(Drained))
        {
            Drained->Next = QueueHead;
            QueueHead     = Drained;
            ++DrainCount;
        }

        // Step 2: drain the Treiber stack fallback (Phase 1b path,
        // used only when the bounded queue overflowed).
        FFreeBlock* StackHead =
            Pool.CrossThreadReclaimHead.exchange(nullptr, ::std::memory_order_acquire);

        // Fast-path: nothing drained.
        if (QueueHead == nullptr && StackHead == nullptr)
        {
            return;
        }

        // Splice both lists onto the central free list. Pool.Mutex
        // protects CentralFreeListHead.
        ::std::lock_guard<::std::mutex> Lock(Pool.Mutex);

        // Merge: append StackHead chain onto QueueHead chain (order
        // doesn't matter for allocator correctness -- the central
        // free list is unordered).
        if (QueueHead != nullptr)
        {
            FFreeBlock* QueueTail = QueueHead;
            while (QueueTail->Next != nullptr)
            {
                QueueTail = QueueTail->Next;
            }
            QueueTail->Next = StackHead;
            StackHead       = QueueHead;
        }

        // Find the tail of the merged list and splice onto the
        // central free list.
        if (StackHead != nullptr)
        {
            FFreeBlock* MergedTail = StackHead;
            while (MergedTail->Next != nullptr)
            {
                MergedTail = MergedTail->Next;
            }
            MergedTail->Next         = Pool.CentralFreeListHead;
            Pool.CentralFreeListHead = StackHead;
        }
    }

    FFreeBlock* FMallocBinnedX::PullBundleFromCentral(::uint32 BinIndex) noexcept
    {
        // First, drain any cross-thread reclaim list so the central
        // pool has every available block.
        DrainCrossThreadReclaim(BinIndex);

        FPoolTable& Pool = m_pools[BinIndex];
        const ::uint32 BinSize = Pool.BinSize;

        ::std::lock_guard<::std::mutex> Lock(Pool.Mutex);

        // If central is empty, commit a new slab of pages in the
        // user-data area, AND commit the matching side-table pages.
        if (Pool.CentralFreeListHead == nullptr)
        {
            // Slab size = LCM(BinSize, PageSize) -- gapless commit.
            // The page-aligned slab is also an exact multiple of
            // BinSize so consecutive slabs concatenate without
            // block-index gaps (the precondition for the side-table
            // indexing via (UserPtr - UserDataAreaBase) / BinSize).
            //
            // For power-of-2 bins <= PageSize: SlabSize == PageSize
            // (one page yields many blocks). For 48-byte bins:
            // SlabSize == 12288 (3 pages, 256 blocks). For the
            // largest bin (16 KiB > PageSize): SlabSize == 16384
            // (4 pages, 1 block).
            const ::SIZE_T PageSize = ::XCore::HAL::FPlatformMemory::GetPageSize();
            const ::SIZE_T SlabSize = ComputeSlabSize(BinSize, PageSize);

            // User-data area bounds check. The pool's user-data area
            // is finite (PoolSize - UserDataAreaOffset); when we
            // exhaust it OR exceed the side-table's block-count
            // capacity (MaxBlocks), the bin's pool is full. The
            // MaxBlocks clamp prevents allocations from writing past
            // the side-table's committed range (see ComputeMaxBlocks
            // above for the rationale).
            const ::SIZE_T UserDataAreaSize = Pool.Metadata->PoolSize -
                (reinterpret_cast<::UPTRINT>(Pool.Metadata->UserDataAreaBase) -
                 reinterpret_cast<::UPTRINT>(Pool.Metadata->PoolBaseAddr));
            const ::SIZE_T NewCommittedBytes = Pool.UserCommittedBytes + SlabSize;
            const ::SIZE_T NewBlockCount     = NewCommittedBytes / BinSize;
            if (NewCommittedBytes > UserDataAreaSize ||
                NewBlockCount > Pool.Metadata->MaxBlocks)
            {
                // Bin's pool exhausted. Hard limit.
                return nullptr;
            }

            // Commit the user-area slab.
            ::uint8* SlabStart = reinterpret_cast<::uint8*>(Pool.Metadata->UserDataAreaBase) + Pool.UserCommittedBytes;
            if (!::XCore::HAL::FPlatformMemory::CommitVirtual(SlabStart, SlabSize))
            {
                return nullptr;
            }

            // Commit the matching side-table pages. The block-index
            // range for this slab is [FirstBlock, FirstBlock + N) where
            // FirstBlock = Pool.UserCommittedBytes / BinSize and N =
            // SlabSize / BinSize. Side-table bytes for that range are
            // [FirstBlock * 2, (FirstBlock + N) * 2). Page-align both
            // ends and commit the delta beyond what's already
            // SideTableCommittedBytes.
            const ::SIZE_T FirstBlockIdx        = Pool.UserCommittedBytes / BinSize;
            const ::SIZE_T LastBlockIdxExclusive = (Pool.UserCommittedBytes + SlabSize) / BinSize;
            const ::SIZE_T NeededSideTableBytes = LastBlockIdxExclusive * sizeof(::uint16);
            const ::SIZE_T NeededSideTableBytesPageAligned = RoundUpToPage(NeededSideTableBytes);

            if (NeededSideTableBytesPageAligned > Pool.SideTableCommittedBytes)
            {
                const ::SIZE_T CommitFromOffset = Pool.SideTableCommittedBytes;
                const ::SIZE_T CommitSize        = NeededSideTableBytesPageAligned - CommitFromOffset;
                ::uint8* CommitAddr = reinterpret_cast<::uint8*>(Pool.Metadata->TagSideTable) + CommitFromOffset;
                if (!::XCore::HAL::FPlatformMemory::CommitVirtual(CommitAddr, CommitSize))
                {
                    // Side-table page commit failure. Roll back the
                    // user-area slab commit conceptually -- but we
                    // don't have an explicit decommit API path here
                    // (UE does the same: a partial commit is treated
                    // as a hard OOM). Return nullptr so the caller
                    // routes through HandleOOM.
                    return nullptr;
                }
                Pool.SideTableCommittedBytes = NeededSideTableBytesPageAligned;
                (void)FirstBlockIdx;  // suppress unused-variable warning in Shipping
            }

            Pool.UserCommittedBytes += SlabSize;

            // Carve the slab into BinSize chunks and push onto the
            // central free list. The chunks are linked in increasing
            // address order; the head is the lowest-address chunk.
            const ::SIZE_T NumChunks = SlabSize / BinSize;
            for (::SIZE_T I = 0; I < NumChunks; ++I)
            {
                FFreeBlock* Chunk = reinterpret_cast<FFreeBlock*>(
                    SlabStart + I * BinSize);
                Chunk->Next              = Pool.CentralFreeListHead;
                Pool.CentralFreeListHead = Chunk;
            }
        }

        // Pull up to kPerBinMaxCacheCount blocks for the TLS cache to
        // bundle-cache.
        FFreeBlock* BundleHead = nullptr;
        ::uint32 PulledCount   = 0;
        while (Pool.CentralFreeListHead != nullptr && PulledCount < kPerBinMaxCacheCount)
        {
            FFreeBlock* Block        = Pool.CentralFreeListHead;
            Pool.CentralFreeListHead = Block->Next;
            Block->Next              = BundleHead;
            BundleHead               = Block;
            ++PulledCount;
        }

        return BundleHead;
    }

    // =====================================================================
    // PoolIndexFromPtr -- the bin-index-only variant.
    //
    // Delegates to GetPoolMetadata and returns the BinIndex field, or
    // kLargeAllocBinIndex (= kBinCount) for "outside every bin's range".
    // Used by DumpUsageReport and tests; the hot-path Free uses
    // GetPoolMetadata directly so it can also read the side-table.
    // =====================================================================
    ::uint16 FMallocBinnedX::PoolIndexFromPtr(const void* UserPtr) const noexcept
    {
        const FPoolMetadata* Meta = GetPoolMetadata(UserPtr);
        if (Meta == nullptr)
        {
            return kLargeAllocBinIndex;
        }
        return Meta->BinIndex;
    }

    void FMallocBinnedX::FlushBundleToCentral(::uint32 BinIndex, FFreeBlock* Head, ::uint32 Count) noexcept
    {
        if (Head == nullptr || Count == 0)
        {
            return;
        }

        FPoolTable& Pool = m_pools[BinIndex];

        ::std::lock_guard<::std::mutex> Lock(Pool.Mutex);

        // Find tail of the flushed list.
        FFreeBlock* Tail = Head;
        while (Tail->Next != nullptr)
        {
            Tail = Tail->Next;
        }

        Tail->Next               = Pool.CentralFreeListHead;
        Pool.CentralFreeListHead = Head;
    }

    // =====================================================================
    // __ThreadExitFlushBundle -- thread-exit reclaim drain (Phase 1g
    // Fix B's MAJOR #2).
    //
    // Called from FTLSBinCache::CrossThreadFlushOnExit when an exiting
    // thread carries non-empty per-bin free-lists. We route the
    // exiting thread's blocks to the central pool's reclaim path so
    // they are not leaked.
    //
    // Implementation choice: enqueue each free-list NODE separately
    // onto the bounded MPSC reclaim queue. The owner thread's next
    // touch of this bin will drain the queue, sucking the blocks back
    // into the central free list for re-use. Falls back to the
    // Treiber-stack head if the bounded queue is full (TryEnqueue
    // returns false).
    //
    // Why per-node, not bulk-flush: the reclaim queue is the
    // documented cross-thread path; the central-pool lock is reserved
    // for the bin owner. The exiting thread is by definition NOT the
    // bin owner, so routing through the queue is correct. Per-node
    // cost: one TryEnqueue per block (lock-free CAS); the exit drain
    // is rare (per-thread, not per-allocation) so the per-node
    // overhead is acceptable.
    // =====================================================================
    void FMallocBinnedX::__ThreadExitFlushBundle(::uint32 BinIndex, FFreeBlock* Head, ::uint32 Count) noexcept
    {
        if (Head == nullptr || Count == 0)
        {
            return;
        }
        if (BinIndex >= kBinCount)
        {
            // Defensive: invalid bin index. The TLS cache only ever
            // calls us with bin indices it allocated under, but
            // defense-in-depth covers caller bugs.
            return;
        }
        if (!m_initialized.load(::std::memory_order_acquire))
        {
            // Allocator already shut down: the bin's VM range has been
            // released; routing blocks here would touch freed memory.
            // Drop silently (the engine is exiting and the process
            // reclamation handles the bytes anyway).
            return;
        }

        FPoolTable& Pool = m_pools[BinIndex];

        // Walk the chain, enqueueing each block individually. The
        // chain pointers (FFreeBlock::Next) are reset to nullptr as
        // we go so the per-block enqueue does not preserve stale
        // links into the bounded queue.
        FFreeBlock* Block = Head;
        while (Block != nullptr)
        {
            FFreeBlock* Next = Block->Next;
            Block->Next = nullptr;

            if (XPACT_LIKELY(Pool.CrossThreadReclaimQueue.TryEnqueue(Block)))
            {
                // Successful enqueue: the owner thread will drain on
                // its next touch of this bin.
            }
            else
            {
                // Queue full: fall through to the Treiber-stack head
                // (Section 8.1 fix B-C4 fallback). The Treiber stack
                // is lock-free, unbounded, and guarantees forward
                // progress; the trade is increased latency on the
                // drain side.
                FFreeBlock* OldHead =
                    Pool.CrossThreadReclaimHead.load(::std::memory_order_relaxed);
                do
                {
                    Block->Next = OldHead;
                }
                while (!Pool.CrossThreadReclaimHead.compare_exchange_weak(
                            OldHead, Block,
                            ::std::memory_order_acq_rel,
                            ::std::memory_order_relaxed));
            }

            Block = Next;
        }
    }

    // =====================================================================
    // MallocLarge / FreeLarge -- the large-alloc path.
    //
    // Phase 1g Round 2: the large-alloc path no longer carries an
    // intra-block FBlockHeader; the user pointer is the page-aligned
    // start of the OS-allocated VM range, with the Align-honouring
    // adjustment applied. FreeLarge looks up the user pointer in the
    // GetLargeAllocMap() to recover VMBase + VMSize + UserSize + Tag.
    //
    // For Align > 16, we reserve extra slack so the user pointer can be
    // shifted forward to satisfy the alignment. The map records VMBase
    // (the ReserveVirtual return; possibly different from the user
    // pointer) and VMSize (the page-rounded reservation size) so
    // ReleaseVirtual gets the right arguments at Free time.
    // =====================================================================

    void* FMallocBinnedX::MallocLarge(::SIZE_T Size, ::SIZE_T Align, FMemTag Tag) noexcept
    {
        // Reserve enough VM for the user data + alignment slack. Page-
        // rounded. For Align <= PageSize (the common case), no slack is
        // needed; for Align > PageSize, we reserve an extra Align bytes
        // and shift the user pointer forward.
        const ::SIZE_T VMSize = RoundUpToPage(Size + Align);

        void* VMBase = ::XCore::HAL::FPlatformMemory::ReserveVirtual(VMSize);
        if (VMBase == nullptr)
        {
            return HandleOOM(Size, Tag);
        }

        if (!::XCore::HAL::FPlatformMemory::CommitVirtual(VMBase, VMSize))
        {
            ::XCore::HAL::FPlatformMemory::ReleaseVirtual(VMBase, VMSize);
            return HandleOOM(Size, Tag);
        }

        // Align the user pointer forward to honour the requested Align.
        // The VMBase from ReserveVirtual is at least page-aligned (4 KiB
        // on all targets), so for Align <= 4 KiB the user pointer is
        // VMBase directly.
        const ::UPTRINT VMBaseInt    = reinterpret_cast<::UPTRINT>(VMBase);
        const ::UPTRINT AlignedUser  = (VMBaseInt + Align - 1) & ~(Align - 1);
        void* UserPtr                = reinterpret_cast<void*>(AlignedUser);

        // Record in the large-alloc map.
        {
            ::XCore::HAL::FScopedMutexLock Lock(GetLargeAllocMutex());
            GetLargeAllocMap()[UserPtr] = FLargeAllocRecord{ VMBase, VMSize, Size, Tag };
        }

        AddTagBytes(Tag, static_cast<::int64>(Size));

        // Invoke malloc hook (FLeakTracker) if installed.
        if (FMallocHook Hook = m_mallocHook.load(::std::memory_order_relaxed))
        {
            Hook(UserPtr, Size, Tag);
        }

        return UserPtr;
    }

    void FMallocBinnedX::FreeLarge(void* UserPtr) noexcept
    {
        FLargeAllocRecord Record;
        {
            ::XCore::HAL::FScopedMutexLock Lock(GetLargeAllocMutex());
            auto& Map = GetLargeAllocMap();
            auto Iter = Map.find(UserPtr);
            if (Iter == Map.end())
            {
                // Double-free or alien pointer. Debug aborts; Shipping
                // is UB (canary patterns not implemented at Phase 1b).
                XPACT_CHECK(false);
                return;
            }
            Record = Iter->second;
            Map.erase(Iter);
        }

        // Invoke free hook (FLeakTracker) if installed.
        if (FFreeHook Hook = m_freeHook.load(::std::memory_order_relaxed))
        {
            Hook(UserPtr);
        }

        AddTagBytes(Record.Tag, -static_cast<::int64>(Record.UserSize));
        ::XCore::HAL::FPlatformMemory::ReleaseVirtual(Record.VMBase, Record.VMSize);
    }

    // =====================================================================
    // Malloc / Realloc / Free top-level.
    //
    // Phase 1g Round 2 flow:
    //
    //   Malloc(Size, Align, Tag):
    //     1. SizeToBinIndex -> BinIndex (or kLargeAllocBinIndex).
    //     2. If large: MallocLarge.
    //     3. Else: pull block from TLS cache (refill from central if
    //        empty).
    //     4. Compute BlockIndex = (BlockPtr - UserDataAreaBase) / BinSize.
    //     5. Write Tag into Pool.Metadata->TagSideTable[BlockIndex].
    //     6. Account BinSize against tag.
    //     7. Return block pointer as user pointer (no header offset).
    //
    //   Free(UserPtr):
    //     1. GetPoolMetadata(UserPtr) -> Meta (or nullptr for large).
    //     2. If null: FreeLarge.
    //     3. Else: BlockIndex; read Tag from Meta->TagSideTable; account
    //        -BinSize; push block onto reclaim queue.
    // =====================================================================

    void* FMallocBinnedX::Malloc(::SIZE_T Size, ::SIZE_T Align, FMemTag Tag) noexcept
    {
        // Phase ladder contract (Section 1.5): FMemory::__Init runs at
        // PreStaticInit before any user-tier static constructor. If a
        // Malloc fires before __Init, the allocator's pools are zero-
        // initialised and the allocation cannot proceed. Debug/Dev
        // aborts cleanly; Shipping trusts the phase ladder discipline
        // (call-site bug, not a runtime concern).
        XPACT_CHECK(m_initialized.load(::std::memory_order_acquire));

        // Sim-path no-alloc scope guard (Section 4.3).
        XPACT_CHECK(!::XCore::HAL::FMemory::IsNoAllocScopeActive());

        // Normalize alignment: minimum 8 bytes; must be power of two.
        if (Align < 8)
        {
            Align = 8;
        }
        XPACT_CHECK((Align & (Align - 1)) == 0);  // power-of-two check

        // Defer Size == 0 to a 1-byte allocation; the result is a
        // distinct non-null pointer that may be Freed.
        const ::SIZE_T ReqSize = (Size == 0) ? 1 : Size;

        const ::uint16 BinIndex = SizeToBinIndex(ReqSize, Align);

        if (BinIndex == kLargeAllocBinIndex)
        {
            return MallocLarge(ReqSize, Align, Tag);
        }

        // Small-bin fast path. Pull from TLS cache; if empty, refill
        // from central.
        FTLSBinCache& Cache = GetThreadCache();

        FFreeBlock* Block = Cache.FreeListHead[BinIndex];
        if (XPACT_UNLIKELY(Block == nullptr))
        {
            // Cache empty: pull a bundle from central.
            FFreeBlock* Bundle = PullBundleFromCentral(BinIndex);
            if (Bundle == nullptr)
            {
                return HandleOOM(Size, Tag);
            }

            // Count how many blocks in the bundle.
            ::uint32 Count = 0;
            FFreeBlock* Iter = Bundle;
            while (Iter != nullptr)
            {
                ++Count;
                Iter = Iter->Next;
            }

            Cache.FreeListHead[BinIndex]  = Bundle;
            Cache.FreeListCount[BinIndex] = Count;
            Block                         = Cache.FreeListHead[BinIndex];
        }

        // Pop head from cache.
        Cache.FreeListHead[BinIndex] = Block->Next;
        --Cache.FreeListCount[BinIndex];

        // User pointer == block start. Phase 1g Round 2: no header
        // offset. The block is BinSize-aligned (per kBinSizeTable
        // 16-alignment invariant + page-aligned user-data area base).
        void* UserPtr = reinterpret_cast<void*>(Block);

        // Write the FMemTag into the pool's side-table at this block's
        // index. The side-table page covering this block was committed
        // by PullBundleFromCentral / first slab commit, so the write
        // is valid.
        FPoolMetadata* Meta = m_pools[BinIndex].Metadata;
        const ::SIZE_T BlockIndex = BlockIndexFromUserPtr(Meta, UserPtr);
        XPACT_CHECK(BlockIndex < Meta->MaxBlocks);
        Meta->TagSideTable[BlockIndex] = static_cast<::uint16>(Tag);

        // Per-tag accounting: add the bin's full size (the user-visible
        // capacity). Symmetric with Free's BinSize subtraction so the
        // round-trip is exactly zero.
        AddTagBytes(Tag, static_cast<::int64>(Meta->BinSize));

        // Invoke malloc hook (FLeakTracker). The hook receives the
        // user's requested size for callsite attribution; the per-tag
        // accounting uses the bin-rounded BinSize.
        if (FMallocHook Hook = m_mallocHook.load(::std::memory_order_relaxed))
        {
            Hook(UserPtr, Size, Tag);
        }

        return UserPtr;
    }

    void FMallocBinnedX::Free(void* UserPtr) noexcept
    {
        if (UserPtr == nullptr)
        {
            return;  // Free(nullptr) is a no-op per the spec body.
        }

        // Recover the pool metadata via binary search. If null, the
        // pointer is not in any small-bin range -- either a large
        // alloc or an alien pointer.
        const FPoolMetadata* Meta = GetPoolMetadata(UserPtr);
        if (Meta == nullptr)
        {
            // Invoke free hook BEFORE FreeLarge for parity with the
            // small-bin path (the hook expects to see the pointer
            // still in the live allocator map).
            if (FFreeHook Hook = m_freeHook.load(::std::memory_order_relaxed))
            {
                Hook(UserPtr);
            }
            FreeLarge(UserPtr);
            return;
        }

        // Defensive: BlockIndex bounds. A malformed user pointer
        // (e.g., interior pointer mid-block) would yield a wrong
        // BlockIndex; the check catches gross cases but cannot detect
        // intra-block aliasing (that requires canary patterns; Phase
        // 1g+ work).
        const ::SIZE_T BlockIndex = BlockIndexFromUserPtr(Meta, UserPtr);
        XPACT_CHECK(BlockIndex < Meta->MaxBlocks);

        // Defensive: the user pointer must be at the start of a block,
        // not an interior pointer. Interior pointers would still pass
        // the BlockIndex < MaxBlocks check but would corrupt the side-
        // table indexing. Verify alignment to BinSize.
        const ::UPTRINT P    = reinterpret_cast<::UPTRINT>(UserPtr);
        const ::UPTRINT Base = reinterpret_cast<::UPTRINT>(Meta->UserDataAreaBase);
        XPACT_CHECK(((P - Base) % Meta->BinSize) == 0);

        // Read the tag from the side-table.
        const FMemTag Tag = static_cast<FMemTag>(Meta->TagSideTable[BlockIndex]);

        // Invoke free hook (FLeakTracker) BEFORE we touch the block
        // so the tracker sees the still-valid pointer.
        if (FFreeHook Hook = m_freeHook.load(::std::memory_order_relaxed))
        {
            Hook(UserPtr);
        }

        // Per-tag accounting: subtract the bin's full size (symmetric
        // with Malloc's BinSize addition; round-trip cancels exactly).
        AddTagBytes(Tag, -static_cast<::int64>(Meta->BinSize));

        // Push the freed block back into circulation.
        //
        // SPEC CONTRACT (Section 4.2 + Section 8.1 fix B-C4): cross-
        // thread Free routes to the owner-thread MPSC reclaim queue
        // (bounded, no per-Free allocation).
        //
        // PHASE 1c IMPLEMENTATION (Section 8.1 fix B-C4 landed):
        // every Free routes through the bounded MPSC queue first.
        // On queue-full, we fall back to the Treiber-stack path
        // (which trades latency for guaranteed forward progress;
        // the spec body explicitly names this as the back-pressure
        // policy).
        //
        //   1. TryEnqueue onto Pool.CrossThreadReclaimQueue. In
        //      normal operation the consumer drains continuously
        //      and the queue stays nearly empty, so TryEnqueue
        //      succeeds.
        //   2. If TryEnqueue returns false (queue full -- rare),
        //      fall back to the Treiber stack push (Phase 1b path,
        //      retained).
        //   3. The consumer (the bin's owner; effectively the next
        //      thread to PullBundleFromCentral on this bin) drains
        //      both the bounded queue AND the Treiber-stack
        //      fallback before pulling new bundles.
        //
        // TODO(Phase 1d): per-slab owner-thread tracking so same-
        // thread Frees skip the queue and hit the local TLS cache
        // directly. The current scheme treats every Free as a
        // cross-thread Free, paying one TryEnqueue per Free.
        const ::uint16 BinIndex = Meta->BinIndex;
        FPoolTable& Pool = m_pools[BinIndex];

        // Reset the block to FFreeBlock layout. The block's first
        // 8 bytes become FFreeBlock::Next; the rest is unused while
        // the block sits on the free list. The previous tag stored
        // in the side-table is now stale; we leave it (a subsequent
        // Malloc on the same block will overwrite it).
        FFreeBlock* Block = reinterpret_cast<FFreeBlock*>(UserPtr);
        Block->Next = nullptr;

        // Fast path: bounded MPSC.
        if (XPACT_LIKELY(Pool.CrossThreadReclaimQueue.TryEnqueue(Block)))
        {
            return;
        }

        // Slow-path fallback: Treiber stack (Phase 1b path).
        // Reached only when the bounded queue is full -- rare.
        FFreeBlock* OldHead = Pool.CrossThreadReclaimHead.load(::std::memory_order_relaxed);
        do
        {
            Block->Next = OldHead;
        } while (!Pool.CrossThreadReclaimHead.compare_exchange_weak(
                     OldHead, Block,
                     ::std::memory_order_release,
                     ::std::memory_order_relaxed));
    }

    void* FMallocBinnedX::Realloc(void* Ptr, ::SIZE_T NewSize, ::SIZE_T Align, FMemTag Tag) noexcept
    {
        // Null pointer => Malloc.
        if (Ptr == nullptr)
        {
            return Malloc(NewSize, Align, Tag);
        }

        // Zero size => Free + return null.
        if (NewSize == 0)
        {
            Free(Ptr);
            return nullptr;
        }

        // Recover the pool metadata to determine current bin / capacity.
        const FPoolMetadata* Meta = GetPoolMetadata(Ptr);

        // Determine current capacity + old user-side bound for memcpy.
        // Under the Phase 1g Round 2 layout, the user-visible capacity
        // IS the bin size (or VM-allocated UserSize for large allocs).
        ::SIZE_T CurrentCapacity;
        ::uint16 BinIndex;
        if (Meta == nullptr)
        {
            // Large alloc. Recover via the sidecar map.
            ::XCore::HAL::FScopedMutexLock Lock(GetLargeAllocMutex());
            auto& Map = GetLargeAllocMap();
            auto Iter = Map.find(Ptr);
            if (Iter == Map.end())
            {
                XPACT_CHECK(false);
                return nullptr;
            }
            CurrentCapacity = Iter->second.UserSize;
            BinIndex        = kLargeAllocBinIndex;
        }
        else
        {
            CurrentCapacity = Meta->BinSize;
            BinIndex        = Meta->BinIndex;
        }

        // If the new size fits in the current capacity, return in-place.
        // For small bins we re-tag (the user may be reusing the block
        // under a different attribution); the tag write goes through
        // the side-table.
        if (NewSize <= CurrentCapacity)
        {
            if (Meta != nullptr)
            {
                const ::SIZE_T BlockIndex = BlockIndexFromUserPtr(Meta, Ptr);
                XPACT_CHECK(BlockIndex < Meta->MaxBlocks);
                const FMemTag OldTag = static_cast<FMemTag>(Meta->TagSideTable[BlockIndex]);

                // Re-tagging accounting: the bin size doesn't change,
                // so we move BinSize from the old tag to the new tag.
                if (OldTag != Tag)
                {
                    AddTagBytes(OldTag, -static_cast<::int64>(Meta->BinSize));
                    AddTagBytes(Tag,     static_cast<::int64>(Meta->BinSize));
                    Meta->TagSideTable[BlockIndex] = static_cast<::uint16>(Tag);
                }
            }
            else
            {
                // Large-alloc in-place resize: update the sidecar
                // map's UserSize so Free sees the new value (and the
                // per-tag accounting is updated to the new size).
                ::XCore::HAL::FScopedMutexLock Lock(GetLargeAllocMutex());
                auto& Map  = GetLargeAllocMap();
                auto  Iter = Map.find(Ptr);
                if (Iter != Map.end())
                {
                    AddTagBytes(Iter->second.Tag, -static_cast<::int64>(Iter->second.UserSize));
                    AddTagBytes(Tag,               static_cast<::int64>(NewSize));
                    Iter->second.UserSize = NewSize;
                    Iter->second.Tag      = Tag;
                }
            }
            (void)BinIndex;  // suppress unused-warning in Release
            return Ptr;
        }

        // Grow path: Malloc new + Memcpy + Free old.
        //
        // Copy size = min(NewSize, OldCapacity). For small bins, the
        // user only initialised up to their requested size, but we no
        // longer track per-allocation requested-size; the safe lower
        // bound is OldCapacity (the bin size). Over-copying into the
        // new block reads bytes the user didn't touch -- those bytes
        // are all-zero (page-commit zero-fills) or are stale data
        // from a previous user of the same block. The latter is a
        // potential "info leak forward" concern, but it mirrors UE
        // Binned3's behaviour (MallocBinned3.cpp:870 also copies
        // min(NewSize, BinSize)). Phase 2 can add an explicit
        // user-size sidecar if the info-leak concern outweighs the
        // 0-overhead-per-block design goal.
        const ::SIZE_T CopyBytes = (CurrentCapacity < NewSize) ? CurrentCapacity : NewSize;

        void* NewPtr = Malloc(NewSize, Align, Tag);
        if (NewPtr == nullptr)
        {
            // OOM under ReturnNull; keep the old block intact and
            // return null.
            return nullptr;
        }

        ::std::memcpy(NewPtr, Ptr, CopyBytes);
        Free(Ptr);
        return NewPtr;
    }

    // =====================================================================
    // SetMallocHook / SetFreeHook
    // =====================================================================

    void FMallocBinnedX::SetMallocHook(FMallocHook Hook) noexcept
    {
        m_mallocHook.store(Hook, ::std::memory_order_relaxed);
    }

    void FMallocBinnedX::SetFreeHook(FFreeHook Hook) noexcept
    {
        m_freeHook.store(Hook, ::std::memory_order_relaxed);
    }

} // namespace XCore::HAL
