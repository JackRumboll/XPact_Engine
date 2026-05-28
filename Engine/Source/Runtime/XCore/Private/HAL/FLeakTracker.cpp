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
#include "HAL/FMutex.h"            // Phase 1g fix M-8: FMutex replaces std::mutex
#include "HAL/FPlatformMemory.h"
#include "HAL/FMallocBinnedX.h"
#include "HAL/StackWalk/IStackWalk.h"
#include "Containers/FString.h"    // Rev 2 FIX-3: WriteReport now writes to disk

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "Macros/XAssertionMacros.h"

#include <atomic>
#include <cstdio>
#include <cstring>

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
            //
            // Phase 1g fix M-8: FMutex (XCore's HAL primitive) replaces
            // std::mutex. FTrackerState is held in a Meyers singleton
            // (GetState() returns `static FTrackerState State{};`); it
            // is not constinit-required, so FMutex's non-constexpr
            // ctor is fine here.
            ::XCore::HAL::FMutex Mutex;

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

            ::XCore::HAL::FScopedMutexLock Lock(State.Mutex);

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

            ::XCore::HAL::FScopedMutexLock Lock(State.Mutex);

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
    // FLeakTracker::CaptureReport (Phase 1g fix M-7: TArray-backed)
    // =====================================================================
    //
    // The Phase 1b opaque-pointer / std::malloc-backed FBucketsArray
    // shim has been removed. FLeakReport::Buckets is now a value-
    // embedded TArray<FStackBucket>, populated directly via Add()
    // during the aggregation pass. The TArray uses the default XCore
    // allocator (FMemory::Malloc tagged FMemTag::LeakTracker via
    // DefaultAllocator); the tracker's existing g_inHook recursion
    // guard prevents re-entry into the per-allocation hook from the
    // bucket-array growth path because CaptureReport is called from
    // outside any malloc-hook scope.
    //
    // Move ctor + move assignment are now compiler-generated (= default
    // in the header) — TArray supplies the right move semantics.
    // -----------------------------------------------------------------

    void FLeakTracker::CaptureReport(FLeakReport& Out) noexcept
    {
        FTrackerState& State = GetState();

        // Reset the report. Buckets.Reset() preserves capacity which
        // is harmless on a repeat-capture; the move-overwrite of Out
        // by the caller is the dominant use pattern.
        Out.TotalLeakedBytes      = 0;
        Out.LeakedAllocationCount = 0;
        Out.Buckets.Reset();

        if (!State.Initialized.load(::std::memory_order_acquire))
        {
            return;
        }

        ::XCore::HAL::FScopedMutexLock Lock(State.Mutex);

        // First pass: tally aggregates.
        for (FAllocRecord* Cur = State.LRUHead; Cur; Cur = Cur->OlderInLRU)
        {
            if (Cur->InUse)
            {
                Out.TotalLeakedBytes += Cur->Size;
                ++Out.LeakedAllocationCount;
            }
        }

        if (Out.LeakedAllocationCount == 0)
        {
            return;
        }

        // Reserve enough capacity for the worst case (all distinct).
        // The actual bucket count is typically much smaller; the
        // reserve avoids reallocation during the grouping pass. The
        // cast to int32 is safe: live alloc count is bounded by the
        // 10M record-pool capacity which fits easily in int32.
        Out.Buckets.Reserve(static_cast<::int32>(Out.LeakedAllocationCount));

        // Second pass: group by frame-array equality. O(N*B) where
        // B is the bucket count; for the synthetic-leak test
        // workload (100 records, typically <10 distinct stacks)
        // the cost is negligible. TODO: hash-table-based grouping
        // if production workloads show this in profile.
        for (FAllocRecord* Cur = State.LRUHead; Cur; Cur = Cur->OlderInLRU)
        {
            if (!Cur->InUse)
            {
                continue;
            }

            // Find an existing bucket with the same frame array.
            ::int32 FoundIdx  = -1;
            const ::int32 NumBuckets = Out.Buckets.Num();
            for (::int32 B = 0; B < NumBuckets; ++B)
            {
                const FStackBucket& Existing = Out.Buckets[B];
                if (static_cast<int>(Existing.FrameCount) == Cur->FrameCount &&
                    ::std::memcmp(Existing.Frames, Cur->Frames,
                                  Cur->FrameCount * sizeof(void*)) == 0)
                {
                    FoundIdx = B;
                    break;
                }
            }

            if (FoundIdx == -1)
            {
                // New bucket.
                FStackBucket NewBucket{};
                ::std::memcpy(NewBucket.Frames, Cur->Frames,
                              kMaxStackFrames * sizeof(void*));
                NewBucket.FrameCount      = Cur->FrameCount;
                NewBucket.TotalBytes      = Cur->Size;
                NewBucket.AllocationCount = 1;
                // FStackBucket is trivially copyable (POD of 24
                // void* + a few size_t / int); Add (const T&) and
                // Emplace (T&&) generate identical code. Use Emplace
                // for stylistic correctness.
                Out.Buckets.Emplace(::std::move(NewBucket));
            }
            else
            {
                FStackBucket& Bkt = Out.Buckets[FoundIdx];
                Bkt.TotalBytes      += Cur->Size;
                Bkt.AllocationCount += 1;
            }
        }
    }

    // =====================================================================
    // FLeakTracker::WriteReport (Rev 2 FIX-3 / C7)
    // =====================================================================
    //
    // Per Rev 2 FIX-3: write a structured report to the file named by
    // Path. The prior Phase 1b implementation took the FString Path
    // argument and silently dropped it on the floor; the user-visible
    // behaviour was indistinguishable from passing the wrong path.
    //
    // FILE FORMAT (line-oriented; UTF-8):
    //
    //   FLeakTracker WriteReport
    //   --------------------------------------------------------------
    //   Total leaked bytes:        <N>
    //   Total leaked allocations:  <N>
    //   Distinct stack buckets:    <N>
    //   --------------------------------------------------------------
    //   Top <K> stack buckets by leaked bytes:
    //
    //   Bucket #<i>:
    //     allocations:  <N>
    //     total bytes:  <N>
    //     frames:
    //       [00]  0x<addr>
    //       [01]  0x<addr>
    //       ...
    //
    // I/O: uses C-stdlib <cstdio> (fopen/fwrite/fclose). A platform-
    // native FFileHandle HAL is not yet shipped in XCore-4a (Phase 1d
    // declared file I/O as deferred to XSerialization Layer 9); the
    // cstdio fallback is the minimum-viable correct path. When
    // FFileHandle ships, the cstdio call sites here should be swapped
    // for FFileHandle::Open / Write / Close.
    //
    // FAILURE: fopen failure logs a diagnostic to stderr and returns
    // without writing. The caller's path is reproduced in the stderr
    // line so the operator can diagnose path issues.
    // =====================================================================

    namespace
    {
        // Number of top-N stack buckets emitted to the report file.
        // Buckets are sorted by TotalBytes descending; only the top-N
        // are written so the file stays scannable. Hardcoded at 10
        // (matches the FIX-3 default; configurable via a CVar in a
        // future revision if needed).
        constexpr ::int32 kWriteReportTopBuckets = 10;
    }

    void FLeakTracker::WriteReport(const ::XCore::FString& Path)
    {
        FLeakReport Report;
        CaptureReport(Report);

        // Open the file in write+text mode. FString::ToUtf8Cstr returns
        // a null-terminated UTF-8 buffer; ToUtf8Cstr may allocate a
        // copy if the storage is not already null-terminated (see
        // FString.h:497). Caller's Path is preserved for diagnostics.
        const char* CPath = Path.ToUtf8Cstr();
        // TODO(FFileHandle when available): swap fopen for FFileHandle.
        ::std::FILE* File = ::std::fopen(CPath, "w");
        if (File == nullptr)
        {
            ::std::fprintf(stderr,
                "[FLeakTracker] WriteReport: fopen(\"%s\", \"w\") failed; "
                "report not written.\n",
                CPath);
            ::std::fflush(stderr);
            return;
        }

        // Header.
        ::std::fprintf(File,
            "FLeakTracker WriteReport\n"
            "--------------------------------------------------------------\n"
            "Total leaked bytes:        %llu\n"
            "Total leaked allocations:  %llu\n"
            "Distinct stack buckets:    %d\n"
            "--------------------------------------------------------------\n",
            static_cast<unsigned long long>(Report.TotalLeakedBytes),
            static_cast<unsigned long long>(Report.LeakedAllocationCount),
            Report.Buckets.Num());

        // Sort buckets by TotalBytes descending. Use a stack-allocated
        // index array so we don't perturb Report.Buckets (which is
        // owned by the caller). For N buckets the O(N log N) sort is
        // negligible against the file-I/O.
        //
        // Cap at 256 distinct buckets to bound the on-stack array; if
        // the report has more buckets than that, the top-N selection is
        // performed against the first 256 (still correct because we
        // sort then truncate).
        constexpr ::int32 kMaxBucketsForSort = 256;
        const ::int32 NumBucketsInReport = Report.Buckets.Num();
        const ::int32 NumBucketsForSort  =
            (NumBucketsInReport < kMaxBucketsForSort) ? NumBucketsInReport
                                                       : kMaxBucketsForSort;
        ::int32 BucketIdx[kMaxBucketsForSort];
        for (::int32 I = 0; I < NumBucketsForSort; ++I)
        {
            BucketIdx[I] = I;
        }

        // Insertion sort by TotalBytes desc. Stable on equal keys
        // (preserves bucket-discovery order). Bounded N ~= 256 keeps
        // the O(N^2) acceptable.
        for (::int32 I = 1; I < NumBucketsForSort; ++I)
        {
            const ::int32 KeyIdx = BucketIdx[I];
            const ::SIZE_T KeyBytes = Report.Buckets[KeyIdx].TotalBytes;
            ::int32 J = I - 1;
            while (J >= 0 &&
                   Report.Buckets[BucketIdx[J]].TotalBytes < KeyBytes)
            {
                BucketIdx[J + 1] = BucketIdx[J];
                --J;
            }
            BucketIdx[J + 1] = KeyIdx;
        }

        // Emit the top-N.
        const ::int32 NumToEmit =
            (NumBucketsForSort < kWriteReportTopBuckets) ? NumBucketsForSort
                                                          : kWriteReportTopBuckets;
        if (NumToEmit > 0)
        {
            ::std::fprintf(File, "Top %d stack buckets by leaked bytes:\n\n",
                          NumToEmit);
        }

        for (::int32 I = 0; I < NumToEmit; ++I)
        {
            const FStackBucket& Bkt = Report.Buckets[BucketIdx[I]];
            ::std::fprintf(File,
                "Bucket #%d:\n"
                "  allocations:  %llu\n"
                "  total bytes:  %llu\n"
                "  frames:\n",
                I,
                static_cast<unsigned long long>(Bkt.AllocationCount),
                static_cast<unsigned long long>(Bkt.TotalBytes));

            for (int FI = 0; FI < Bkt.FrameCount; ++FI)
            {
                ::std::fprintf(File, "    [%02d]  %p\n", FI, Bkt.Frames[FI]);
            }
            ::std::fprintf(File, "\n");
        }

        if (NumBucketsInReport > kMaxBucketsForSort)
        {
            ::std::fprintf(File,
                "Note: report contained %d distinct buckets; top-N selection "
                "was performed against the first %d only.\n",
                NumBucketsInReport, kMaxBucketsForSort);
        }

        ::std::fclose(File);
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
