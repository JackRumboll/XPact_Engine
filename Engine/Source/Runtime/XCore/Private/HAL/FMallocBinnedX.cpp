// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FMallocBinnedX.cpp -- the central allocator body.
// =====================================================================
//
// XCore-4a Rev 3, Section 4. See FMallocBinnedX.h for the architecture
// header and the UE source citations.
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
#include "HAL/FOOMPolicy.h"
#include "HAL/FPlatformMemory.h"

#include <atomic>
#include <cstring>           // ::std::memcpy / memset
#include <mutex>
#include <unordered_map>     // large-alloc map (Phase 1b)

namespace XCore::HAL
{
    // =====================================================================
    // Large-alloc map (Phase 1b implementation).
    //
    // Maps user-pointer (header + 4) to the original ReserveVirtual base
    // + size + tag. Free uses this map to find the right ReleaseVirtual
    // arguments for a large alloc.
    //
    // Phase 1b: std::unordered_map under a global mutex. Phase 1c will
    // swap to a custom intrusive hash table if benchmarks show this
    // is a bottleneck.
    // =====================================================================

    namespace
    {
        struct FLargeAllocRecord
        {
            void*    VMBase;     // ReserveVirtual return; for Release
            ::SIZE_T VMSize;     // bytes reserved (page-aligned, >= header + Size)
            ::SIZE_T UserSize;   // requested allocation size (without header)
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

        ::std::mutex& GetLargeAllocMutex() noexcept
        {
            static ::std::mutex Mutex;
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

        // Compute the user pointer from a block start address.
        //
        // The user pointer is at offset kUserOffset (= 16) from the
        // block start; the 4-byte header occupies the last 4 bytes
        // before the user pointer (offsets 12..15 inside the block).
        [[nodiscard]] XPACT_FORCEINLINE void* BlockStartToUser(void* BlockStart) noexcept
        {
            return reinterpret_cast<void*>(reinterpret_cast<::uint8*>(BlockStart) + kUserOffset);
        }

        // Compute the block start address from a user pointer.
        //
        // BlockStart = UserPtr - kUserOffset. The 4-byte header lives
        // at BlockStart + 12 (i.e. UserPtr - sizeof(FBlockHeader)).
        [[nodiscard]] XPACT_FORCEINLINE void* UserToBlockStart(void* User) noexcept
        {
            return reinterpret_cast<void*>(reinterpret_cast<::uint8*>(User) - kUserOffset);
        }

        // Compute the header location from a user pointer.
        //
        // Header sits at UserPtr - sizeof(FBlockHeader) (= UserPtr - 4).
        [[nodiscard]] XPACT_FORCEINLINE FBlockHeader* UserToHeader(void* User) noexcept
        {
            return reinterpret_cast<FBlockHeader*>(reinterpret_cast<::uint8*>(User) - sizeof(FBlockHeader));
        }
    } // anonymous

    // =====================================================================
    // SizeToBinIndex / BinIndexToBinSize static helpers.
    // =====================================================================

