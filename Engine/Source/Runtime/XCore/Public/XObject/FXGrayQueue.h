// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FXGrayQueue.h -- the GC mark-phase gray queue (XCoreXObject Rev 4
// §4.2 + §4.4; Phase 5.g).
// =====================================================================
//
// XCoreXObject Rev 4 §4.2 (mark phase: "drain the gray queue. For
// each gray object, mark its FXObjectArrayEntry, then walk its
// schema vector; for each reference, if unmarked, push it onto the
// gray queue") + §4.4 ("workers run in parallel; the gray queue is
// per-thread with work-stealing to balance").
//
// PURPOSE: FIFO-ish work queue used by the mark inner loop. Holds
// XObject* pointers waiting to be visited (their schema-vector
// referenced objects need to be discovered + marked).
//
// =====================================================================
//
// IMPLEMENTATION CHOICE (Prime Directive judgement; documented in
// the Phase 5.g .Build.toml block).
//
// The spec mentions both:
//   * "per-thread with work-stealing to balance" (§4.4)
//   * "the gray queue is the worker's local work-list" (implied)
//
// Two principled implementation options:
//
//   1. Chase-Lev work-stealing deque -- lock-free deque where the
//      OWNING thread pushes/pops the bottom (LIFO) and STEALERS
//      pop the top (FIFO). Canonical for work-stealing schedulers
//      (TBB, Cilk, Tokio). Pros: zero contention in the steady-
//      state (owning thread only touches the bottom). Cons: harder
//      to reason about; non-trivial memory-ordering invariants.
//
//   2. Per-thread bounded LIFO + global MPSC overflow list -- the
//      owning thread Push/Pops from a bounded TLS buffer; when
//      Push would overflow the buffer it spills to a global lock-
//      protected list. When Pop would underflow the local buffer
//      it pulls a chunk from the global list. Pros: simple +
//      bounded latency for the local case + the global list is
//      the natural integration point for the "drain ALL gray
//      queues at end-of-mark" termination check. Cons: not
//      lock-free in the overflow path.
//
// Phase 5.g ships OPTION 2 (the simpler, principled-for-MVP
// choice). Justification:
//
//   * The MVP mark phase is single-threaded (dedicated marker
//     thread per FIX-A-MED-36 + §4.7); work-stealing across
//     workers is FUTURE-WORK gated on XTaskGraph integration. The
//     Chase-Lev complexity is unwarranted for a one-worker
//     configuration.
//
//   * The global overflow list IS the natural termination-detection
//     surface: when all worker TLS queues are empty AND the global
//     list is empty AND the SATB log is drained AND no dirty cards
//     remain, the mark phase is complete. Option 1 would still
//     need a separate termination-detection structure.
//
//   * The bounded TLS buffer matches the FXObjectSatbQueue
//     discipline (Phase 5.f) shipping in the same family. Code-
//     style + reasoning consistency.
//
// THREAD-DEATH POLICY (per prompt's prompt corner-case):
//
//   The MVP gray queue is the dedicated-marker-thread's TLS. The
//   thread lives for the engine's lifetime; there is no
//   thread-death-during-mark scenario in Phase 5.g. Phase 5.j /
//   XTaskGraph integration (post-System-8) will need a richer
//   thread-exit handoff path; Phase 5.g documents the contract:
//   on thread death, the dying thread's TLS gray queue MUST be
//   drained into the global overflow list before the thread
//   exits (the FXGrayQueueTLS destructor will land here once
//   multi-worker mark ships).
//
// =====================================================================
//
// CONCURRENCY (per Phase 5.g MVP):
//
//   The Phase 5.g MVP runs the mark phase on the dedicated marker
//   thread only (one TLS queue). The MVP queue surface IS race-safe
//   against:
//     * Concurrent Push from the producer thread (the marker thread
//       itself pushing during the schema walk's visitor lambda).
//     * Pop / Steal from a future worker-pool integration (the API
//       shape is forward-compatible; the TLS queue's push-pop
//       discipline is documented as "LIFO for the owner; the
//       global overflow list is the cross-thread integration
//       point").
//
//   The global overflow list IS protected by FCriticalSection. The
//   per-Push overflow rate is bounded (one acquire per 256 Push
//   operations because the TLS buffer is 256 entries deep);
//   contention is amortised.
//
// =====================================================================
//
// QUEUE CAPACITY:
//
//   256 entries per TLS queue, matching FXObjectSatbQueue's
//   capacity (Phase 5.f spec §15 O3: "256 entries; fits in 2 KB
//   which fits comfortably in L1"). The capacity is a tuning
//   parameter; the global overflow list is unbounded so an
//   undersized TLS queue degrades to higher per-Push contention
//   but stays correct.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "HAL/FCriticalSection.h"

#include <atomic>
#include <cstddef>
#include <cstdint>

namespace XCore { class XObject; }

