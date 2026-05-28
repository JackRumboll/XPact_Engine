// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FXDeferredDestructionQueue.h -- Phase 5.h two-phase destruction queue
// (XCoreXObject Rev 4 §2.5 + §4.2 step 7 + Rev 2 design decision O9).
// =====================================================================
//
// XCoreXObject Rev 4 §4.2 step 7 (deferred destruction):
//
//   "After the sweep phase enqueues each unreachable object with its
//    BeginDestroy slot dispatched, the deferred-destruction queue is
//    drained one-pass-per-frame on the sim-tick boundary. For each
//    queued object, the engine calls IsReadyForFinishDestroy via the
//    lifecycle table; if true, FinishDestroy is dispatched, the
//    FXObjectAllocator cell is released, and the FXObjectArray slot is
//    returned to the LIFO free list with SerialNumber bumped.
//    Otherwise the object remains in the queue for a future pass, with
//    a per-object timeout that forces FinishDestroy after a bounded
//    wall-time window."
//
// Per Rev 2 design decision O9: "Deferred-destruction queue draining:
// one pass per frame, time-budgeted (max 1 ms of FinishDestroy calls
// per frame)".
//
// =====================================================================
//
// QUEUE SHAPE -- MPSC-style:
//
//   * Producer: the Phase 5.h sweep phase (one thread; either the
//     synchronous CollectGarbage caller or the dedicated marker thread).
//     EnqueueAfterBeginDestroy is the producer entry point.
//
//   * Consumer: the engine main thread at the sim-tick boundary. The
//     DrainOnePassWithBudget call is the consumer entry point.
//
//   * The producer/consumer never overlap in time across a single
//     cycle (sweep produces; sim-tick drains; no race). The lock is
//     therefore uncontended in the steady-state. The lock is the same
//     FCriticalSection pattern FXSweepCandidateQueue uses.
//
// =====================================================================
//
// PER-ENTRY STATE -- {InternalIndex, FirstSeenSeconds}:
//
//   * InternalIndex is the FXObjectArray slot index identifying the
//     XObject pending destruction. The XObject* itself is NOT stored
//     because the slot identity is sufficient for the consumer's
//     IsReadyForFinishDestroy dispatch + FinishDestroy dispatch + slot
//     return (the consumer fetches the XObject* via
//     FXObjectArray::GetObjectAtIndexUnchecked).
//
//   * FirstSeenSeconds captures the FPlatformTime::Seconds() value at
//     enqueue time. The consumer uses this to enforce a per-object
//     timeout: if (now - FirstSeenSeconds) >= kFXDeferredDestructionTimeoutSeconds
//     the consumer forces FinishDestroy regardless of
//     IsReadyForFinishDestroy's return. This is the spec §4.2 step 7
//     trailing prose ("or after a per-object timeout") + the prompt's
//     corner-case clause.
//
//   * Per Prime Directive: the timeout is 1 second of wall time across
//     drain passes. Rationale: most lifecycle hooks complete in microseconds
//     (the IsReadyForFinishDestroy slot is typically a one-cycle bool-load
//     of a "deferred refs cleared" flag); a 1 s ceiling tolerates any
//     legitimate "wait for async I/O to release file handles" pattern
//     while bounding the worst-case slot-release latency to a fraction
//     of a second of perceptible lag. Configurable as a future tuneable
//     (no runtime CVar discipline per XCore-4a; constant for MVP).
//
// =====================================================================
//
// DRAIN PASS BUDGET -- per Rev 2 design decision O9:
//
//   * DrainOnePassWithBudget(int64_t budget_us) processes pending
//     entries until either: (a) the queue is empty, OR (b) the
//     consumer has spent budget_us microseconds processing entries.
//
//   * The 1 ms default budget aligns with the prompt + Rev 2 wording.
//
//   * Entries that fail IsReadyForFinishDestroy on this pass are left
//     in the queue (re-enqueued at the end of the pass) for a future
//     pass. The entries-not-yet-ready re-enqueue is internal; the
//     producer never re-enqueues.
//
// =====================================================================
//
// THREAD SAFETY:
//
//   * The queue is shared between the sweep producer and the sim-tick
//     consumer. Both acquire m_lock (FCriticalSection); the contention
//     is structurally zero (sweep and sim-tick never run simultaneously
//     in the MVP single-marker-thread design).
//
//   * Per-entry state (InternalIndex + FirstSeenSeconds) is captured at
//     enqueue time; the consumer reads it under the lock. The XObject
//     pointed at by InternalIndex MAY be touched by the consumer's
//     lifecycle-table dispatch -- the consumer takes the lock for the
//     read of the queue entry, RELEASES the lock before dispatching
//     IsReadyForFinishDestroy / FinishDestroy (the lifecycle slots
//     may themselves take other locks; calling them under our queue
//     lock would risk lock-order inversion). Re-acquires the lock for
//     the next pop.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "HAL/FCriticalSection.h"

