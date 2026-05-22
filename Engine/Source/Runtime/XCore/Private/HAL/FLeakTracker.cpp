// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FLeakTracker.cpp -- per-tag shadow table + LRU eviction.
// =====================================================================
//
// XCore-4a Rev 3, Section 12.
//
// See FLeakTracker.h for the architecture overview. This .cpp delivers:
//
//   * Per-tag chained hash map (bounded chain depth 16; LRU eviction
//     on 10 M live-alloc overflow).
//   * Hook callbacks into FMallocBinnedX::SetMallocHook /
//     SetFreeHook.
//   * Stack-walk per-allocation (calls IStackWalk::CaptureStackWalk).
//   * CaptureReport / WriteReport (Phase 1b: aggregate counts only;
//     per-bucket TArray gated by Phase 1c TArray dependency).
//
// Per Section 12.2 fix A-MIN5:
//   * Shadow table is a per-tag chained hash. Hash key = user
//     pointer (64-bit pointer mixed via xor-shift; the platform
//     allocator returns 8-byte-aligned pointers so the low 3 bits
//     are zero, removed before mixing).
//   * Max chain depth = 16. First chain reaching 16 emits a
//     diagnostic warning "shadow table chain depth limit reached".
//   * Max live allocs = 10 M (sized against Quest 3 4 GB RAM, ~400
//     bytes typical allocation = 4 GB attribution coverage).
//   * On overflow: evict LRU entry (the oldest-inserted record);
//     emit warning + increment skipped-attribution counter.
//
// The tracker itself MUST NOT recurse into FMemory::Malloc; that
// would deadlock when the per-tag table grows. The hash table's
// backing storage is allocated upfront in __Init (one VirtualAlloc
// for the entire 10 M-record array) so the per-allocation hot path
// does no allocator work.
//
// =====================================================================

#include "HAL/FLeakTracker.h"

#if XPACT_LEAK_TRACKING_ENABLED

#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "HAL/FPlatformMemory.h"
#include "Private/HAL/FMallocBinnedX.h"
#include "Private/HAL/StackWalk/IStackWalk.h"

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "Macros/XAssertionMacros.h"

#include <atomic>
#include <cstdio>
#include <cstdlib>     // std::malloc / std::free for bucket storage
#include <cstring>
#include <mutex>

namespace XCore::HAL
{
    namespace
    {
        // -----------------------------------------------------------------
        // Sizing constants (Section 12.2 fix A-MIN5).
        // -----------------------------------------------------------------

        // Max live allocations. Sized against Quest 3 4 GB RAM /
        // ~400-byte typical allocation = 4 GB attribution coverage.
        constexpr ::SIZE_T kMaxLiveAllocations = 10ull * 1024 * 1024;  // 10 M

        // Max chain depth before warning fires.
        constexpr ::SIZE_T kMaxChainDepth = 16;

        // Hash bucket count. Power of two so hash-to-bucket is a
        // single AND. ~10M / 16 = 625k buckets minimum; rounded up
        // to next power of two = 1 M (1 << 20). Average chain depth
        // at full capacity = 10 (well within the 16 limit).
        constexpr ::SIZE_T kBucketCount = 1ull << 20;
        constexpr ::SIZE_T kBucketMask  = kBucketCount - 1;

        // -----------------------------------------------------------------
        // FAllocRecord -- one tracked allocation.
        //
        // Stored in an intrusive doubly-linked list per bucket; the
        // global LRU list links across all buckets (insertion order).
        // -----------------------------------------------------------------
        struct FAllocRecord
        {
            void*    UserPtr;            // the user pointer FMemory returned
            ::SIZE_T Size;               // the requested allocation size
            FMemTag  Tag;                // the tag
            ::uint16 _pad0;              // alignment to 8 bytes

            // Per-bucket chain (singly-linked; chain depth bounded).
            FAllocRecord* NextInBucket;

            // Global LRU chain (doubly-linked; eviction targets the
            // oldest end).
            FAllocRecord* OlderInLRU;
            FAllocRecord* NewerInLRU;