namespace XCore
{
    // -----------------------------------------------------------------
    // FXGrayQueue -- per-thread gray queue with global-overflow spill.
    //
    // Layout:
    //   * m_entries[256] : 2048 bytes
    //   * m_count        : 4 bytes (atomic so foreign threads can
    //                       observe the live-count without locking)
    //   * trailing pad   : 4 bytes
    //
    // Total: 2056 bytes per queue (matches FXObjectSatbQueue's
    // footprint).
    //
    // alignas(8) -- matches XObject* slot alignment.
    //
    // NO virtual methods. Trivially destructible.
    // -----------------------------------------------------------------
    class alignas(8) FXGrayQueue
    {
    public:
        // Per-thread queue capacity (matches FXObjectSatbQueue::kCapacity
        // per the family discipline).
        static constexpr ::std::size_t kCapacity = 256;

        // -------------------------------------------------------------
        // Construction.
        //
        // Default ctor zero-initialises m_count. The entry slots are
        // intentionally uninitialised; slots are overwritten by Push
        // before Pop reads them.
        // -------------------------------------------------------------
        FXGrayQueue() noexcept
            : m_count(0)
            , m_peakCount(0)
        {
        }

        FXGrayQueue(const FXGrayQueue&)            = delete;
        FXGrayQueue(FXGrayQueue&&)                 = delete;
        FXGrayQueue& operator=(const FXGrayQueue&) = delete;
        FXGrayQueue& operator=(FXGrayQueue&&)      = delete;

        ~FXGrayQueue() noexcept = default;

        // -------------------------------------------------------------
        // Push -- enqueue an XObject* for later visit.
        //
        // Called from the mark loop's visitor lambda when a referenced
        // object is first-marked. The first-mark CAS is performed by
        // the COLLECTOR (FXObjectCollector::MarkObject); the Push is
        // unconditional from the queue's standpoint.
        //
        // FAST PATH (wait-free): m_count < kCapacity. Single slot
        // write + relaxed-store increment of m_count.
        //
        // SLOW PATH (overflow): drain the TLS buffer into the global
        // overflow list under FCriticalSection. The drain spills the
        // FULL contents of the TLS buffer; subsequent Push at slot 0
        // writes the new entry.
        //
        // Push silently ignores nullptr (defensive; the mark loop's
        // visitor pre-filters nullptr but the defence-in-depth is
        // cheap).
        // -------------------------------------------------------------
        void Push(XObject* Object) noexcept;

        // -------------------------------------------------------------
        // Pop -- dequeue the most-recently-pushed XObject* (LIFO).
        //
        // Returns nullptr when the queue is empty. The caller's mark
        // loop polls Pop until it returns nullptr, then checks the
        // global overflow list, then checks the SATB log, then checks
        // the dirty-card list (the spec §4.4 termination condition).
        //
        // FAST PATH: m_count > 0. Single relaxed-load + slot read +
        // relaxed-store decrement of m_count.
        //
        // SLOW PATH: TLS empty. Pull a chunk (up to kCapacity entries)
        // from the global overflow list under FCriticalSection. If the
        // global list is also empty, return nullptr.
        //
        // PROPERTY: Pop is the inverse of Push for the owning thread.
        // Foreign threads MUST NOT call Pop (the per-thread queue is
        // not multi-consumer-safe). The work-stealing variant lands
        // post-System-8 via a separate StealFrom path.
        // -------------------------------------------------------------
        [[nodiscard]] XObject* Pop() noexcept;

        // -------------------------------------------------------------
        // IsEmpty -- snapshot of "this TLS queue is empty + the global
        // overflow list is empty".
        //
        // The check is best-effort: the TLS count + the global count
        // are read with relaxed loads; a concurrent Push from another
        // thread (via the overflow list) may have already pushed by
        // the time this returns. The MVP Phase 5.g uses IsEmpty as
        // part of the termination predicate; the marker thread holds
        // a logical lock against itself so the snapshot is consistent
        // from the marker thread's perspective.
        // -------------------------------------------------------------
        [[nodiscard]] bool IsEmpty() const noexcept;

        // -------------------------------------------------------------
        // Size -- snapshot of the TLS queue's live count.
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE
        ::std::size_t Size() const noexcept
        {
            return static_cast<::std::size_t>(
                m_count.load(::std::memory_order_acquire));
        }

        // -------------------------------------------------------------
        // GetPeakSize -- maximum observed TLS queue depth since the
        // last ResetForCycle. Diagnostic; used by the Phase 5.g
        // collector to populate the kGCMarkEnd grayQueuePeak field.
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE
        ::std::size_t GetPeakSize() const noexcept
        {
            return static_cast<::std::size_t>(
                m_peakCount.load(::std::memory_order_acquire));
        }

        // -------------------------------------------------------------
        // ResetForCycle -- clear the queue's state at start of GC
        // cycle. Drops any residual entries (which should be zero at
        // cycle start in production; the reset is a defence-in-depth
        // against incomplete teardown).
        // -------------------------------------------------------------
        void ResetForCycle() noexcept;