#include <cstddef>
#include <cstdint>

namespace XCore
{
    // -----------------------------------------------------------------
    // Tunables (constexpr per the no-runtime-CVar discipline).
    // -----------------------------------------------------------------

    // Per-object timeout for IsReadyForFinishDestroy polling. After
    // this many seconds across drain passes, the consumer forces
    // FinishDestroy regardless of the slot's return value. 1.0 seconds
    // is the Prime-Directive choice per the rationale at the head of
    // this header.
    inline constexpr double kFXDeferredDestructionTimeoutSeconds = 1.0;

    // Default per-pass budget (microseconds). Matches Rev 2 design
    // decision O9 ("max 1 ms of FinishDestroy calls per frame").
    inline constexpr ::std::int64_t kFXDeferredDestructionDefaultBudgetUs = 1'000;

    // Initial buffer capacity (matches the FXSweepCandidateQueue
    // initial-capacity discipline).
    inline constexpr ::std::size_t kFXDeferredDestructionInitialCapacity = 256;

    // -----------------------------------------------------------------
    // FXDeferredDestructionQueue -- process-singleton lifecycle queue.
    //
    // Holds objects whose BeginDestroy has been called and which are
    // waiting for IsReadyForFinishDestroy to return true (or for the
    // per-object timeout to elapse). Drained one-pass-per-frame from
    // the sim-tick boundary.
    //
    // Accessed via the singleton accessor Get(). The ctor does NOT
    // allocate the buffer; it is lazily allocated on first
    // EnqueueAfterBeginDestroy.
    // -----------------------------------------------------------------
    class FXDeferredDestructionQueue
    {
    public:
        // -------------------------------------------------------------
        // Singleton accessor.
        //
        // Magic-static. The ctor is trivial; the destructor frees the
        // heap buffer.
        // -------------------------------------------------------------
        [[nodiscard]] static FXDeferredDestructionQueue& Get() noexcept;

        // -------------------------------------------------------------
        // EnqueueAfterBeginDestroy -- producer entry point.
        //
        // Called by the Phase 5.h sweep phase after BeginDestroy has
        // been dispatched on the XObject at InternalIndex (and after
        // the EObjectFlags::BeginDestroyed transition + the
        // kPendingDestroyBit mirror have been set). The queue records
        // the slot identity + the current wall time so the consumer's
        // timeout policy can fire.
        //
        // Pre-conditions:
        //   * InternalIndex > 0 (not the null sentinel).
        //   * BeginDestroy has already been dispatched on the XObject.
        //   * The XObject's EObjectFlags::BeginDestroyed bit is set.
        //   * FXObjectArrayEntry.StateBits.kPendingDestroyBit is set.
        //
        // The queue does NOT validate these pre-conditions (the sweep
        // is the sole producer; the contract is held at the call site).
        // -------------------------------------------------------------
        void EnqueueAfterBeginDestroy(::std::int32_t InternalIndex) noexcept;