    ::uint16 FMallocBinnedX::SizeToBinIndex(::SIZE_T Size, ::SIZE_T Align) noexcept
    {
        // The block layout (FMallocBinnedX.h FBlockHeader comment):
        //   * Block start (BinSize-aligned, always >= 8-aligned).
        //   * Bytes [0..11] padding (becomes FFreeBlock::Next when free).
        //   * Bytes [12..15] FBlockHeader (Tag + BinIndex).
        //   * Bytes [16..BinSize-1] user data, always 16-aligned because
        //     every kBinSizeTable bin size is a multiple of 16.
        //
        // Align <= 16 routes through the small-bin path; Align > 16
        // requires a large-alloc (the bin path's fixed kUserOffset = 16
        // cannot honour Align > 16 alignment).
        //
        // TODO(Phase 1c): switch to PoolIndexFromPtr-style tag recovery
        // so the kUserOffset overhead vanishes and the small-bin path
        // can honour arbitrary Align via per-allocation slot lookup.
        if (Align > kUserOffset)
        {
            return kLargeAllocBinIndex;
        }

        const ::SIZE_T NeededSize = kUserOffset + Size;

        if (NeededSize > kLargeAllocThreshold)
        {
            return kLargeAllocBinIndex;
        }

        // Linear search over kBinSizeTable. Phase 1c may swap to a
        // pre-computed lookup table (256-byte LUT for sizes <= 8 KiB);
        // for Phase 1b a 56-step compare loop is acceptable.
        for (::uint16 I = 0; I < kBinCount; ++I)
        {
            if (static_cast<::SIZE_T>(kBinSizeTable[I]) >= NeededSize)
            {
                return I;
            }
        }

        // Should not reach here: NeededSize <= kLargeAllocThreshold but
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

    void FMallocBinnedX::Init() noexcept
    {
        // Already initialized? Idempotent (Section 1.5 invariant:
        // PreStaticInit hooks may be called once; defensive double-call
        // protection here).
        if (m_initialized.load(::std::memory_order_acquire))
        {
            return;
        }

        // Reserve VM range per bin.
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

            Pool.CommittedBytes      = 0;
            Pool.CentralFreeListHead = nullptr;
            Pool.CrossThreadReclaimHead.store(nullptr, ::std::memory_order_relaxed);

            // Initialise the bounded MPSC reclaim queue (Section 8.1
            // fix B-C4 swap-in landed in Phase 1c). The Vyukov
            // invariant (slot[i].seq = i) is set here at Init()-time;
            // the queue's default constructor leaves slot sequences
            // at zero (constexpr-required) which would corrupt the
            // algorithm's empty-vs-full distinction without this
            // call.
            Pool.CrossThreadReclaimQueue.Initialize();
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

        // Release all per-bin VM reservations.
        for (::uint32 I = 0; I < kBinCount; ++I)
        {
            FPoolTable& Pool = m_pools[I];
            if (Pool.VMBase != nullptr)
            {
                ::XCore::HAL::FPlatformMemory::ReleaseVirtual(Pool.VMBase, kPerBinVMReservation);
                Pool.VMBase              = nullptr;
                Pool.CommittedBytes      = 0;
                Pool.CentralFreeListHead = nullptr;
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
        }

        // Release all outstanding large allocations.
        {
            ::std::lock_guard<::std::mutex> Lock(GetLargeAllocMutex());
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

        // If central is empty, commit a new slab of pages.
        if (Pool.CentralFreeListHead == nullptr)
        {
            // Commit one page (or one bin-sized chunk, whichever is
            // larger). For small bins, one page yields many blocks;
            // for the largest bin (16 KiB) we commit 4 pages so we
            // get at least 1 full block (and typically more).
            const ::SIZE_T PageSize = ::XCore::HAL::FPlatformMemory::GetPageSize();
            const ::SIZE_T SlabSize = (BinSize > PageSize) ? RoundUpToAlign(BinSize * 4, PageSize) : PageSize;

            if (Pool.CommittedBytes + SlabSize > kPerBinVMReservation)
            {
                // Bin's reserved VM exhausted. This is a hard limit;
                // bumping kPerBinVMReservation is the resolution.
                return nullptr;
            }

            void* SlabStart = reinterpret_cast<::uint8*>(Pool.VMBase) + Pool.CommittedBytes;
            if (!::XCore::HAL::FPlatformMemory::CommitVirtual(SlabStart, SlabSize))
            {
                return nullptr;
            }

            Pool.CommittedBytes += SlabSize;

            // Carve the slab into BinSize chunks and push onto the
            // central free list. The chunks are linked in increasing
            // address order; the head is the lowest-address chunk.
            const ::SIZE_T NumChunks = SlabSize / BinSize;
            for (::SIZE_T I = 0; I < NumChunks; ++I)
            {
                FFreeBlock* Chunk = reinterpret_cast<FFreeBlock*>(
                    reinterpret_cast<::uint8*>(SlabStart) + I * BinSize);
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
    // MallocLarge / FreeLarge -- the large-alloc path.
    // =====================================================================

    void* FMallocBinnedX::MallocLarge(::SIZE_T Size, ::SIZE_T Align, FMemTag Tag) noexcept
    {
        // Reserve + commit a VM range large enough for header + user data +
        // alignment slack. Page-rounded.
        const ::SIZE_T HeaderedSize = Size + sizeof(FBlockHeader);
        const ::SIZE_T VMSize       = RoundUpToPage(HeaderedSize + Align);

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

        // Place the header so the user pointer is Align-aligned.
        const ::UPTRINT VMBaseInt   = reinterpret_cast<::UPTRINT>(VMBase);
        const ::UPTRINT EarliestUser = VMBaseInt + sizeof(FBlockHeader);
        const ::UPTRINT AlignedUser  = (EarliestUser + Align - 1) & ~(Align - 1);
        FBlockHeader* Header        = reinterpret_cast<FBlockHeader*>(AlignedUser - sizeof(FBlockHeader));
        void* UserPtr               = reinterpret_cast<void*>(AlignedUser);

        Header->Tag      = Tag;
        Header->BinIndex = kLargeAllocBinIndex;
        // UserSize is capped at uint32 max (~4 GiB); larger allocs use
        // the sidecar map's UserSize for accounting at Free. For
        // realistic Quest 3 workloads (16 GiB max process) no single
        // alloc reaches 4 GiB so this is a theoretical concern.
        Header->UserSize = (Size > ~::uint32(0)) ? 0u : static_cast<::uint32>(Size);

        // Record in the large-alloc map.
        {
            ::std::lock_guard<::std::mutex> Lock(GetLargeAllocMutex());
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
            ::std::lock_guard<::std::mutex> Lock(GetLargeAllocMutex());
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

        // Compute the user pointer + header location.
        //
        // Block start = `Block`. User pointer is at Block + kUserOffset.
        // Header occupies the 4 bytes immediately before the user
        // pointer (offsets 12..15 inside the block).
        //
        // Per the kBinSizeTable static_assert (all bins multiple of 16)
        // and the slab-aligned-to-page property (every slab start is
        // page-aligned, hence >= 16-aligned), every Block start is
        // 16-aligned, so Block + kUserOffset is 16-aligned. Align <=
        // 16 is honoured; Align > 16 is rejected by SizeToBinIndex
        // (routed to the large-alloc path).
        void* BlockStart = reinterpret_cast<void*>(Block);
        void* UserPtr    = BlockStartToUser(BlockStart);
        FBlockHeader* Header = UserToHeader(UserPtr);

        // Write the header (Tag + BinIndex + UserSize).
        Header->Tag      = Tag;
        Header->BinIndex = BinIndex;
        Header->UserSize = static_cast<::uint32>(Size);

        AddTagBytes(Tag, static_cast<::int64>(Size));

        // Invoke malloc hook (FLeakTracker).
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

        // Read header to determine small-bin vs large-alloc and the
        // tag + size. Header lives at UserPtr - sizeof(FBlockHeader)
        // (= block + 8).
        FBlockHeader* Header = UserToHeader(UserPtr);
        const ::uint16 BinIndex  = Header->BinIndex;
        const FMemTag  Tag       = Header->Tag;
        const ::uint32 UserSize  = Header->UserSize;

        // Invoke free hook (FLeakTracker) BEFORE we touch the block
        // so the tracker sees the still-valid header.
        if (FFreeHook Hook = m_freeHook.load(::std::memory_order_relaxed))
        {
            Hook(UserPtr);
        }

        if (BinIndex == kLargeAllocBinIndex)
        {
            FreeLarge(UserPtr);
            return;
        }

        // Small-bin path. Subtract the exact UserSize stored in the
        // header at Malloc time -- per-tag accounting is exact across
        // round-trip (Section 4.6 acceptance: "any sequence ending
        // all-Free leaves GetAllocatedBytes(tag)==0").
        AddTagBytes(Tag, -static_cast<::int64>(UserSize));

        // Push the freed block back into circulation.
        //
        // SPEC CONTRACT (Section 4.2 + Section 8.1 fix B-C4): cross-
        // thread Free routes to the owner-thread MPSC reclaim queue
        // (bounded, no per-Free allocation).
        //
        // PHASE 1C IMPLEMENTATION (Section 8.1 fix B-C4 landed):
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
        //
        // Reset the block to FFreeBlock layout. The block's first
        // 8 bytes are FFreeBlock::Next; this overwrites the padding
        // bytes 0..7 of the block start. The header at bytes 12..15
        // is overwritten too (we re-cast the block start to
        // FFreeBlock so the Next pointer lands at offsets 0..7).
        FPoolTable& Pool  = m_pools[BinIndex];
        FFreeBlock* Block = reinterpret_cast<FFreeBlock*>(UserToBlockStart(UserPtr));

        // Block->Next is not used by the bounded queue (the queue
        // stores FFreeBlock* directly); but we reset it to nullptr
        // so a subsequent fallback-to-Treiber push has a clean
        // starting state.
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

        FBlockHeader* Header    = UserToHeader(Ptr);
        const ::uint16 BinIndex = Header->BinIndex;

        // Determine current capacity (user-visible bytes inside the
        // bin / VM block).
        ::SIZE_T CurrentCapacity;
        if (BinIndex == kLargeAllocBinIndex)
        {
            ::std::lock_guard<::std::mutex> Lock(GetLargeAllocMutex());
            auto& Map = GetLargeAllocMap();
            auto Iter = Map.find(Ptr);
            if (Iter == Map.end())
            {
                XPACT_CHECK(false);
                return nullptr;
            }
            CurrentCapacity = Iter->second.UserSize;
        }
        else
        {
            CurrentCapacity = kBinSizeTable[BinIndex] - kUserOffset;
        }

        // If the new size fits in the current capacity, in-place succeed.
        // (Note: for shrinks within the same bin we keep the block; for
        // shrinks crossing a bin boundary we could re-fit to a smaller
        // bin but that requires a Malloc + Memcpy + Free and is rarely
        // worth it for small shrinks. Phase 1c may add a heuristic.)
        if (NewSize <= CurrentCapacity)
        {
            // In-place: update the per-tag accounting to reflect the
            // new requested size, and update the tag + UserSize fields
            // in the header so subsequent Free unwinds the new size
            // correctly. Spec body: "the tag on the existing block is
            // updated".
            if (BinIndex != kLargeAllocBinIndex)
            {
                const ::uint32 OldSize = Header->UserSize;
                const FMemTag  OldTag  = Header->Tag;
                AddTagBytes(OldTag, -static_cast<::int64>(OldSize));
                AddTagBytes(Tag,    static_cast<::int64>(NewSize));

                Header->Tag      = Tag;
                Header->UserSize = static_cast<::uint32>(NewSize);
            }
            else
            {
                // Large-alloc in-place resize: update the sidecar
                // map's UserSize so Free sees the new value.
                ::std::lock_guard<::std::mutex> Lock(GetLargeAllocMutex());
                auto& Map  = GetLargeAllocMap();
                auto  Iter = Map.find(Ptr);
                if (Iter != Map.end())
                {
                    AddTagBytes(Iter->second.Tag, -static_cast<::int64>(Iter->second.UserSize));
                    AddTagBytes(Tag,               static_cast<::int64>(NewSize));
                    Iter->second.UserSize = NewSize;
                    Iter->second.Tag      = Tag;
                }
                Header->Tag = Tag;  // header carries the most recent tag too
            }
            return Ptr;
        }

        // Grow path: Malloc new + Memcpy + Free old.
        //
        // Copy size = min(OldUserSize, NewSize). The user only
        // initialised up to OldUserSize bytes of the old block; the
        // OldUserSize-to-CurrentCapacity tail is uninit-padding-area
        // that we MUST NOT propagate (could leak stale data of a
        // previous allocation in the same bin if FLeakTracker's stomp
        // mode isn't on).
        const ::SIZE_T OldUserSize = (BinIndex == kLargeAllocBinIndex)
                                         ? CurrentCapacity   // recorded in sidecar
                                         : Header->UserSize;
        const ::SIZE_T CopyBytes   = (OldUserSize < NewSize) ? OldUserSize : NewSize;

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