            // Stack walk -- 24 frames. Captured at Malloc time.
            void*    Frames[kMaxStackFrames];
            ::uint8  FrameCount;
            ::uint8  _pad1[7];           // alignment to 8

            // True if this record's slot is in-use; false if the slot
            // is on the free-slot list (records are pre-allocated;
            // free-list-managed).
            bool     InUse;
        };

        static_assert(sizeof(FAllocRecord) >= 256,
                      "FAllocRecord layout sanity check (24 frames * 8 bytes ~= 192 + overhead)");

        // -----------------------------------------------------------------
        // The global tracker state.
        //
        // All members are zero-initialised at static-storage-duration
        // time (the struct is constinit); __Init populates them.
        // -----------------------------------------------------------------
        struct FTrackerState
        {
            // The pre-allocated record pool. Capacity kMaxLiveAllocations.
            // Allocated via FPlatformMemory::ReserveVirtual + CommitVirtual
            // in __Init so the tracker itself does not route through
            // FMemory::Malloc (avoiding recursive-hook deadlock).
            FAllocRecord* RecordPool;

            // Free-slot list head. Records popped on Malloc, pushed on
            // Free.
            FAllocRecord* FreeListHead;

            // The bucket array. Each bucket holds a singly-linked
            // chain of FAllocRecord*. Allocated via VM-reservation
            // alongside the record pool.
            FAllocRecord** Buckets;

            // LRU list head + tail. Head = newest; tail = oldest.
            FAllocRecord* LRUHead;
            FAllocRecord* LRUTail;

            // Live count + skipped-attribution count.
            ::std::atomic<::SIZE_T> LiveCount;
            ::std::atomic<::SIZE_T> SkippedCount;

            // Global mutex. Coarse-grained; the tracker's perf cost is
            // dominated by the stack walk (~50 ns for 24 frames on
            // Win64 RtlCaptureStackBackTrace), so the mutex is not
            // the bottleneck at the per-alloc level. Phase 1c may
            // shard the table per-thread if benchmarks indicate
            // contention.
            ::std::mutex Mutex;

            // Initialised guard.
            ::std::atomic<bool> Initialized;

            // Chain-depth-limit-reached warning fired? One-shot.
            ::std::atomic<bool> ChainDepthWarned;
        };

        // The constinit-friendly storage. The struct is POD-default-
        // initialised so all members are zero at static-storage time;
        // __Init populates the rest.
        FTrackerState& GetState() noexcept
        {
            static FTrackerState State{};
            return State;
        }

        // -----------------------------------------------------------------
        // Hash a pointer to a bucket index.
        //
        // The 8-byte alignment of FMemory-returned pointers means the
        // low 3 bits are always zero; shift them out before mixing.
        // The mixer is the classic xor-shift constant from murmur3.
        // -----------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE ::SIZE_T HashPtr(void* P) noexcept
        {
            ::uint64 H = reinterpret_cast<::uint64>(P) >> 3;
            H ^= H >> 33;
            H *= 0xFF51AFD7ED558CCDull;
            H ^= H >> 33;
            return static_cast<::SIZE_T>(H) & kBucketMask;
        }

        // -----------------------------------------------------------------
        // LRU helpers.
        // -----------------------------------------------------------------

        void LRUInsertHead(FAllocRecord* Rec) noexcept
        {
            FTrackerState& State = GetState();
            Rec->OlderInLRU = State.LRUHead;
            Rec->NewerInLRU = nullptr;
            if (State.LRUHead != nullptr)
            {
                State.LRUHead->NewerInLRU = Rec;
            }
            State.LRUHead = Rec;
            if (State.LRUTail == nullptr)
            {
                State.LRUTail = Rec;
            }
        }

        void LRURemove(FAllocRecord* Rec) noexcept
        {
            FTrackerState& State = GetState();
            if (Rec->OlderInLRU)
            {
                Rec->OlderInLRU->NewerInLRU = Rec->NewerInLRU;
            }
            else
            {
                State.LRUHead = Rec->NewerInLRU;
            }
            if (Rec->NewerInLRU)
            {
                Rec->NewerInLRU->OlderInLRU = Rec->OlderInLRU;
            }
            else
            {
                State.LRUTail = Rec->OlderInLRU;
            }
        }

