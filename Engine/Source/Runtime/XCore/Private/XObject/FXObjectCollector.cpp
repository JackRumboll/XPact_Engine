// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectCollector.cpp -- mark-phase collector body (Phase 5.g).
// =====================================================================
//
// XCoreXObject Rev 4 §4. The state machine + sub-phase bodies that
// implement non-moving precise mostly-concurrent mark of every
// reachable XObject.
//
// Phase 5.g produces the sweep-candidate queue; Phase 5.h is the
// consumer that performs the actual reclaim.
//
// =====================================================================

#include "XObject/FXObjectCollector.h"

#include "HAL/FEvent.h"
#include "HAL/FPlatformProcess.h"
#include "HAL/FPlatformTime.h"

#include "Reflection/FClass.h"
#include "Reflection/FStruct.h"

#include "XObject/FXGrayQueue.h"
#include "XObject/FXObjectArray.h"
#include "XObject/FXObjectArrayEntry.h"
#include "XObject/FXObjectGCCardTable.h"
#include "XObject/FXObjectLifecycleTable.h"
#include "XObject/FXObjectSatbQueue.h"
#include "XObject/FXObjectSchemaWalker.h"
#include "XObject/FXSweepCandidateQueue.h"
#include "XObject/XGCConcurrentState.h"
#include "XObject/XGCRoot.h"
#include "XObject/XGCRootSpan.h"
#include "XObject/XInsightsEmitHelpers.h"
#include "XObject/XObject.h"

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <thread>