    private:
        // 256 XObject* slots.
        XObject*                       m_entries[kCapacity];

        // Live-entry count. Atomic because Size() may be read by the
        // collector's monitor / a diagnostic thread.
        ::std::atomic<::std::uint32_t> m_count;

        // Peak observed count this cycle. Updated by Push when m_count
        // exceeds the current peak.
        ::std::atomic<::std::uint32_t> m_peakCount;
    };

    static_assert(sizeof(FXGrayQueue) == 2056,
                  "FXGrayQueue size lock: 256 * sizeof(XObject*) + "
                  "2 * sizeof(atomic<uint32>) = 2056 bytes (matches "
                  "FXObjectSatbQueue per the queue-family discipline).");
    static_assert(alignof(FXGrayQueue) == 8,
                  "FXGrayQueue alignof lock: 8 (matches XObject* "
                  "alignment).");
    static_assert(::std::is_trivially_destructible_v<FXGrayQueue>,
                  "FXGrayQueue must be trivially destructible "
                  "(TLS storage teardown via FMemory::Free; trivial "
                  "destructor matches the no-op cleanup).");

    // -----------------------------------------------------------------
    // FXGrayOverflowList -- process-singleton global overflow list.
    //
    // Aggregates spill from per-thread FXGrayQueue Push overflow paths.
    // The mark loop's owning thread refills its TLS queue from this
    // list when local is empty.
    //
    // CONCURRENCY: FCriticalSection-protected. The lock is acquired
    // briefly for AppendBatch (push N entries from a TLS overflow) and
    // PullBatch (refill up to N entries into a TLS queue). The lock
    // is NOT recursive.
    //
    // The list is a heap-grown buffer (FMemTag::Reflection). Growth
    // doubles capacity per grow; under-lock per the same waiver as
    // FXObjectGlobalSatbLog.
    // -----------------------------------------------------------------
    class FXGrayOverflowList
    {
    public:
        // -------------------------------------------------------------
        // Singleton accessor.
        // -------------------------------------------------------------
        [[nodiscard]] static FXGrayOverflowList& Get() noexcept;

        // -------------------------------------------------------------
        // AppendBatch -- spill N entries from a TLS queue's overflow.
        //
        // The entries are COPIED into the global list; the caller's
        // TLS queue is the owner of its slots and is responsible for
        // resetting m_count after the spill.
        // -------------------------------------------------------------
        void AppendBatch(XObject* const* Entries, ::std::size_t Count) noexcept;

        // -------------------------------------------------------------
        // PullBatch -- refill up to MaxCount entries into the caller's
        // buffer. Returns the number of entries actually pulled (0
        // means the global list is empty).
        //
        // The pull is LIFO from the global list's tail (recency-first;
        // matches the TLS queue's LIFO discipline).
        // -------------------------------------------------------------
        ::std::size_t PullBatch(XObject** OutEntries, ::std::size_t MaxCount) noexcept;

        // -------------------------------------------------------------
        // Size -- snapshot of the global list's count.
        // -------------------------------------------------------------
        [[nodiscard]] ::std::size_t Size() const noexcept;

        // -------------------------------------------------------------
        // IsEmpty -- snapshot predicate.
        // -------------------------------------------------------------
        [[nodiscard]] bool IsEmpty() const noexcept;

        // -------------------------------------------------------------
        // Test-only reset. Drops every entry; releases the buffer.
        // -------------------------------------------------------------
        void __ResetForTests() noexcept;

    private:
        FXGrayOverflowList() noexcept;
        ~FXGrayOverflowList() noexcept;

        FXGrayOverflowList(const FXGrayOverflowList&)            = delete;
        FXGrayOverflowList(FXGrayOverflowList&&)                 = delete;
        FXGrayOverflowList& operator=(const FXGrayOverflowList&) = delete;
        FXGrayOverflowList& operator=(FXGrayOverflowList&&)      = delete;

        // -------------------------------------------------------------
        // GrowIfNeededUnderLock -- expand the buffer to fit
        // RequiredSlots beyond m_count. Caller holds m_lock.
        // -------------------------------------------------------------
        void GrowIfNeededUnderLock(::std::size_t RequiredSlots) noexcept;

        // Heap-grown entry buffer; nullptr until first AppendBatch.
        XObject**                              m_entries;
        ::std::size_t                          m_count;
        ::std::size_t                          m_capacity;

        // Lock guarding all state.
        mutable ::XCore::HAL::FCriticalSection m_lock;
    };

    // -----------------------------------------------------------------
    // GetThreadGrayQueue -- TLS accessor for the calling thread's gray
    // queue. Mirrors the GetThreadSatbQueue pattern (Phase 5.f).
    //
    // First call on any thread lazy-creates the FXGrayQueue via
    // TModuleSafeThreadLocal::Get. The queue persists for the thread's
    // lifetime (or until the engine module unloads).
    // -----------------------------------------------------------------
    [[nodiscard]] FXGrayQueue& GetThreadGrayQueue() noexcept;

} // namespace XCore
