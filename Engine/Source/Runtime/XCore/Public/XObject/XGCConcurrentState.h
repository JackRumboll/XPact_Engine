// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XGCConcurrentState.h -- global concurrent-mark flag + global SATB log
// (XCoreXObject Rev 4 §4.3 + §4.7 + §5.6).
// =====================================================================
//
// XCoreXObject Rev 4 §4.3 ("Mostly-concurrent marking: the snapshot-
// at-the-beginning SATB discipline") + §4.7 (GC thread architecture)
// + §5.6 (SATB queue management) + Rev 2 FIX-A-MED-37 (SATB queue
// bound during quiesce).
//
// THIS HEADER PROVIDES:
//
//   * g_XGCIsConcurrentMarkActive  -- the global atomic<bool> the write
//                                      barrier checks on every store.
//                                      Phase 5.g sets/clears this around
//                                      the mark phase.
//
//   * g_XGCAcceptDrains            -- the global atomic<bool> gating
//                                      per-thread SATB queue drains
//                                      into the global log. Cleared by
//                                      XLiveCoding during hot-reload
//                                      quiesce; set otherwise. Phase
//                                      5.j wires this.
//
//   * FXObjectGlobalSatbLog        -- the global SATB log; aggregates
//                                      per-thread SATB queue drains.
//                                      The Phase 5.g mark phase consumes
//                                      this to maintain the SATB
//                                      invariant.
//
// =====================================================================
//
// CONCURRENT-MARK FLAG SEMANTICS:
//
// The mutator's write barrier (XGCWriteBarrier.h) checks
// g_XGCIsConcurrentMarkActive with memory_order_relaxed on EVERY store
// to a reflected reference slot. When false (the common case outside
// a GC cycle), the barrier short-circuits the SATB push and just
// dirties the card.
//
// When Phase 5.g enters the concurrent-mark phase, it stores `true`
// with memory_order_release; subsequent loads on mutator threads
// observe the flag (acquire-ordering ensures the snapshot of pre-mark
// state is consistent).
//
// At end-of-mark Phase 5.g stores `false` with memory_order_release.
// The barrier path then degrades to a card-only mark.
//
// RELAXED-LOAD RATIONALE.
//
// The barrier's check is memory_order_relaxed because the cost of
// acquire on EVERY store is unjustified -- the worst case if a stale
// "false" is loaded after the GC just flipped to "true" is one missed
// SATB push, which would mean one extra walk of the reference on the
// next cycle. This is BENIGN: SATB's correctness is "what was
// reachable at snapshot time stays marked"; missing a few stores in
// the boundary microseconds means those references' OLD values are
// captured by the safe-point root scan instead of the barrier (the
// safe-point itself is sequenced before mark, so the snapshot is
// complete).
//
// =====================================================================
//
// GLOBAL SATB LOG.
//
// Per-thread SATB queues (FXObjectSatbQueue, 256 entries) flush into
// the global log when full. The log is a TArray<XObject*> protected by
// FCriticalSection. The Phase 5.g mark phase drains the log into the
// gray queue.
//
// The log is bounded ONLY by available memory; the per-thread queues
// are bounded at 256 entries each per Rev 2 FIX-A-MED-37 (256 entries *
// 8 bytes * N threads = 16 KB on 8-thread machine; well within the
// 64 KB nominal bound).
//
// DESIGN NOTE: a lock-free MPSC queue would be more performant for the
// mark-phase drain, but the drain is RARE (typically once per ~256
// stores per thread = once every few thousand stores; for a tick with
// 100k stores across 8 threads, ~50 drains/sec). The FCriticalSection
// approach is simpler to reason about + the cost is amortised.
//
// A future Phase 2 may swap to TBoundedMpscQueue if profiling indicates
// drain contention is meaningful.
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
    // Global concurrent-mark active flag (per spec §4.3).
    //
    // True when Phase 5.g's mark phase is running concurrently with
    // the mutator. The write barrier checks this on every store.
    //
    // INITIAL VALUE: false. No mark phase is active at process start.
    // Phase 5.g sets this at concurrent-mark begin and clears at
    // concurrent-mark end.
    //
    // EXPOSED AS extern -- the load on the mutator's hot path needs to
    // resolve to the canonical storage location, not a per-TU
    // duplicate. The definition lives in
    // Private/XObject/XGCConcurrentState.cpp.
    // -----------------------------------------------------------------
    extern ::std::atomic<bool> g_XGCIsConcurrentMarkActive;

    // -----------------------------------------------------------------
    // Global SATB-drain accept flag (per Rev 2 FIX-A-MED-37 + spec
    // §5.6).
    //
    // True when per-thread SATB queue drains into the global log are
    // permitted. Cleared by XLiveCoding's BeginHotReloadQuiesce before
    // the class-replacement cascade; set again by
    // FinishHotReloadCascade.
    //
    // When this flag is false:
    //   * Per-thread SATB queues continue to receive entries from
    //     write-barriers (so post-quiesce collections see consistent
    //     state).
    //   * If a per-thread queue fills, the writer thread blocks on
    //     `g_XGCAcceptDrainsCV` (a condition variable) until the flag
    //     is set again.
    //   * Block is bounded by §11 acceptance criterion (e) cascade
    //     hot-patch timeout (<120 s nominal / <150 s kill).
    //
    // INITIAL VALUE: true. Drains are permitted by default.
    //
    // Phase 5.f exposes the flag + the wait/notify primitives; Phase
    // 5.j (hot-reload) sets/clears the flag at the quiesce boundary.
    // -----------------------------------------------------------------
    extern ::std::atomic<bool> g_XGCAcceptDrains;

    // -----------------------------------------------------------------
    // FXObjectGlobalSatbLog -- process-singleton aggregation log for
    // per-thread SATB queue drains.
    //
    // Owns a heap-grown buffer of XObject* entries. Each entry is an
    // OLD reference value captured by the write barrier; the Phase 5.g
    // mark phase drains the log and re-marks each entry.
    //
    // CONCURRENCY:
    //
    //   * AppendBatch acquires the lock briefly while it copies a
    //     batch of entries from a per-thread queue.
    //   * DrainAll acquires the lock for the duration of the drain;
    //     concurrent appends are blocked.
    //   * Size / IsEmpty are best-effort snapshots (acquire load on
    //     the size counter; no lock).
    //
    // OWNERSHIP / TEARDOWN:
    //
    // The log owns an FMemory-backed buffer (FMemTag::Reflection per
    // spec §4.5 trailing prose; the SATB log is reflection-runtime
    // metadata like the card table). Buffer growth uses the
    // "compute-need-under-shared / allocate-without-lock /
    // integrate-under-exclusive" pattern from the engine-wide lock
    // discipline (FRWLock.h Principle 2).
    //
    // Per Prime Directive: there is NO single "correct" growth
    // strategy; the log is unbounded in the common case (it grows to
    // accommodate the worst-case burst of SATB pushes during a long
    // mark cycle). The growth-doubling pattern keeps amortised
    // O(1)-per-push cost.
    // -----------------------------------------------------------------
    class FXObjectGlobalSatbLog
    {
    public:
        // -------------------------------------------------------------
        // Singleton accessor.
        //
        // Magic-static. Default-constructed; no allocation at first
        // access (the buffer is lazily allocated on first AppendBatch).
        // -------------------------------------------------------------
        [[nodiscard]] static FXObjectGlobalSatbLog& Get() noexcept;

        // -------------------------------------------------------------
        // Append a batch from a per-thread queue.
        //
        // Pre-condition: g_XGCAcceptDrains is true (the caller MUST
        // honour the gate; if false the caller blocks the writer
        // thread instead of calling AppendBatch).
        //
        // The Count entries are copied into the log; the caller's
        // queue is then cleared by the caller.
        // -------------------------------------------------------------
        void AppendBatch(XObject* const* Entries, ::std::size_t Count) noexcept;

        // -------------------------------------------------------------
        // Drain all entries into the visitor.
        //
        // Visitor signature: `void(XObject* OldValue)`. The visitor is
        // invoked under the log's lock; it MUST NOT call back into
        // AppendBatch (would self-deadlock; the lock is non-recursive).
        //
        // Returns the number of entries drained.
        //
        // The buffer is reset to empty after the drain; the underlying
        // storage is RETAINED (subsequent appends reuse it).
        // -------------------------------------------------------------
        template<typename Visitor>
        ::std::size_t DrainAll(Visitor&& V) noexcept
        {
            ::XCore::HAL::FScopedLock Lock(m_lock);
            const ::std::size_t Drained = m_count;
            for (::std::size_t I = 0; I < Drained; ++I)
            {
                V(m_entries[I]);
            }
            m_count = 0;
            return Drained;
        }

        // -------------------------------------------------------------
        // Diagnostic / test API.
        //
        // Size: best-effort snapshot of the current count.
        // -------------------------------------------------------------
        [[nodiscard]] ::std::size_t Size() const noexcept;

        // -------------------------------------------------------------
        // Test-only reset. Drops every entry; releases the buffer.
        // -------------------------------------------------------------
        void __ResetForTests() noexcept;

    private:
        FXObjectGlobalSatbLog() noexcept;
        ~FXObjectGlobalSatbLog() noexcept;

        FXObjectGlobalSatbLog(const FXObjectGlobalSatbLog&)            = delete;
        FXObjectGlobalSatbLog(FXObjectGlobalSatbLog&&)                 = delete;
        FXObjectGlobalSatbLog& operator=(const FXObjectGlobalSatbLog&) = delete;
        FXObjectGlobalSatbLog& operator=(FXObjectGlobalSatbLog&&)      = delete;

        // -------------------------------------------------------------
        // GrowIfNeededUnderLock -- ensure the buffer has at least
        // `RequiredSlots` slots beyond m_count. The caller holds
        // m_lock exclusively. Reallocates via FMemory under the lock
        // because the SATB log is reflection-runtime metadata + the
        // grow is rare (capacity doubles per grow); the engine-wide
        // lock-discipline waiver here is the same as FXObjectArray's
        // GrowFreeListUnderLock (FXObjectArray.h:498).
        // -------------------------------------------------------------
        void GrowIfNeededUnderLock(::std::size_t RequiredSlots) noexcept;

        // Heap-grown entry buffer. nullptr until first AppendBatch
        // allocates the initial capacity. FMemTag::Reflection per spec
        // §4.5 trailing prose.
        XObject**                  m_entries;

        // Live entry count.
        ::std::size_t              m_count;

        // Buffer capacity (allocated slot count).
        ::std::size_t              m_capacity;

        // Single lock guarding the buffer + count + capacity. Non-
        // recursive (the lock is acquired briefly per append; the
        // drain visitor MUST NOT recurse into AppendBatch).
        mutable ::XCore::HAL::FCriticalSection m_lock;
    };

    // -----------------------------------------------------------------
    // XGCWaitForDrainsAccepted -- block until g_XGCAcceptDrains is true.
    //
    // Called by a per-thread SATB queue when it fills AND
    // g_XGCAcceptDrains is false (hot-reload quiesce window). The
    // calling thread spin-yields (Sleep(0)/Sleep(1)) until the flag is
    // set again; the wait is bounded by §11 acceptance criterion (e)
    // cascade hot-patch timeout (<120 s nominal).
    //
    // This is a coarse-grained primitive: there is no condition
    // variable backing the wait because:
    //   1. The wait is RARE (hot-reload happens engineer-station-only).
    //   2. The wait is bounded by the cascade timeout, not by memory.
    //   3. A condvar would require the writer threads to acquire a
    //      mutex on the wait path, which would itself serialise SATB
    //      pushes under contention.
    // A spin-yield loop is the principled posture: no contention in
    // steady state (the flag is always true outside hot-reload), and
    // the hot-reload code-path is engineer-only.
    //
    // PHASE 5.F SHIPS the spin-yield primitive; Phase 5.j wires the
    // hot-reload flag.
    // -----------------------------------------------------------------
    void XGCWaitForDrainsAccepted() noexcept;

} // namespace XCore