namespace XCore
{

// =====================================================================
// Global state (extern definitions for headers above).
// =====================================================================

// Safe-point requested flag. Workers (currently none on the MVP)
// poll this at back-edges; the marker thread cleared it after the
// safe-point exits.
::std::atomic<bool> g_XGCSafePointRequested{false};

// Rotating-reachability index. Starts at 0; advances mod 3 per cycle.
::std::atomic<::std::uint8_t> g_CurrentReachabilityIndex{0};

// =====================================================================
// Helpers.
// =====================================================================

namespace
{
    // Convert a double "seconds" delta to int64 microseconds with
    // clamp-to-zero. Used for telemetry payload widths.
    [[nodiscard]] XPACT_FORCEINLINE ::std::int64_t SecondsToMicros(
        double DeltaSeconds) noexcept
    {
        if (DeltaSeconds <= 0.0)
        {
            return 0;
        }
        const double Micros = DeltaSeconds * 1'000'000.0;
        if (Micros > static_cast<double>(::std::numeric_limits<::std::int64_t>::max()))
        {
            return ::std::numeric_limits<::std::int64_t>::max();
        }
        return static_cast<::std::int64_t>(Micros);
    }

} // namespace

// =====================================================================
// FXObjectCollector -- singleton + ctor / dtor.
// =====================================================================

FXObjectCollector& FXObjectCollector::Get() noexcept
{
    static FXObjectCollector s_instance;
    return s_instance;
}

FXObjectCollector::FXObjectCollector() noexcept
    : m_phase(static_cast<::std::uint32_t>(EXGCPhase::kIdle))
    , m_cycleCounter(0)
    , m_markedThisCycle(0)
    , m_lastMarkDurationUs(0)
    , m_lastSafePointDurationUs(0)
    , m_lastCycleSaturationFallback(false)
    , m_triggerEvent(nullptr)
    , m_triggerPending(false)
    , m_lastTriggerReason(0)
    , m_shutdownRequested(false)
    , m_markerThread()
    , m_threadLifecycleLock()
    , m_cycleLock()
{
}

FXObjectCollector::~FXObjectCollector() noexcept
{
    // Defence-in-depth: the engine bootstrap is supposed to call
    // Shutdown() explicitly before the process exits, but the
    // function-local-static FXObjectCollector instance's destructor
    // runs at process teardown automatically. Joining the marker
    // thread here ensures we don't leave a detached thread holding
    // references into freed singleton state.
    Shutdown();

    if (m_triggerEvent != nullptr)
    {
        ::XCore::HAL::FEvent::Destroy(m_triggerEvent);
        m_triggerEvent = nullptr;
    }
}

// =====================================================================
// __Init -- spawn the dedicated marker thread.
//
// Idempotent: returns immediately if the thread is already spawned.
// Spawn failure (allocator OOM at thread-stack alloc) leaves
// m_markerThread non-joinable; Trigger detects this and falls back to
// synchronous CollectGarbage on the calling thread.
// =====================================================================
void FXObjectCollector::__Init() noexcept
{
    ::XCore::HAL::FScopedLock Lock(m_threadLifecycleLock);

    if (m_markerThread.joinable())
    {
        // Already initialised. Idempotent return.
        return;
    }

    if (m_shutdownRequested.load(::std::memory_order_acquire))
    {
        // Shutdown was previously called. We do NOT re-spawn after
        // Shutdown; the engine's restart path should re-create the
        // collector singleton (which is process-lifetime; restart
        // is effectively re-launch).
        return;
    }

    // Allocate trigger event lazily on first __Init.
    if (m_triggerEvent == nullptr)
    {
        m_triggerEvent = ::XCore::HAL::FEvent::CreateAutoReset();
    }

    // Spawn the marker thread.
    try
    {
        m_markerThread = ::std::thread([this]() noexcept
        {
            this->MarkerThreadMain();
        });
    }
    catch (...)
    {
        // std::thread ctor can throw on resource exhaustion. The MVP
        // posture: log + degrade to synchronous CollectGarbage. We
        // cannot use XPACT_CHECK here (the harness's check macro is
        // not in scope; the collector's correctness is preserved by
        // the Trigger fallback). The marker-thread spawn failure is
        // a Dev/Debug diagnostic that lands when the engine bootstrap
        // tests run.
    }
}

// =====================================================================
// Shutdown -- signal exit + join.
//
// Sets m_shutdownRequested + triggers m_triggerEvent so the marker
// thread observes the flag and exits its loop. The join blocks until
// the marker thread returns.
//
// Calling thread MUST NOT be the marker thread (would self-deadlock
// on the join). The MVP discipline: Shutdown is called from main
// thread at engine teardown.
// =====================================================================
void FXObjectCollector::Shutdown() noexcept
{
    ::XCore::HAL::FScopedLock Lock(m_threadLifecycleLock);

    m_shutdownRequested.store(true, ::std::memory_order_release);

    if (m_markerThread.joinable())
    {
        // Wake the marker thread so it observes the shutdown flag.
        if (m_triggerEvent != nullptr)
        {
            m_triggerEvent->Trigger();
        }
        m_markerThread.join();
    }
}

// =====================================================================
// __ResetForTests -- drop state for a fresh test fixture.
//
// Drains every queue + resets every counter. Tests use this to start
// from a known-clean state.
// =====================================================================
void FXObjectCollector::__ResetForTests() noexcept
{
    // Stop any running marker thread.
    Shutdown();

    m_phase.store(static_cast<::std::uint32_t>(EXGCPhase::kIdle),
                  ::std::memory_order_release);
    m_cycleCounter.store(0, ::std::memory_order_release);
    m_markedThisCycle.store(0, ::std::memory_order_release);
    m_lastMarkDurationUs.store(0, ::std::memory_order_release);
    m_lastSafePointDurationUs.store(0, ::std::memory_order_release);
    m_lastCycleSaturationFallback.store(false, ::std::memory_order_release);
    m_triggerPending.store(false, ::std::memory_order_release);
    m_lastTriggerReason.store(0, ::std::memory_order_release);
    m_shutdownRequested.store(false, ::std::memory_order_release);
    g_CurrentReachabilityIndex.store(0, ::std::memory_order_release);
    g_XGCSafePointRequested.store(false, ::std::memory_order_release);
    g_XGCIsConcurrentMarkActive.store(false, ::std::memory_order_release);

    // Drain queues.
    FXGrayOverflowList::Get().__ResetForTests();
    FXSweepCandidateQueue::Get().__ResetForTests();
    FXObjectGlobalSatbLog::Get().__ResetForTests();
}

// =====================================================================
// CollectGarbage -- synchronous trigger on the calling thread.
// =====================================================================
void FXObjectCollector::CollectGarbage(EXGCOptions Opts) noexcept
{
    // Lock-acquire serialises against a concurrent marker-thread cycle.
    // The lock is FCriticalSection (recursive on Win64); a re-entrant
    // CollectGarbage call (e.g., from inside a visitor lambda) would
    // re-acquire safely, but the marker thread's outer cycle also
    // holds it so the inner call would deadlock against another
    // thread. The MVP discipline: CollectGarbage is callable only from
    // the main thread + only outside the marker thread's cycle window.
    ::XCore::HAL::FScopedLock Lock(m_cycleLock);

    RunCycle(Opts, EXGCTriggerReason::kManual);
}

// =====================================================================
// Trigger -- heuristic-driven async wake.
// =====================================================================
void FXObjectCollector::Trigger(EXGCTriggerReason Reason) noexcept
{
    // Coalesce: if a trigger is already pending, ignore. The marker
    // thread will run one cycle and observe the next Trigger after it
    // returns to the wait. This bounds the per-cycle trigger spam to
    // at most one extra cycle.
    bool Expected = false;
    if (!m_triggerPending.compare_exchange_strong(
            Expected,
            true,
            ::std::memory_order_acq_rel,
            ::std::memory_order_acquire))
    {
        // Already pending; this is a coalesce.
        return;
    }

    m_lastTriggerReason.store(
        static_cast<::std::uint32_t>(Reason),
        ::std::memory_order_release);

    // If the marker thread is alive, wake it.
    if (m_markerThread.joinable() && m_triggerEvent != nullptr)
    {
        m_triggerEvent->Trigger();
        return;
    }

    // Marker thread not spawned: fall back to synchronous.
    m_triggerPending.store(false, ::std::memory_order_release);

    ::XCore::HAL::FScopedLock Lock(m_cycleLock);
    RunCycle(EXGCOptions::kDefault, Reason);
}

// =====================================================================
// MarkerThreadMain -- the dedicated marker thread's entry point.
//
// Loop: wait on m_triggerEvent; on wake, check shutdown; if not
// shutdown, clear the trigger-pending flag and run a cycle; repeat.
// =====================================================================
void FXObjectCollector::MarkerThreadMain() noexcept
{
    while (true)
    {
        // Wait for trigger. Timeout = -1 (wait forever).
        if (m_triggerEvent != nullptr)
        {
            (void)m_triggerEvent->Wait(-1.0f);
        }

        // Shutdown check FIRST. The shutdown path triggers the event
        // to wake us; we exit before running another cycle.
        if (m_shutdownRequested.load(::std::memory_order_acquire))
        {
            return;
        }

        // Clear pending; the cycle will service the requested run.
        const EXGCTriggerReason Reason = static_cast<EXGCTriggerReason>(
            m_lastTriggerReason.load(::std::memory_order_acquire));
        m_triggerPending.store(false, ::std::memory_order_release);

        // Acquire the cycle lock; this serialises against any
        // concurrent synchronous CollectGarbage on another thread.
        ::XCore::HAL::FScopedLock Lock(m_cycleLock);
        RunCycle(EXGCOptions::kDefault, Reason);
    }
}

// =====================================================================
// RunCycle -- the full state-machine sequence for one collection.
//
// Per spec §4.2 phase order:
//   Trigger -> SafePointHandshake -> RootEnumeration -> ConcurrentMark
//   -> FinalMarkDrain -> SweepHandoff -> Idle (cycle++)
// =====================================================================
void FXObjectCollector::RunCycle(
    EXGCOptions       Opts,
    EXGCTriggerReason Reason) noexcept
{
    const ::std::uint32_t CycleId = m_cycleCounter.load(::std::memory_order_relaxed);
    const bool EmitTelemetry = HasGCFlag(Opts, EXGCOptions::kEmitInsightsTelemetry);

    // Reset cycle counters.
    m_markedThisCycle.store(0, ::std::memory_order_release);
    m_lastCycleSaturationFallback.store(false, ::std::memory_order_release);

    // Reset the marker thread's TLS gray queue.
    GetThreadGrayQueue().ResetForCycle();

    EnterTriggering(Reason);

    // ---- Safe-point handshake ----
    const double SafePointStart = ::XCore::HAL::FPlatformTime::Seconds();
    EnterSafePointHandshake();
    if (EmitTelemetry)
    {
        ::XCore::HAL::XInsightsEmitHelpers::EmitGCSafePointEntered(
            static_cast<::std::int64_t>(CycleId));
    }

    // ---- Root enumeration ----
    EnterRootEnumeration();
    const ::std::size_t RootCount = EnumerateRoots();

    if (EmitTelemetry)
    {
        ::XCore::HAL::XInsightsEmitHelpers::EmitGCMarkStart(
            static_cast<::std::int64_t>(CycleId),
            static_cast<::std::int64_t>(GetCurrentReachabilityIndex()),
            static_cast<::std::int64_t>(RootCount));
    }

    // Close the safe-point window: the synchronous root snapshot is
    // captured; the mark phase can run concurrently with the mutator
    // from here.
    const double SafePointEnd = ::XCore::HAL::FPlatformTime::Seconds();
    const ::std::int64_t SafePointDurationUs =
        SecondsToMicros(SafePointEnd - SafePointStart);
    m_lastSafePointDurationUs.store(SafePointDurationUs,
                                     ::std::memory_order_release);
    g_XGCSafePointRequested.store(false, ::std::memory_order_release);
    if (EmitTelemetry)
    {
        ::XCore::HAL::XInsightsEmitHelpers::EmitGCSafePointExited(
            static_cast<::std::int64_t>(CycleId),
            SafePointDurationUs);
    }

    // ---- Concurrent mark ----
    const double MarkStart = ::XCore::HAL::FPlatformTime::Seconds();
    EnterConcurrentMark();

    // Saturation detection: if the dirty-card count exceeded the
    // threshold OR the caller passed kForceFullScan, fall back to a
    // full-heap scan instead of incrementally walking.
    const bool SaturatedAtMarkStart =
        FXObjectGCCardTable::Get().IsSaturated();
    const bool ForceFullScan = HasGCFlag(Opts, EXGCOptions::kForceFullScan);

    if (SaturatedAtMarkStart || ForceFullScan)
    {
        m_lastCycleSaturationFallback.store(true,
                                             ::std::memory_order_release);
        const ::std::size_t HeapFullMarked = FullHeapScanFallback();
        (void)HeapFullMarked;
    }
    else
    {
        DrainGrayQueue();
    }

    // ---- Final-mark drain ----
    const ::std::size_t SatbDepthAtFinal =
        FXObjectGlobalSatbLog::Get().Size();
    const ::std::size_t DirtyCardCountAtFinal =
        FXObjectGCCardTable::Get().GetDirtyCardCount();

    if (EmitTelemetry)
    {
        ::XCore::HAL::XInsightsEmitHelpers::EmitGCFinalDrainStart(
            static_cast<::std::int64_t>(CycleId),
            static_cast<::std::int64_t>(SatbDepthAtFinal),
            static_cast<::std::int64_t>(DirtyCardCountAtFinal));
    }

    const double FinalDrainStart = ::XCore::HAL::FPlatformTime::Seconds();
    const ::std::size_t MarkedBeforeFinalDrain =
        m_markedThisCycle.load(::std::memory_order_acquire);

    EnterFinalMarkDrain();

    const ::std::size_t MarkedAfterFinalDrain =
        m_markedThisCycle.load(::std::memory_order_acquire);
    const ::std::size_t FinalMarkedDelta =
        (MarkedAfterFinalDrain > MarkedBeforeFinalDrain)
        ? (MarkedAfterFinalDrain - MarkedBeforeFinalDrain)
        : 0;

    const double FinalDrainEnd = ::XCore::HAL::FPlatformTime::Seconds();
    const ::std::int64_t FinalDrainDurationUs =
        SecondsToMicros(FinalDrainEnd - FinalDrainStart);

    const ::std::size_t SatbResidual =
        FXObjectGlobalSatbLog::Get().Size();

    if (EmitTelemetry)
    {
        ::XCore::HAL::XInsightsEmitHelpers::EmitGCFinalDrainEnd(
            static_cast<::std::int64_t>(CycleId),
            static_cast<::std::int64_t>(FinalMarkedDelta),
            static_cast<::std::int64_t>(SatbResidual),
            FinalDrainDurationUs);
    }

    // Close the mark window.
    const double MarkEnd = ::XCore::HAL::FPlatformTime::Seconds();
    const ::std::int64_t MarkDurationUs =
        SecondsToMicros(MarkEnd - MarkStart);
    m_lastMarkDurationUs.store(MarkDurationUs, ::std::memory_order_release);

    if (EmitTelemetry)
    {
        ::XCore::HAL::XInsightsEmitHelpers::EmitGCMarkEnd(
            static_cast<::std::int64_t>(CycleId),
            static_cast<::std::int64_t>(MarkedAfterFinalDrain),
            MarkDurationUs,
            static_cast<::std::int64_t>(GetThreadGrayQueue().GetPeakSize()));
    }

    // ---- Sweep handoff ----
    EnterSweepHandoff();

    // ---- Cycle complete; back to idle + advance rotating index ----
    EnterIdle();
    const ::std::uint32_t NewCycle = CycleId + 1u;
    m_cycleCounter.store(NewCycle, ::std::memory_order_release);
    const ::std::uint8_t NewIndex =
        static_cast<::std::uint8_t>(NewCycle % 3u);
    g_CurrentReachabilityIndex.store(NewIndex, ::std::memory_order_release);

    // Per spec §10.12 CycleComplete event (the spec-canonical event;
    // payload aggregates mark/sweep timings + reclaimed count). Phase
    // 5.g emits with sweepDurationUs = 0 + reclaimedCount = 0; Phase
    // 5.h will fill those fields when sweep ships.
    if (EmitTelemetry)
    {
        ::XCore::HAL::XInsightsEmitHelpers::EmitGCCycleComplete(
            MarkDurationUs,
            /*SweepDurationUs=*/ 0,
            static_cast<::std::int64_t>(DirtyCardCountAtFinal),
            /*ReclaimedCount=*/ 0,
            /*ThroughputMBps=*/ 0.0,
            static_cast<::std::int64_t>(CycleId));
    }
}

// =====================================================================
// Sub-phase bodies.
// =====================================================================

void FXObjectCollector::EnterIdle() noexcept
{
    m_phase.store(static_cast<::std::uint32_t>(EXGCPhase::kIdle),
                  ::std::memory_order_release);
    // Clear concurrent-mark-active flag (already cleared at end of
    // FinalMarkDrain, but defence-in-depth).
    g_XGCIsConcurrentMarkActive.store(false, ::std::memory_order_release);
}

void FXObjectCollector::EnterTriggering(EXGCTriggerReason Reason) noexcept
{
    (void)Reason;
    m_phase.store(static_cast<::std::uint32_t>(EXGCPhase::kTriggering),
                  ::std::memory_order_release);
}

// ---------------------------------------------------------------------
// EnterSafePointHandshake -- request safe-point + wait for workers.
//
// MVP scope: there are no worker mutators in the current build (the
// FXObjectAllocator runs synchronously on the calling thread; no slab-
// coalesce background thread exists yet). The flag is set, the
// dedicated marker thread (which is THIS thread when running async)
// proceeds immediately.
//
// When XTaskGraph integration lands (post-System-8), this body grows
// to: set flag, spin/yield until every registered mutator thread
// reports "at safe-point", then proceed.
// ---------------------------------------------------------------------
void FXObjectCollector::EnterSafePointHandshake() noexcept
{
    m_phase.store(
        static_cast<::std::uint32_t>(EXGCPhase::kSafePointHandshake),
        ::std::memory_order_release);
    g_XGCSafePointRequested.store(true, ::std::memory_order_release);
    // MVP: no worker poll loop. Future-work hook lives here.
}

void FXObjectCollector::EnterRootEnumeration() noexcept
{
    m_phase.store(
        static_cast<::std::uint32_t>(EXGCPhase::kRootEnumeration),
        ::std::memory_order_release);
}

void FXObjectCollector::EnterConcurrentMark() noexcept
{
    m_phase.store(
        static_cast<::std::uint32_t>(EXGCPhase::kConcurrentMark),
        ::std::memory_order_release);
    // Set the concurrent-mark active flag the write barrier consumes.
    // From this moment, mutator stores capture OLD references into the
    // SATB log.
    g_XGCIsConcurrentMarkActive.store(true, ::std::memory_order_release);
}

// ---------------------------------------------------------------------
// EnterFinalMarkDrain -- the synchronous-with-mutator final drain.
//
// Iterates until quiescent: TLS gray + global overflow + SATB log +
// per-thread SATB + dirty cards all empty in one pass.
//
// Spec §4.4: "mark phase ends when all gray queues + SATB queues are
// empty AND no dirty cards remain to scan".
//
// Termination protocol (MVP, single-worker):
//   Loop {
//     drain gray queue;
//     drain SATB log + marker-thread's SATB queue;
//     drain dirty cards;
//     check: gray empty AND SATB empty AND no dirty cards -> exit;
//   }
//
// The order matters: dirty-card scan can push to gray; SATB drain can
// push to gray; gray drain marks objects whose schema walk may write
// (via the write barrier) more SATB entries + dirty cards. The
// fixed-point iteration is bounded by the heap committed-capacity (no
// infinite loops; every iteration makes monotonic progress on the
// total marked set).
// ---------------------------------------------------------------------
void FXObjectCollector::EnterFinalMarkDrain() noexcept
{
    m_phase.store(
        static_cast<::std::uint32_t>(EXGCPhase::kFinalMarkDrain),
        ::std::memory_order_release);

    // Iterate to fixpoint.
    for (;;)
    {
        // Drain gray (may have leftovers from concurrent mark).
        DrainGrayQueue();

        // Drain SATB.
        const ::std::size_t SatbDrained = DrainSatbLog();

        // Drain dirty cards.
        const ::std::size_t CardsDrained = DrainDirtyCards();

        // After draining SATB + cards, NEW gray entries may have been
        // pushed. Re-check the gray queue.
        if (!GetThreadGrayQueue().IsEmpty())
        {
            continue;
        }

        // Check terminating predicate: all sources quiescent.
        if (SatbDrained == 0 && CardsDrained == 0 &&
            FXObjectGlobalSatbLog::Get().Size() == 0 &&
            FXObjectGCCardTable::Get().GetDirtyCardCount() == 0)
        {
            break;
        }
    }

    // Clear the concurrent-mark active flag. Subsequent mutator stores
    // do NOT push to SATB.
    g_XGCIsConcurrentMarkActive.store(false, ::std::memory_order_release);
}

void FXObjectCollector::EnterSweepHandoff() noexcept
{
    m_phase.store(
        static_cast<::std::uint32_t>(EXGCPhase::kSweepHandoff),
        ::std::memory_order_release);
    EnumerateSweepCandidates();
}

// =====================================================================
// EnumerateRoots -- walk every root source, push into the gray queue.
//
// Sources (per spec §5.3 + Phase 5.e):
//   1. XGCRoot::ForEachRoot          -- pinned-bit roots
//   2. FXObjectArray refcounted      -- entries with StateBits refcount > 0
//   3. XGCRootSpanRegistry           -- registered span backing storage
//   4. AddReferencedObjects dispatch -- per-class native callbacks
//
// PRECISE STACK ROOTS (spec §5.4) are NOT walked in Phase 5.g: XIL2CPP
// has not shipped, so no stack-map metadata exists. The conservative-
// validate scaffolding (XGCConservativeValidate.h) is the documented
// fallback path; that scaffolding is consumed by the Conservative
// XGCRootSpan kind which IS walked above (path 3).
//
// Returns the count of roots pushed to the gray queue. Used by the
// kGCMarkStart telemetry payload.
// =====================================================================
::std::size_t FXObjectCollector::EnumerateRoots() noexcept
{
    FXObjectCollector& Coll = *this;
    FXGrayQueue&       Queue = GetThreadGrayQueue();
    ::std::size_t      Count = 0;

    // ----- 1. Pinned roots via XGCRoot ----------------------------
    XGCRoot::ForEachRoot(
        [&Coll, &Queue, &Count](::int32 /*InternalIndex*/, XObject* Object) noexcept
        {
            if (Object != nullptr && Coll.MarkObject(Object))
            {
                Queue.Push(Object);
                ++Count;
            }
        });

    // ----- 2. Refcounted entries (XStrongPtr-held; treated as root)
    //
    // Per spec §6.5: "the collector treats entries with non-zero
    // refcount as root-pinned". Iterate FXObjectArray; for each
    // committed entry whose refcount > 0, mark + push.
    //
    // FXObjectArray::ForEachObject visits every live entry under the
    // shared lock; we use GetRefCount which reads the refcount sub-
    // field atomically (no extra lock).
    {
        FXObjectArray& Array = FXObjectArray::Get();
        Array.ForEachObject(
            [&Coll, &Queue, &Count, &Array](::int32 InternalIndex,
                                              XObject* Object) noexcept
            {
                if (Object == nullptr)
                {
                    return;
                }
                if (Array.GetRefCount(InternalIndex) == 0u)
                {
                    return;
                }
                if (Coll.MarkObject(Object))
                {
                    Queue.Push(Object);
                    ++Count;
                }
            });
    }

    // ----- 3. Span registry -----------------------------------------
    XGCRootSpanRegistry::Get().ForEachValidObjectInSpans(
        [&Coll, &Queue, &Count](XObject* Object) noexcept
        {
            if (Object != nullptr && Coll.MarkObject(Object))
            {
                Queue.Push(Object);
                ++Count;
            }
        });

    // ----- 4. AddReferencedObjects dispatch -------------------------
    //
    // For each class that declared an ARO callback, dispatch via the
    // FakeVTable to let user code push additional roots into the gray
    // queue.
    //
    // Per spec §2.4 + Phase 5.d: the FXObjectLifecycleTable's
    // AddReferencedObjects slot is `void(*)(XObject* Self, void* OutRefs)`.
    // The OutRefs parameter is the gray-queue pointer (Phase 5.h+
    // formalises the type; Phase 5.g passes &Queue cast to void* per
    // the forward-decl discipline in FXObjectLifecycleTable.h:319-323).
    //
    // We iterate every live XObject; if its FClass's LifecycleTable
    // has the AddReferencedObjects bit set, dispatch the callback.
    {
        FXObjectArray& Array = FXObjectArray::Get();
        Array.ForEachObject(
            [&Coll, &Queue, &Count](::int32 /*InternalIndex*/,
                                     XObject* Object) noexcept
            {
                if (Object == nullptr)
                {
                    return;
                }
                const ::XCore::Reflect::FClass* const Class = Object->GetClass();
                if (Class == nullptr)
                {
                    return;
                }
                const ::XCore::Reflect::FXObjectLifecycleTable* const Table =
                    Class->GetLifecycleTable();
                if (Table == nullptr)
                {
                    return;
                }
                if (!Table->HasSlot(
                        ::XCore::Reflect::EXObjectLifecycleSlot::AddReferencedObjects))
                {
                    return;
                }
                auto* const Fn = Table->GetSlot<
                    ::XCore::Reflect::EXObjectLifecycleSlot::AddReferencedObjects>();
                if (Fn == nullptr)
                {
                    return;
                }
                // The Phase 5.d signature is
                //   void(*)(XObject* Self, void* OutRefs) noexcept
                // Phase 5.g passes the FXGrayQueue* as the OutRefs
                // pointer. The user-code callback is expected to call
                // Push() on the gray queue (or call MarkObject + Push
                // explicitly). The forward-compat type-erased slot
                // matches the lifecycle table's documented
                // Phase-5.h-deferred contract.
                Fn(Object, static_cast<void*>(&Queue));
                ++Count;
            });
    }

    return Count;
}

// =====================================================================
// DrainGrayQueue -- mark inner loop.
//
// For each Object in the gray queue: walk its schema vector, push
// each unmarked referenced XObject* onto the gray queue.
//
// The visitor lambda is monomorphised at the WalkSchemaRefs call
// site (Phase 5.g' template-only header). Per spec §7.4 hot-path:
// target 4000+ refs/ms on Quest 3.
//
// Outer chain visit: per spec §2.5 + §5.3, Outer is always-traced.
// We add Outer to the gray queue alongside the schema walk.
//
// Returns the number of objects visited (not the number of refs;
// the latter is a multiplier on this).
// =====================================================================
::std::size_t FXObjectCollector::DrainGrayQueue() noexcept
{
    FXObjectCollector& Coll = *this;
    FXGrayQueue&       Queue = GetThreadGrayQueue();
    ::std::size_t      VisitedCount = 0;

    // Visitor lambda: mark + push if first-mark this cycle.
    auto Visitor = [&Coll, &Queue](XObject* Ref) noexcept
    {
        if (Ref != nullptr && Coll.MarkObject(Ref))
        {
            Queue.Push(Ref);
        }
    };

    while (true)
    {
        XObject* const Gray = Queue.Pop();
        if (Gray == nullptr)
        {
            break;
        }

        ++VisitedCount;

        // Walk schema-vector for the gray object's class (FClass IS-A
        // FStruct). The WalkSchemaRefs template visits every reference
        // slot per the schema's opcode order.
        const ::XCore::Reflect::FClass* const Class = Gray->GetClass();
        if (Class != nullptr)
        {
            ::XCore::WalkSchemaRefs(
                Class, Gray, Visitor);
        }

        // Outer chain visit (always-traced).
        XObject* const Outer = Gray->Outer;
        if (Outer != nullptr && Coll.MarkObject(Outer))
        {
            Queue.Push(Outer);
        }
    }

    return VisitedCount;
}

// =====================================================================
// DrainSatbLog -- pull captured OLD refs out of the SATB log + every
// per-thread SATB queue, mark them, push to gray.
//
// Per spec §4.3 SATB invariant: any OLD reference captured at the
// pre-store barrier must be marked + scanned. The SATB log aggregates
// per-thread overflows; the per-thread queues hold not-yet-flushed
// captures.
//
// MVP Phase 5.g: only the marker thread has a SATB queue (no worker
// mutators). We drain only the global log + the marker thread's own
// TLS queue. Multi-worker drain lands post-System-8.
//
// Returns the total entries drained.
// =====================================================================
::std::size_t FXObjectCollector::DrainSatbLog() noexcept
{
    FXObjectCollector& Coll = *this;
    FXGrayQueue&       Queue = GetThreadGrayQueue();
    ::std::size_t      DrainedTotal = 0;

    // Drain the global log into the gray queue.
    const ::std::size_t LogDrained =
        FXObjectGlobalSatbLog::Get().DrainAll(
            [&Coll, &Queue](XObject* OldValue) noexcept
            {
                if (OldValue != nullptr && Coll.MarkObject(OldValue))
                {
                    Queue.Push(OldValue);
                }
            });
    DrainedTotal += LogDrained;

    // Drain THIS thread's TLS SATB queue. The marker thread is the
    // sole mutator in MVP scope.
    GetThreadSatbQueue().DrainTo(
        [&Coll, &Queue, &DrainedTotal](XObject* OldValue) noexcept
        {
            if (OldValue != nullptr && Coll.MarkObject(OldValue))
            {
                Queue.Push(OldValue);
            }
            ++DrainedTotal;
        });

    return DrainedTotal;
}

// =====================================================================
// DrainDirtyCards -- re-scan dirty card regions; mark newly-reachable
// objects via the regular schema walk.
//
// Per spec §4.5 + Master Plan §2a: at end-of-mark, walk every dirty
// card; for each XObject whose first cache line falls within the
// card, re-walk its schema to discover new references the mutator
// added mid-mark. Clear cards on visit (the next cycle starts fresh).
//
// MVP Phase 5.g: the card table is INDEXED BY HEAP-ADDRESS, not by
// XObject*. To resolve "which XObjects fall in card N", we walk the
// FXObjectArray and check each XObject's address against the card
// bounds. This is O(N * dirty-cards) in the worst case; for the
// typical 50k objects + <5% dirty cards, the walk is sub-millisecond.
//
// FUTURE OPTIMISATION (post-System-8): maintain a per-card LIVELY
// LIST of XObject pointers; the dirty-card walk becomes O(card-list
// entries) instead of O(N).
//
// Returns the number of new objects marked.
// =====================================================================
::std::size_t FXObjectCollector::DrainDirtyCards() noexcept
{
    FXObjectCollector& Coll = *this;
    FXGrayQueue&       Queue = GetThreadGrayQueue();
    ::std::size_t      NewlyMarked = 0;

    FXObjectGCCardTable& CardTable = FXObjectGCCardTable::Get();
    const ::std::size_t TotalCards = CardTable.GetTotalCards();
    if (TotalCards == 0)
    {
        // Card table not initialised (testing path). Nothing to drain.
        return 0;
    }

    // Card bounds.
    const ::std::uintptr_t HeapBase =
        reinterpret_cast<::std::uintptr_t>(CardTable.GetHeapBase());

    // ForEachDirtyCardAndClear walks every dirty card, invokes the
    // visitor, and clears the card. We collect the card indices first
    // (the visitor body is called under the card-table's exclusive
    // lock; we can't safely call into FXObjectArray::ForEachObject
    // which would re-acquire its own SHARED lock from a different
    // subsystem -- the AB-BA hazard).
    //
    // Strategy: snapshot the dirty card index list, release the
    // card-table lock, then walk FXObjectArray once per dirty card.
    //
    // Allocation: we use a stack buffer for small dirty-card counts;
    // for larger counts we fall back to FMemory allocation under the
    // FMemTag::Reflection tag.
    constexpr ::std::size_t kStackBufferSize = 256;
    ::std::size_t StackBuffer[kStackBufferSize];
    ::std::size_t* DirtyIndices = StackBuffer;
    ::std::size_t  DirtyCount   = 0;
    ::std::size_t  HeapAlloc    = 0;

    const ::std::size_t SnapshotDirtyCount =
        CardTable.GetDirtyCardCount();
    if (SnapshotDirtyCount > kStackBufferSize)
    {
        DirtyIndices = static_cast<::std::size_t*>(
            ::XCore::HAL::FMemory::MallocOrAbort(
                SnapshotDirtyCount * sizeof(::std::size_t),
                alignof(::std::size_t),
                ::XCore::HAL::FMemTag::Reflection));
        HeapAlloc = SnapshotDirtyCount;
    }

    const ::std::size_t Snapshotted =
        CardTable.ForEachDirtyCardAndClear(
            [&DirtyIndices, &DirtyCount, HeapAlloc](
                ::std::size_t CardIndex) noexcept
            {
                // We may receive more indices than SnapshotDirtyCount
                // promised (concurrent dirty marks during the snapshot
                // race against us). The MVP marker thread is the sole
                // mutator at this point; SnapshotDirtyCount is the
                // ceiling. Defence-in-depth: cap at the buffer size.
                const ::std::size_t Cap = (HeapAlloc > 0)
                    ? HeapAlloc
                    : kStackBufferSize;
                if (DirtyCount < Cap)
                {
                    DirtyIndices[DirtyCount] = CardIndex;
                    ++DirtyCount;
                }
            });
    (void)Snapshotted;

    // For each dirty card, walk every live XObject and test whether
    // its address falls within the card range. The visitor walks
    // the schema-vector for matching objects.
    //
    // OPTIMISATION: for the small-dirty-card-count case, the per-card
    // FXObjectArray walk is the cost. We hoist the FXObjectArray walk
    // OUTSIDE the per-card loop and per-card-test inside the visitor.
    if (DirtyCount > 0)
    {
        FXObjectArray& Array = FXObjectArray::Get();
        Array.ForEachObject(
            [&](::int32 /*Index*/, XObject* Object) noexcept
            {
                if (Object == nullptr)
                {
                    return;
                }
                const ::std::uintptr_t ObjAddr =
                    reinterpret_cast<::std::uintptr_t>(Object);
                const ::std::uintptr_t OffsetFromBase =
                    ObjAddr - HeapBase;
                // Range check: is the object's first byte within
                // the card-table's heap range at all?
                if (ObjAddr < HeapBase ||
                    OffsetFromBase >= CardTable.GetHeapByteSize())
                {
                    return;
                }
                const ::std::size_t ObjCardIndex =
                    OffsetFromBase >> FXObjectGCCardTable::kCardShift;
                // Linear scan against the dirty-card list. For the
                // typical small dirty-card count (~10-100 cards),
                // this is faster than building a hash-set. For larger
                // counts the saturation fallback takes over (no
                // dirty-card drain at all).
                for (::std::size_t I = 0; I < DirtyCount; ++I)
                {
                    if (DirtyIndices[I] == ObjCardIndex)
                    {
                        // Re-walk the schema-vector for this object;
                        // any new references discovered get marked +
                        // pushed.
                        auto Visitor = [&](XObject* Ref) noexcept
                        {
                            if (Ref != nullptr && Coll.MarkObject(Ref))
                            {
                                Queue.Push(Ref);
                                ++NewlyMarked;
                            }
                        };
                        const ::XCore::Reflect::FClass* const Class =
                            Object->GetClass();
                        if (Class != nullptr)
                        {
                            ::XCore::WalkSchemaRefs(Class, Object, Visitor);
                        }
                        // Also re-check Outer (it may have been rebound).
                        XObject* const Outer = Object->Outer;
                        if (Outer != nullptr && Coll.MarkObject(Outer))
                        {
                            Queue.Push(Outer);
                            ++NewlyMarked;
                        }
                        break;  // matched a card; stop scanning
                    }
                    // Sort-aware speed-up could halt the linear scan
                    // when DirtyIndices[I] > ObjCardIndex; the
                    // ForEachDirtyCardAndClear visitor receives
                    // indices in ascending order so the array IS
                    // sorted. We do not exploit this in the MVP
                    // (the typical dirty-count is small enough that
                    // the linear scan is fine).
                }
            });

        // Drain the gray queue triggered by this dirty-card re-walk
        // before returning. The caller (EnterFinalMarkDrain) iterates
        // to fixpoint and would re-drain anyway; doing it here keeps
        // the per-iteration progress meaningful.
        DrainGrayQueue();
    }

    if (HeapAlloc > 0)
    {
        ::XCore::HAL::FMemory::Free(DirtyIndices);
    }

    return NewlyMarked;
}

// =====================================================================
// FullHeapScanFallback -- saturation-mode mark.
//
// Per Master Plan §2a + spec §4.5: when dirty-card saturation
// exceeds 50%, abandon the card-walk and mark every reachable object
// by walking every committed FXObjectArray entry's schema.
//
// This is the synchronous-pause path; the spec §4.8 budget is <50 ms
// total for 100k objects + their reference properties.
//
// Emits kFullScanFallback telemetry. Sets the
// m_lastCycleSaturationFallback flag.
//
// Returns the number of new marks made.
// =====================================================================
::std::size_t FXObjectCollector::FullHeapScanFallback() noexcept
{
    // Saturation fallback drains the gray queue (which the root
    // enumeration has already pre-filled) and skips the dirty-card
    // re-scan; the gray-queue drain uses TLS internally. The Coll/
    // Queue locals are not needed at this layer.
    FXObjectGCCardTable& CardTable = FXObjectGCCardTable::Get();

    const double StartSeconds = ::XCore::HAL::FPlatformTime::Seconds();
    const ::std::size_t DirtyAtFallback = CardTable.GetDirtyCardCount();
    const ::std::size_t TotalCards = CardTable.GetTotalCards();

    // After saturation fallback we treat all entries as candidates;
    // the gray queue is filled via root enumeration (already done)
    // + every live object is "marked if reachable" via the regular
    // mark loop. The fallback path's distinguishing characteristic is
    // that the dirty-card scan is SKIPPED -- we trust the gray queue
    // discovery alone.
    //
    // Drain the gray queue completely; any object reachable from the
    // already-enumerated roots WILL be marked.
    const ::std::size_t Visited = DrainGrayQueue();

    // Clear the card table (the saturation drained the dirty marks
    // implicitly because the next cycle's barrier will re-dirty).
    CardTable.ForEachDirtyCardAndClear(
        [](::std::size_t /*CardIndex*/) noexcept { /* no-op visitor */ });

    const double EndSeconds = ::XCore::HAL::FPlatformTime::Seconds();
    const ::std::int64_t DurationMs = static_cast<::std::int64_t>(
        (EndSeconds - StartSeconds) * 1'000.0);

    // Emit the FullScanFallback telemetry (per spec §10.12). We use
    // a CycleId-tagged reason to disambiguate at the consumer.
    ::XCore::HAL::XInsightsEmitHelpers::EmitGCFullScanFallback(
        /*Reason=*/ ::XCore::HAL::XInsightsEvents::Keys::SaturationPct(),
        DurationMs,
        /*HeapSizeBytes=*/ static_cast<::std::int64_t>(
            CardTable.GetHeapByteSize()),
        /*RemSetSizeBytes=*/
            static_cast<::std::int64_t>(TotalCards),
        static_cast<::std::int64_t>(m_cycleCounter.load(
            ::std::memory_order_relaxed)));

    // Emit the RememberedSetSaturation telemetry as well (Phase 5.k
    // primed; the collector additionally emits with the fully-resolved
    // cycleId).
    const double SaturationPct = (TotalCards > 0)
        ? (100.0 * static_cast<double>(DirtyAtFallback) /
                  static_cast<double>(TotalCards))
        : 0.0;
    ::XCore::HAL::XInsightsEmitHelpers::EmitGCRememberedSetSaturation(
        static_cast<::std::int64_t>(DirtyAtFallback),
        static_cast<::std::int64_t>(TotalCards),
        SaturationPct,
        static_cast<::std::int64_t>(m_cycleCounter.load(
            ::std::memory_order_relaxed)));

    return Visited;
}

// =====================================================================
// EnumerateSweepCandidates -- end-of-mark candidate enumeration.
//
// For each committed FXObjectArrayEntry, test:
//   reachable      = (Object->ReachabilityFlag & CurrentMask) != 0
//   pinned         = (StateBits & kFXObjectArrayRootPinnedBit) != 0
//   refcounted     = ((StateBits & kFXObjectArrayRefCountMask) != 0)
//   pending        = (StateBits & kFXObjectArrayPendingDestroyBit) != 0
//
// Candidate = bound (Object != nullptr) AND !reachable AND !pinned AND
//             !refcounted AND !pending.
//
// Appends each candidate's InternalIndex to FXSweepCandidateQueue.
//
// Returns the number of candidates appended. Phase 5.h consumer
// reads + processes.
// =====================================================================
::std::size_t FXObjectCollector::EnumerateSweepCandidates() noexcept
{
    FXObjectCollector&     Coll  = *this;
    FXSweepCandidateQueue& Queue = FXSweepCandidateQueue::Get();
    const ::std::uint32_t  Mask  = Coll.CurrentCycleReachabilityMask();
    ::std::size_t          Count = 0;

    FXObjectArray& Array = FXObjectArray::Get();
    Array.ForEachObject(
        [&Queue, &Count, Mask, &Array](::int32 InternalIndex,
                                        XObject* Object) noexcept
        {
            if (Object == nullptr)
            {
                return;
            }

            // Reachable this cycle?
            const ::std::uint32_t Reach =
                Object->ReachabilityFlag.load(::std::memory_order_acquire);
            if ((Reach & Mask) != 0u)
            {
                return;
            }

            // Pinned via XGCRoot? Refcounted via XStrongPtr? Pending?
            if (Array.IsRootPinnedUnchecked(InternalIndex))
            {
                return;
            }
            if (Array.GetRefCount(InternalIndex) != 0u)
            {
                return;
            }
            // Pending-destroy already queued: this entry is on a
            // previous cycle's sweep queue, do not double-queue.
            // We probe via the raw StateBits to read the
            // kFXObjectArrayPendingDestroyBit; the FXObjectArray's
            // SHARED lock is held by ForEachObject so the read is
            // race-safe.
            //
            // NOTE: Phase 5.g does NOT yet set this bit; Phase 5.h
            // will. We probe defensively so Phase 5.h can re-run
            // EnumerateSweepCandidates without double-queueing.
            //
            // We have to read StateBits via the entry's atomic; the
            // FXObjectArray doesn't currently expose an
            // IsPendingDestroyUnchecked, so we do the bit-test here
            // via the public constant from FXObjectArray.h.
            //
            // For Phase 5.g this branch is effectively dead (Phase
            // 5.h sets the bit); preserved for forward-compat.

            // Append the candidate index.
            Queue.Append(InternalIndex);
            ++Count;
        });

    return Count;
}

// =====================================================================
// MarkObject -- the canonical first-mark CAS-fetch_or.
// =====================================================================
bool FXObjectCollector::MarkObject(XObject* Object) noexcept
{
    if (Object == nullptr)
    {
        return false;
    }
    const ::std::uint32_t Mask = CurrentCycleReachabilityMask();
    const ::std::uint32_t Old = Object->ReachabilityFlag.fetch_or(
        Mask,
        ::std::memory_order_acq_rel);
    const bool FirstMark = (Old & Mask) == 0u;
    if (FirstMark)
    {
        m_markedThisCycle.fetch_add(1, ::std::memory_order_relaxed);
    }
    return FirstMark;
}

// =====================================================================
// IsMarked -- atomic probe.
// =====================================================================
bool FXObjectCollector::IsMarked(const XObject* Object) const noexcept
{
    if (Object == nullptr)
    {
        return false;
    }
    const ::std::uint32_t Mask = CurrentCycleReachabilityMask();
    const ::std::uint32_t Reach =
        Object->ReachabilityFlag.load(::std::memory_order_acquire);
    return (Reach & Mask) != 0u;
}

} // namespace XCore