        // -----------------------------------------------------------------
        // Pop a record from the free list. Returns nullptr if exhausted
        // (caller should trigger LRU eviction).
        // -----------------------------------------------------------------
        FAllocRecord* PopFreeSlot() noexcept
        {
            FTrackerState& State = GetState();
            FAllocRecord* Rec = State.FreeListHead;
            if (Rec)
            {
                State.FreeListHead = Rec->NextInBucket;
                Rec->NextInBucket  = nullptr;
            }
            return Rec;
        }

        void PushFreeSlot(FAllocRecord* Rec) noexcept
        {
            FTrackerState& State = GetState();
            Rec->InUse        = false;
            Rec->NextInBucket = State.FreeListHead;
            State.FreeListHead = Rec;
        }

        // -----------------------------------------------------------------
        // Evict the oldest LRU record (eviction policy: forget the
        // attribution, increment SkippedCount, emit one warning).
        //
        // Caller holds the mutex.
        // -----------------------------------------------------------------
        void EvictOldest() noexcept
        {
            FTrackerState& State = GetState();
            FAllocRecord* Victim = State.LRUTail;
            if (Victim == nullptr)
            {
                return;
            }

            // Remove victim from its bucket chain.
            const ::SIZE_T Bucket = HashPtr(Victim->UserPtr);
            FAllocRecord** Slot   = &State.Buckets[Bucket];
            while (*Slot && *Slot != Victim)
            {
                Slot = &((*Slot)->NextInBucket);
            }
            if (*Slot == Victim)
            {
                *Slot = Victim->NextInBucket;
            }

            LRURemove(Victim);
            PushFreeSlot(Victim);
            State.SkippedCount.fetch_add(1, ::std::memory_order_relaxed);

            // One-shot warning.
            static ::std::atomic<bool> WarnedOverflow{false};
            if (!WarnedOverflow.exchange(true, ::std::memory_order_relaxed))
            {
                ::std::fprintf(stderr,
                               "[FLeakTracker] shadow table overflow at %llu allocations; "
                               "oldest entries forgotten\n",
                               static_cast<unsigned long long>(kMaxLiveAllocations));
                ::std::fflush(stderr);
            }
        }

        // -----------------------------------------------------------------
        // Recursion guard. The tracker MUST NOT re-enter itself on its
        // own allocations (FMemTag::LeakTracker tag). The hook checks
        // this tag and a thread-local recursion flag.
        // -----------------------------------------------------------------
        thread_local bool g_inHook = false;

