// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FXSweepCandidateQueue.h -- producer queue for the Phase 5.h sweep
// consumer (XCoreXObject Rev 4 §4.2 step 6 + Phase 5.g handoff).
// =====================================================================
//
// XCoreXObject Rev 4 §4.2 step 6 (sweep phase): "walk the FXObjectArray;
// for each unmarked, non-pinned, live entry, set the PendingDestroy
// bit and enqueue onto the deferred-destruction queue".
//
// Phase 5.g produces (via the end-of-mark rotating-flag check) the
// queue of InternalIndex entries that are CANDIDATES FOR SWEEP this
// cycle. Phase 5.h is the consumer that actually performs the
// PendingDestroy bit set + the BeginDestroy / FinishDestroy lifecycle
// dispatch.
//
// PRODUCER (Phase 5.g): at end-of-mark, the collector walks every
// committed FXObjectArrayEntry and tests:
//     reachable = (XObject::ReachabilityFlag &
//                  CurrentCycleReachabilityMask) != 0;
// If !reachable AND !pinned AND !refcounted AND Object != nullptr,
// the entry's InternalIndex is appended to this queue.
//
// CONSUMER (Phase 5.h; not in Phase 5.g): the sweep thread drains
// this queue and dispatches per-object teardown.
//
// =====================================================================
//
// QUEUE SHAPE -- SPSC (single-producer / single-consumer).
//
// The producer is the GC marker thread (one thread). The consumer is
// the GC sweep thread (one thread; Phase 5.h). The SPSC discipline
// matches XCore-4a's TSpscQueue but the Phase 5.g implementation
// uses a simpler heap-grown vector under an FCriticalSection lock:
//
//   * Producer + consumer never overlap in time (producer finishes
//     at end-of-mark; consumer starts at begin-of-sweep). The lock
//     is therefore uncontended in practice.
//
//   * The queue is bounded by the heap committed-capacity (~50k
//     typical at Foundation Prototype scale); the heap-grown buffer
//     starts at kInitialCapacity (256) and doubles per grow.
//
//   * Phase 5.h can swap to TSpscQueue if profiling indicates the
//     lock contention is meaningful; the surface (Append + Drain)
//     is unchanged.
//
// =====================================================================
//
// ELEMENT TYPE -- int32 InternalIndex (NOT XObject*).
//
// Rationale (Prime Directive judgement; documented in the Phase 5.g
// .Build.toml block):
//
//   * The sweep needs to access the FXObjectArrayEntry for the
//     candidate to set PendingDestroy + read other state bits. It
//     fetches the entry via Array[InternalIndex], which is faster
//     than dereferencing the XObject* to read InternalIndex back
//     (the InternalIndex is at XObject@8 -- a load through the
//     XObject pointer is a 1-2 cycle hit on the L1d cache).
//
//   * The XObject* itself might be nullptr in a corner case (a
//     concurrent FreeEntry during sweep is structurally impossible
//     in the Phase 5.h discipline but the int32 is the canonical
//     identity used throughout FXObjectArray's API surface).
//
//   * Memory footprint: 4 bytes per entry vs 8 bytes for the
//     pointer. At 50k candidates the queue is 200 KB vs 400 KB.
//     Within budget either way; the smaller variant is principled.
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
    // FXSweepCandidateQueue -- process-singleton SPSC queue of
    // InternalIndex values pending sweep.
    //
    // Producer: Phase 5.g end-of-mark candidate enumeration.
    // Consumer: Phase 5.h sweep phase (NOT implemented in Phase 5.g).
    //
    // Accessed via the singleton accessor Get().
    // -----------------------------------------------------------------
    class FXSweepCandidateQueue
    {
    public:
        // -------------------------------------------------------------
        // Singleton accessor.
        //
        // Magic-static. The ctor does NOT allocate the buffer; it is
        // lazily allocated on first Append.
        // -------------------------------------------------------------
        [[nodiscard]] static FXSweepCandidateQueue& Get() noexcept;

        // -------------------------------------------------------------
        // Append -- producer-side enqueue of one InternalIndex.
        //
        // Called from the Phase 5.g end-of-mark candidate sweep. The
        // queue grows its buffer on demand; under-lock per the same
        // waiver as FXObjectGlobalSatbLog.
        // -------------------------------------------------------------
        void Append(::std::int32_t InternalIndex) noexcept;

        // -------------------------------------------------------------
        // AppendBatch -- producer-side enqueue of N indices.
        //
        // Bulk-write path; reduces lock-acquisition cost for the
        // typical "produce 50k candidates at end-of-mark" workload.
        // -------------------------------------------------------------
        void AppendBatch(const ::std::int32_t* Entries, ::std::size_t Count) noexcept;

        // -------------------------------------------------------------
        // DrainAll -- consumer-side drain. Phase 5.h consumer.
        //
        // Visitor signature: `void(int32_t InternalIndex)`. The visitor
        // is invoked once per entry in FIFO order. The queue is
        // cleared after the drain.
        // -------------------------------------------------------------
        template <typename Visitor>
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
        // Size -- snapshot of the live count.
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
        FXSweepCandidateQueue() noexcept;
        ~FXSweepCandidateQueue() noexcept;

        FXSweepCandidateQueue(const FXSweepCandidateQueue&)            = delete;
        FXSweepCandidateQueue(FXSweepCandidateQueue&&)                 = delete;
        FXSweepCandidateQueue& operator=(const FXSweepCandidateQueue&) = delete;
        FXSweepCandidateQueue& operator=(FXSweepCandidateQueue&&)      = delete;

        // -------------------------------------------------------------
        // GrowIfNeededUnderLock -- expand the buffer to fit
        // RequiredSlots beyond m_count.
        // -------------------------------------------------------------
        void GrowIfNeededUnderLock(::std::size_t RequiredSlots) noexcept;

        // Heap-grown int32 buffer.
        ::std::int32_t*                        m_entries;
        ::std::size_t                          m_count;
        ::std::size_t                          m_capacity;

        // Lock guarding all state.
        mutable ::XCore::HAL::FCriticalSection m_lock;
    };

} // namespace XCore