        // -------------------------------------------------------------
        // DrainOnePassWithBudget -- consumer entry point.
        //
        // Called by the engine main thread at the sim-tick boundary.
        // Drains pending entries until either:
        //   (a) the queue is empty,
        //   (b) the consumer has spent budget_us microseconds processing
        //       entries.
        //
        // For each entry:
        //   1. Fetch the XObject* via FXObjectArray::GetObjectAtIndex
        //      Unchecked (the slot has not yet been released).
        //   2. Dispatch IsReadyForFinishDestroy via the FClass's
        //      lifecycle table. If the slot is unimplemented, the spec
        //      §2.8 default is TRUE.
        //   3. Check the per-object timeout: if elapsed >=
        //      kFXDeferredDestructionTimeoutSeconds, force-finalise.
        //   4. If ready OR timeout: dispatch FinishDestroy, set
        //      EObjectFlags::FinishDestroyed, deallocate the
        //      FXObjectAllocator cell, ReleaseSlot the FXObjectArray
        //      entry.
        //   5. Otherwise: re-enqueue at the tail of the queue for a
        //      future pass.
        //
        // Returns the number of slots fully finalised on this pass.
        //
        // BUDGET ENFORCEMENT: the budget is checked AFTER each entry is
        // processed (the per-entry cost is bounded; a single entry that
        // exceeds the budget still completes). This is the standard
        // "best-effort budgeting" posture (the alternative -- aborting
        // an entry mid-dispatch -- is not safe).
        // -------------------------------------------------------------
        ::std::size_t DrainOnePassWithBudget(
            ::std::int64_t BudgetUs = kFXDeferredDestructionDefaultBudgetUs) noexcept;

        // -------------------------------------------------------------
        // Size -- snapshot of the pending entry count.
        // -------------------------------------------------------------
        [[nodiscard]] ::std::size_t Size() const noexcept;

        // -------------------------------------------------------------
        // IsEmpty -- snapshot predicate.
        // -------------------------------------------------------------
        [[nodiscard]] bool IsEmpty() const noexcept;

        // -------------------------------------------------------------
        // Last-pass diagnostics. Updated after each DrainOnePassWithBudget
        // call.
        // -------------------------------------------------------------

        // Number of FinishDestroy calls dispatched on the last pass.
        [[nodiscard]] ::std::size_t GetLastPassFinishDestroyCount() const noexcept;

        // Number of slots forced past the timeout on the last pass.
        [[nodiscard]] ::std::size_t GetLastPassTimeoutForcedCount() const noexcept;

        // Wall-clock microseconds spent in the last DrainOnePassWithBudget.
        [[nodiscard]] ::std::int64_t GetLastPassDurationUs() const noexcept;

        // -------------------------------------------------------------
        // Test-only reset. Drops every queued entry; releases the
        // buffer. Does NOT call any lifecycle hooks (the entries are
        // forgotten).
        // -------------------------------------------------------------
        void __ResetForTests() noexcept;

    private:
        FXDeferredDestructionQueue() noexcept;
        ~FXDeferredDestructionQueue() noexcept;

        FXDeferredDestructionQueue(const FXDeferredDestructionQueue&)            = delete;
        FXDeferredDestructionQueue(FXDeferredDestructionQueue&&)                 = delete;
        FXDeferredDestructionQueue& operator=(const FXDeferredDestructionQueue&) = delete;
        FXDeferredDestructionQueue& operator=(FXDeferredDestructionQueue&&)      = delete;

        // -------------------------------------------------------------
        // FEntry -- per-pending-object record.
        //
        // 16 bytes: 4-byte InternalIndex + 4-byte _pad + 8-byte
        // FirstSeenSeconds. Aligned to 8 so the heap buffer alignment
        // is uniform.
        // -------------------------------------------------------------
        struct alignas(8) FEntry
        {
            ::std::int32_t InternalIndex;       // 0  +4
            ::std::uint32_t _pad;                // 4  +4
            double         FirstSeenSeconds;     // 8  +8
        };

        // -------------------------------------------------------------
        // GrowIfNeededUnderLock -- expand the buffer to fit RequiredSlots
        // beyond m_count. Mirror of FXSweepCandidateQueue's grower.
        // -------------------------------------------------------------
        void GrowIfNeededUnderLock(::std::size_t RequiredSlots) noexcept;

        // Heap-grown entry buffer.
        FEntry*        m_entries;
        ::std::size_t  m_count;
        ::std::size_t  m_capacity;

        // Last-pass diagnostics.
        ::std::size_t  m_lastPassFinishDestroyCount;
        ::std::size_t  m_lastPassTimeoutForcedCount;
        ::std::int64_t m_lastPassDurationUs;

        // Lock guarding all state.
        mutable ::XCore::HAL::FCriticalSection m_lock;
    };

} // namespace XCore