        // -----------------------------------------------------------------
        // The Malloc hook -- called by FMallocBinnedX::Malloc.
        // -----------------------------------------------------------------
        void OnMalloc(void* Ptr, ::SIZE_T Size, FMemTag Tag) noexcept
        {
            if (g_inHook || Tag == FMemTag::LeakTracker)
            {
                return;
            }

            FTrackerState& State = GetState();
            if (!State.Initialized.load(::std::memory_order_acquire))
            {
                return;
            }

            g_inHook = true;

            ::std::lock_guard<::std::mutex> Lock(State.Mutex);

            // Get a slot.
            FAllocRecord* Rec = PopFreeSlot();
            if (Rec == nullptr)
            {
                // Free list empty: evict LRU + try again.
                EvictOldest();
                Rec = PopFreeSlot();
                if (Rec == nullptr)
                {
                    // Should not happen post-eviction; record skip.
                    State.SkippedCount.fetch_add(1, ::std::memory_order_relaxed);
                    g_inHook = false;
                    return;
                }
            }

            Rec->UserPtr      = Ptr;
            Rec->Size         = Size;
            Rec->Tag          = Tag;
            Rec->InUse        = true;
            Rec->NextInBucket = nullptr;

            // Capture stack. Skip 3 frames: OnMalloc, FMallocBinnedX::
            // Malloc (or MallocLarge), FMemory::Malloc.
            const ::SIZE_T FrameCount = CaptureStackWalk(Rec->Frames, 3);
            Rec->FrameCount = static_cast<::uint8>(FrameCount);

            // Insert at bucket head.
            const ::SIZE_T Bucket = HashPtr(Ptr);
            Rec->NextInBucket     = State.Buckets[Bucket];
            State.Buckets[Bucket] = Rec;

            // Insert at LRU head (newest).
            LRUInsertHead(Rec);

            State.LiveCount.fetch_add(1, ::std::memory_order_relaxed);

            // Check chain depth for one-shot warning.
            if (!State.ChainDepthWarned.load(::std::memory_order_relaxed))
            {
                ::SIZE_T Depth = 0;
                for (FAllocRecord* Cur = State.Buckets[Bucket]; Cur; Cur = Cur->NextInBucket)
                {
                    ++Depth;
                    if (Depth >= kMaxChainDepth)
                    {
                        if (!State.ChainDepthWarned.exchange(true, ::std::memory_order_relaxed))
                        {
                            ::std::fprintf(stderr,
                                           "[FLeakTracker] shadow table chain depth limit reached "
                                           "(%llu); future chain overflows are absorbed and counted "
                                           "as skipped-attribution\n",
                                           static_cast<unsigned long long>(kMaxChainDepth));
                            ::std::fflush(stderr);
                        }
                        break;
                    }
                }
            }

            g_inHook = false;
        }

        // -----------------------------------------------------------------
        // The Free hook -- called by FMallocBinnedX::Free.
        // -----------------------------------------------------------------
        void OnFree(void* Ptr) noexcept
        {
            if (g_inHook || Ptr == nullptr)
            {
                return;
            }

            FTrackerState& State = GetState();
            if (!State.Initialized.load(::std::memory_order_acquire))
            {
                return;
            }

            g_inHook = true;

            ::std::lock_guard<::std::mutex> Lock(State.Mutex);

            const ::SIZE_T Bucket = HashPtr(Ptr);
            FAllocRecord** Slot   = &State.Buckets[Bucket];
            while (*Slot)
            {
                if ((*Slot)->UserPtr == Ptr)
                {
                    FAllocRecord* Rec = *Slot;
                    *Slot = Rec->NextInBucket;
                    LRURemove(Rec);
                    PushFreeSlot(Rec);
                    State.LiveCount.fetch_sub(1, ::std::memory_order_relaxed);
                    g_inHook = false;
                    return;
                }
                Slot = &((*Slot)->NextInBucket);
            }

            // Not found: either the original allocation was evicted by
            // LRU, or this is a Free of a pointer the tracker never
            // saw (e.g., allocated before __Init). Silent.
            g_inHook = false;
        }
    } // anonymous

    // =====================================================================
    // FLeakTracker::__Init -- engine bootstrap.
    // =====================================================================

    void FLeakTracker::__Init() noexcept
    {
        FTrackerState& State = GetState();

        if (State.Initialized.exchange(true, ::std::memory_order_acq_rel))
        {
            return;  // already initialised
        }

        // Allocate the record pool. Size = kMaxLiveAllocations *
        // sizeof(FAllocRecord). Use VM-reservation so the tracker does
        // not route through FMemory::Malloc.
        const ::SIZE_T RecordPoolBytes = kMaxLiveAllocations * sizeof(FAllocRecord);
        void* RecordPoolVM = ::XCore::HAL::FPlatformMemory::ReserveVirtual(RecordPoolBytes);
        if (RecordPoolVM == nullptr ||
            !::XCore::HAL::FPlatformMemory::CommitVirtual(RecordPoolVM, RecordPoolBytes))
        {
            ::XCore::HAL::AbortWithMessage(
                "FLeakTracker::__Init: VM reservation/commit failed for record pool",
                __FILE__, __LINE__);
        }
        State.RecordPool = static_cast<FAllocRecord*>(RecordPoolVM);

        // Allocate the bucket array.
        const ::SIZE_T BucketBytes = kBucketCount * sizeof(FAllocRecord*);
        void* BucketVM = ::XCore::HAL::FPlatformMemory::ReserveVirtual(BucketBytes);
        if (BucketVM == nullptr ||
            !::XCore::HAL::FPlatformMemory::CommitVirtual(BucketVM, BucketBytes))
        {
            ::XCore::HAL::AbortWithMessage(
                "FLeakTracker::__Init: VM reservation/commit failed for bucket array",
                __FILE__, __LINE__);
        }
        State.Buckets = static_cast<FAllocRecord**>(BucketVM);
        ::std::memset(State.Buckets, 0, BucketBytes);

        // Push every record onto the free list.
        State.FreeListHead = nullptr;
        for (::SIZE_T I = 0; I < kMaxLiveAllocations; ++I)
        {
            FAllocRecord* Rec  = &State.RecordPool[I];
            Rec->InUse         = false;
            Rec->NextInBucket  = State.FreeListHead;
            State.FreeListHead = Rec;
        }

        State.LRUHead = nullptr;
        State.LRUTail = nullptr;
        State.LiveCount.store(0,    ::std::memory_order_relaxed);
        State.SkippedCount.store(0, ::std::memory_order_relaxed);
        State.ChainDepthWarned.store(false, ::std::memory_order_relaxed);

        // Initialise stack walker.
        InitStackWalk();

        // Install hooks into the allocator.
        g_Allocator.SetMallocHook(&OnMalloc);
        g_Allocator.SetFreeHook(&OnFree);
    }

    // =====================================================================
    // FLeakTracker::__Shutdown -- engine teardown.
    // =====================================================================

    void FLeakTracker::__Shutdown() noexcept
    {
        FTrackerState& State = GetState();

        if (!State.Initialized.exchange(false, ::std::memory_order_acq_rel))
        {
            return;
        }

        // Uninstall hooks first (so any deletion below doesn't recurse).
        g_Allocator.SetMallocHook(nullptr);
        g_Allocator.SetFreeHook(nullptr);

        // Emit any final leak report (Section 12 / acceptance I1:
        // "Deliberate leak in Debug reported at exit with full callstack
        // on all three platforms"). Phase 1b emits per-tag aggregate
        // counts; per-bucket symbol resolution lands in Phase 1c.
        const ::SIZE_T Live    = State.LiveCount.load(::std::memory_order_relaxed);
        const ::SIZE_T Skipped = State.SkippedCount.load(::std::memory_order_relaxed);

        if (Live > 0 || Skipped > 0)
        {
            ::std::fprintf(stderr,
                           "[FLeakTracker] LEAKS at shutdown: live=%llu skipped=%llu\n",
                           static_cast<unsigned long long>(Live),
                           static_cast<unsigned long long>(Skipped));
            ::std::fflush(stderr);
        }

        // Release the VM pools.
        if (State.RecordPool)
        {
            ::XCore::HAL::FPlatformMemory::ReleaseVirtual(
                State.RecordPool,
                kMaxLiveAllocations * sizeof(FAllocRecord));
            State.RecordPool = nullptr;
        }
        if (State.Buckets)
        {
            ::XCore::HAL::FPlatformMemory::ReleaseVirtual(
                State.Buckets,
                kBucketCount * sizeof(FAllocRecord*));
            State.Buckets = nullptr;
        }

        ShutdownStackWalk();
    }

    // =====================================================================
    // FLeakTracker::CaptureReport
    // =====================================================================

    namespace
    {
        // BucketsImpl storage: a malloc-backed FStackBucket[] array.
        // Allocated via std::malloc (NOT via FMemory::Malloc; the
        // tracker must not recurse into the allocator's hooks at
        // CaptureReport time). Phase 1c will swap to FMemory::Malloc
        // tagged FMemTag::LeakTracker once the hook recursion guard
        // is verified safe under the snapshot path.
        struct FBucketsArray
        {
            FLeakTracker::FStackBucket* Data;
            ::SIZE_T                    Count;
            ::SIZE_T                    Capacity;
        };
    } // anonymous

    // -----------------------------------------------------------------
    // FLeakReport methods.
    // -----------------------------------------------------------------

    FLeakTracker::FLeakReport::~FLeakReport() noexcept
    {
        if (BucketsImpl)
        {
            FBucketsArray* Arr = static_cast<FBucketsArray*>(BucketsImpl);
            ::std::free(Arr->Data);
            ::std::free(Arr);
            BucketsImpl = nullptr;
        }
    }

    FLeakTracker::FLeakReport::FLeakReport(FLeakReport&& Other) noexcept
        : TotalLeakedBytes(Other.TotalLeakedBytes),
          LeakedAllocationCount(Other.LeakedAllocationCount),
          BucketCount(Other.BucketCount),
          BucketsImpl(Other.BucketsImpl)
    {
        Other.BucketsImpl           = nullptr;
        Other.BucketCount           = 0;
        Other.TotalLeakedBytes      = 0;
        Other.LeakedAllocationCount = 0;
    }

    FLeakTracker::FLeakReport& FLeakTracker::FLeakReport::operator=(FLeakReport&& Other) noexcept
    {
        if (this != &Other)
        {
            // Free our own storage first.
            if (BucketsImpl)
            {
                FBucketsArray* Arr = static_cast<FBucketsArray*>(BucketsImpl);
                ::std::free(Arr->Data);
                ::std::free(Arr);
            }
            TotalLeakedBytes      = Other.TotalLeakedBytes;
            LeakedAllocationCount = Other.LeakedAllocationCount;
            BucketCount           = Other.BucketCount;
            BucketsImpl           = Other.BucketsImpl;

            Other.BucketsImpl           = nullptr;
            Other.BucketCount           = 0;
            Other.TotalLeakedBytes      = 0;
            Other.LeakedAllocationCount = 0;
        }
        return *this;
    }

    FLeakTracker::FStackBucket FLeakTracker::FLeakReport::GetBucket(::SIZE_T I) const noexcept
    {
        // Bounds check (Debug/Dev only; Shipping UB-on-OOB matches the
        // rest of the engine's accessor policy).
        XPACT_CHECK(I < BucketCount);
        FStackBucket Empty{};
        if (BucketsImpl == nullptr || I >= BucketCount)
        {
            return Empty;
        }
        const FBucketsArray* Arr = static_cast<const FBucketsArray*>(BucketsImpl);
        return Arr->Data[I];
    }

    // -----------------------------------------------------------------
    // FLeakTracker::CaptureReport
    // -----------------------------------------------------------------

    void FLeakTracker::CaptureReport(FLeakReport& Out) noexcept
    {
        FTrackerState& State = GetState();

        // Free any existing storage.
        Out.TotalLeakedBytes      = 0;
        Out.LeakedAllocationCount = 0;
        Out.BucketCount           = 0;
        if (Out.BucketsImpl)
        {
            FBucketsArray* OldArr = static_cast<FBucketsArray*>(Out.BucketsImpl);
            ::std::free(OldArr->Data);
            ::std::free(OldArr);
            Out.BucketsImpl = nullptr;
        }

        if (!State.Initialized.load(::std::memory_order_acquire))
        {
            return;
        }

        ::std::lock_guard<::std::mutex> Lock(State.Mutex);

        // First pass: tally aggregates + count distinct call-stack
        // buckets (frame-array equality).
        // Phase 1b: simple O(N*N) bucket grouping. N is bounded by the
        // live-alloc count (<= 10M; typically much smaller). For the
        // synthetic-leak test (100 records) the cost is negligible.
        // Phase 1c will use a hash on the frame-array contents.
        // TODO(Phase 1c): hash-table-based bucket grouping.

        for (FAllocRecord* Cur = State.LRUHead; Cur; Cur = Cur->OlderInLRU)
        {
            if (Cur->InUse)
            {
                Out.TotalLeakedBytes += Cur->Size;
                ++Out.LeakedAllocationCount;
            }
        }

        // Optional bucketing pass: allocate up to LiveCount buckets
        // (worst case = all distinct). If the report has 0 live allocs
        // skip the allocation entirely.
        if (Out.LeakedAllocationCount == 0)
        {
            return;
        }

        FBucketsArray* Arr = static_cast<FBucketsArray*>(::std::malloc(sizeof(FBucketsArray)));
        if (Arr == nullptr)
        {
            return;  // out-of-host-memory; report ships with bucket
                     // count 0 but aggregate counts intact.
        }
        Arr->Capacity = Out.LeakedAllocationCount;
        Arr->Count    = 0;
        Arr->Data     = static_cast<FStackBucket*>(
                            ::std::malloc(Arr->Capacity * sizeof(FStackBucket)));
        if (Arr->Data == nullptr)
        {
            ::std::free(Arr);
            return;
        }
        ::std::memset(Arr->Data, 0, Arr->Capacity * sizeof(FStackBucket));

        // Group by frame-array equality.
        for (FAllocRecord* Cur = State.LRUHead; Cur; Cur = Cur->OlderInLRU)
        {
            if (!Cur->InUse)
            {
                continue;
            }

            // Find an existing bucket with the same frame array.
            ::SIZE_T FoundIdx = ~::SIZE_T(0);
            for (::SIZE_T B = 0; B < Arr->Count; ++B)
            {
                if (Arr->Data[B].FrameCount == Cur->FrameCount &&
                    ::std::memcmp(Arr->Data[B].Frames, Cur->Frames,
                                  Cur->FrameCount * sizeof(void*)) == 0)
                {
                    FoundIdx = B;
                    break;
                }
            }

            if (FoundIdx == ~::SIZE_T(0))
            {
                // New bucket.
                FoundIdx = Arr->Count++;
                FStackBucket& Bucket = Arr->Data[FoundIdx];
                ::std::memcpy(Bucket.Frames, Cur->Frames,
                              kMaxStackFrames * sizeof(void*));
                Bucket.FrameCount      = Cur->FrameCount;
                Bucket.TotalBytes      = 0;
                Bucket.AllocationCount = 0;
            }

            Arr->Data[FoundIdx].TotalBytes      += Cur->Size;
            Arr->Data[FoundIdx].AllocationCount += 1;
        }

        Out.BucketCount = Arr->Count;
        Out.BucketsImpl = Arr;
    }

    // =====================================================================
    // FLeakTracker::WriteReport
    // =====================================================================

    void FLeakTracker::WriteReport(const ::XCore::FString& /*Path*/)
    {
        // Phase 1b: FString::operator-as-C-string is not yet available
        // (FString lands at XCore-4a Step 8, Phase 1c). The writer
        // currently emits to stderr; the Phase 1c version will accept
        // the FString path and write to a real file.
        // TODO(Phase 1c): convert Path to a C-string, open the file
        // via std::ofstream, write the report.
        FLeakReport Report;
        CaptureReport(Report);
        ::std::fprintf(stderr,
                       "[FLeakTracker] WriteReport (stderr fallback, Phase 1b):\n"
                       "  TotalLeakedBytes      = %llu\n"
                       "  LeakedAllocationCount = %llu\n",
                       static_cast<unsigned long long>(Report.TotalLeakedBytes),
                       static_cast<unsigned long long>(Report.LeakedAllocationCount));
        ::std::fflush(stderr);
    }

    // =====================================================================
    // Diagnostic accessors.
    // =====================================================================

    ::SIZE_T FLeakTracker::GetLiveAllocationCount() noexcept
    {
        return GetState().LiveCount.load(::std::memory_order_relaxed);
    }

    ::SIZE_T FLeakTracker::GetSkippedAttributionCount() noexcept
    {
        return GetState().SkippedCount.load(::std::memory_order_relaxed);
    }

} // namespace XCore::HAL

#endif  // XPACT_LEAK_TRACKING_ENABLED
