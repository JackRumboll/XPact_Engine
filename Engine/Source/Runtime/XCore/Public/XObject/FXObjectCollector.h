// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FXObjectCollector.h -- the mark-phase collector (XCoreXObject Rev 4
// §4 + §10.12; Phase 5.g).
// =====================================================================
//
// XCoreXObject Rev 4 §4 ("GC Collector Algorithm") + §4.2 (phase
// structure) + §4.3 (mostly-concurrent SATB) + §4.4 (worker-pool
// concurrency) + §4.5 (card-table generational scan-reduction) +
// §4.7 (trigger heuristic) + §4.8 (acceptance: pause budget breakdown)
// + §10.12 (XInsights events).
//
// PURPOSE: the process-singleton GC collector. Implements the MARK
// PHASE of the non-moving precise mostly-concurrent mark-and-sweep
// collector. Sweep is Phase 5.h; Phase 5.g produces the sweep-
// candidate queue and stops there.
//
// =====================================================================
//
// API SHAPE (per the prompt's enumerated requirements + spec §4.0
// EXGCOptions + spec §4.7 trigger heuristic):
//
//   * Singleton: FXObjectCollector::Get()
//   * State machine: EXGCPhase + transitions
//     EnterIdle / EnterTriggering / EnterSafePointHandshake /
//     EnterRootEnumeration / EnterConcurrentMark / EnterFinalMarkDrain /
//     EnterSweepHandoff
//   * Probes:
//     bool IsMarking() -- "the mark phase is active" (any sub-state from
//                          ConcurrentMark through FinalMarkDrain)
//     bool IsMarkingActive() -- the alias the write-barrier consumes
//                                (matches the XGCWriteBarrier.h stub
//                                that Phase 5.g replaces)
//   * Triggers:
//     void CollectGarbage(EXGCOptions = kDefault)
//     void Trigger(EXGCTriggerReason)
//   * Rotating-flag state:
//     uint32_t GetCycleCounter()
//     uint8_t  GetCurrentReachabilityIndex()
//     uint32_t CurrentCycleReachabilityMask()
//   * Mark primitive:
//     bool MarkObject(XObject*)     -- atomic fetch_or; returns true on
//                                       first-mark this cycle
//     bool IsMarked(const XObject*) -- atomic load
//   * Shutdown:
//     void Shutdown()
//
// =====================================================================
//
// PHASE STRUCTURE (per spec §4.2):
//
//   kIdle
//     -> Trigger reason fires
//   kTriggering
//     -> Trigger thread = main thread; the trigger thread IS the
//        synchronous safe-point on the MVP
//   kSafePointHandshake
//     -> Sets kSafePointRequested; for the MVP this is a no-op past
//        the main thread itself (no worker mutators)
//   kRootEnumeration
//     -> Walks XGCRoot::ForEachRoot (pinned roots) + FXObjectArray
//        sparse iteration for refcounted entries + XGCRootSpan-
//        Registry::ForEachValidObjectInSpans + AddReferencedObjects
//        dispatch (Outer chain visit is folded into the schema walk)
//   kConcurrentMark
//     -> Sets g_XGCIsConcurrentMarkActive = true; drains gray queue
//        via WalkSchemaRefs visitor
//   kFinalMarkDrain
//     -> Drains SATB log + per-thread SATB queues + dirty-card
//        re-scan iterations until quiescent
//   kSweepHandoff
//     -> Walks every committed array entry; rotating-flag check
//        produces InternalIndex candidates into FXSweepCandidateQueue
//   kIdle (with cycle counter incremented + reachability index
//          advanced)
//
// =====================================================================
//
// SAFE-POINT HANDSHAKE (MVP scope per prompt; documented divergence
// from full spec §5.5):
//
//   The full spec contemplates a multi-thread handshake where every
//   gameplay thread reaches a safe-point. PRE-XTASKGRAPH (post-
//   System-8) there is exactly one mutator (the gameplay tick on
//   the main thread). The MVP safe-point scope is:
//
//     * CollectGarbage() is callable ONLY from the main thread (the
//       gameplay tick boundary). The main thread IS at safe-point
//       by virtue of having called the API.
//
//     * Worker threads (the FXObjectAllocator's slab-coalesce worker;
//       any future allocator background threads) honour
//       g_XGCSafePointRequested at any back-edge by spinning. There
//       are very few such workers; the scope is bounded.
//
//     * The DEDICATED MARKER THREAD (this collector's own thread)
//       is the consumer of the safe-point; it does NOT participate
//       as a mutator.
//
//   The full XTaskGraph-integrated handshake lands post-System-8.
//
// =====================================================================
//
// TERMINATION DETECTION (per prompt's corner case):
//
//   The spec §4.4 says "mark phase ends when all gray queues + SATB
//   queues are empty AND no dirty cards remain to scan". The MVP
//   marker thread runs as the SOLE worker, so the termination check
//   is trivially:
//
//     while (TLS-gray + global-overflow non-empty OR SATB-log non-
//            empty OR per-thread-SATB non-empty OR dirty-cards
//            present):
//         drain everything; iterate
//     when all-empty: terminate
//
//   The Dijkstra-Scholten style "active worker count" termination
//   detection that the prompt mentions is for the multi-worker
//   case (post-System-8 XTaskGraph integration). Single-worker
//   MVP termination is straightforward iteration-to-fixpoint.
//
// =====================================================================
//
// CYCLE COUNTER WIDTH (per prompt's corner case):
//
//   uint32_t supports 4 billion cycles. At 1 cycle/sec sustained
//   (an aggressive cadence; production typically <1 cycle/sec) the
//   wraparound is at ~136 years. Wraparound is benign: the cycle
//   counter is used modulo 3 to select the reachability bit, so the
//   ONLY observable effect of wrap is one cycle where the rotation
//   re-uses bit N twice. The spec §4.2.1 explicitly contemplates
//   this -- the rotating-flag scheme is correctness-preserving
//   across counter wrap. uint32_t is the principled width.
//
// =====================================================================
//
// MARKOBJECT RACE CORRECTNESS (per prompt's corner case):
//
//   MarkObject uses fetch_or on the atomic ReachabilityFlag. fetch_or
//   atomically reads the old value AND writes (old | mask); the
//   first-mark test is `(old & mask) == 0`. This is the canonical
//   correct pattern.
//
//   Two threads concurrently marking the same object: fetch_or
//   atomicity guarantees the two RMWs linearise. One observes
//   Old-WITHOUT-bit (returns true; first-mark; pushes to gray
//   queue); the other observes Old-WITH-bit (returns false; not
//   first-mark; skips push). Double-push is structurally impossible.
//
//   The bit ends set regardless of race direction; the queue sees
//   exactly one push.
//
// =====================================================================
//
// HOT-RELOAD: NO virtual methods. Singleton is a function-local
// static; cycle counter + reachability index are atomic. The
// marker thread joins at Shutdown().
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "HAL/FCriticalSection.h"
#include "HAL/FEvent.h"

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <thread>

namespace XCore { class XObject; }

namespace XCore
{
    // -----------------------------------------------------------------
    // EXGCOptions -- runtime-tunable per-cycle options (per spec §4.0
    // Rev 3 FIX-N-R2-6).
    //
    // The kDefault constant matches spec wording: "the runtime default
    // for an unannotated CollectGarbage() call is kEliminateGarbageRefs |
    // kEmitInsightsTelemetry".
    // -----------------------------------------------------------------
    enum class EXGCOptions : ::std::uint32_t
    {
        kNone                    = 0,
        kEliminateGarbageRefs    = 1u << 0,   // sweep also nulls refs to RF_MarkedAsGarbage objects
        kPreserveCDO             = 1u << 1,   // never collect class default objects
        kForceFullScan           = 1u << 2,   // bypass card-table optimisation for this cycle
        kSkipDeferredDestruction = 1u << 3,   // synchronous BeginDestroy/FinishDestroy (shutdown)
        kIncrementalMark         = 1u << 4,   // explicit incremental mark request
        kEmitInsightsTelemetry   = 1u << 5,   // emit XInsights GC events
        kEmitVerboseLog          = 1u << 6,   // emit XLog at Verbose verbosity
        // bits 7..31 reserved.

        kDefault = kEliminateGarbageRefs | kEmitInsightsTelemetry,
    };

    [[nodiscard]] XPACT_FORCEINLINE constexpr EXGCOptions
        operator|(EXGCOptions a, EXGCOptions b) noexcept
    {
        return static_cast<EXGCOptions>(
            static_cast<::std::uint32_t>(a) | static_cast<::std::uint32_t>(b));
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr EXGCOptions
        operator&(EXGCOptions a, EXGCOptions b) noexcept
    {
        return static_cast<EXGCOptions>(
            static_cast<::std::uint32_t>(a) & static_cast<::std::uint32_t>(b));
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr bool
        HasGCFlag(EXGCOptions Opts, EXGCOptions Flag) noexcept
    {
        return (static_cast<::std::uint32_t>(Opts)
                & static_cast<::std::uint32_t>(Flag)) != 0u;
    }

    // -----------------------------------------------------------------
    // EXGCTriggerReason -- why the collector is running (per spec §4.7
    // trigger heuristic + the Phase 5.g API surface).
    // -----------------------------------------------------------------
    enum class EXGCTriggerReason : ::std::uint32_t
    {
        kManual              = 0,   // explicit CollectGarbage() call
        kHeapOccupancy       = 1,   // >70% threshold
        kAllocationRate      = 2,   // >5000 obj/s
        kIdleWindow          = 3,   // engine idle > 5s
        kPreScenarioUnload   = 4,   // XScenarios pre-unload trigger
        kShutdown            = 5,   // engine teardown
    };

    // -----------------------------------------------------------------
    // EXGCPhase -- the collector's state-machine states.
    // -----------------------------------------------------------------
    enum class EXGCPhase : ::std::uint32_t
    {
        kIdle                = 0,   // no GC running; mutator runs freely
        kTriggering          = 1,   // trigger fired; preparing safe-point
        kSafePointHandshake  = 2,   // synchronous-with-mutator window
        kRootEnumeration     = 3,   // walking root sources
        kConcurrentMark      = 4,   // mark loop draining gray queue
        kFinalMarkDrain      = 5,   // SATB + dirty-card final drain
        kSweepHandoff        = 6,   // producing sweep-candidate queue
        kSweep               = 7,   // draining FXSweepCandidateQueue; dispatching BeginDestroy
        kDeferredDestruction = 8,   // (intra-cycle) drain pass on FXDeferredDestructionQueue
    };

    // -----------------------------------------------------------------
    // Global safe-point requested flag (per spec §5.5 + Phase 5.g MVP).
    //
    // Set by FXObjectCollector::EnterSafePointHandshake; cleared on
    // exit. Worker threads (e.g., the allocator's slab-coalesce
    // thread) MUST poll this at any back-edge and spin briefly while
    // it is set.
    //
    // Lives at namespace scope (not a member) so worker threads in
    // sibling subsystems can probe without taking a dependency on the
    // collector's singleton accessor.
    // -----------------------------------------------------------------
    extern ::std::atomic<bool> g_XGCSafePointRequested;

    // -----------------------------------------------------------------
    // Global rotating-reachability index (per spec §4.2.1).
    //
    // The "current cycle's" mark bit is selected by
    //   CurrentMask = 1u << g_CurrentReachabilityIndex
    // where g_CurrentReachabilityIndex is in {0, 1, 2}.
    //
    // Advances mod 3 at end of each cycle. Lives at namespace scope
    // so the XGCWriteBarrier's SATB pre-store check can read it
    // without going through the collector's singleton accessor on
    // the hot path. (Phase 5.g write-barrier wiring uses
    // g_XGCIsConcurrentMarkActive for the gate; the index is read by
    // any mark visitor that needs to compute the mask.)
    //
    // PRIME-DIRECTIVE NOTE -- spec wording says "global"; we provide
    // BOTH the namespace-scope extern AND a static-member accessor on
    // FXObjectCollector. The accessor is the principled C++ surface;
    // the extern is the hot-path-friendly direct-load surface for
    // contexts that cannot afford a function call.
    // -----------------------------------------------------------------
    extern ::std::atomic<::std::uint8_t> g_CurrentReachabilityIndex;

    // -----------------------------------------------------------------
    // FXObjectCollector -- the process-singleton GC collector.
    //
    // Members are split:
    //   * Public API surface: state-machine transitions, probes,
    //     triggers, mark primitive, shutdown.
    //   * Private: cycle counter, marker thread, sub-phase bodies.
    //
    // No virtual methods. The class is non-copyable + non-movable.
    // -----------------------------------------------------------------
    class FXObjectCollector
    {
    public:
        // =============================================================
        // Singleton accessor.
        //
        // Magic-static. The ctor does NOT spawn the marker thread; the
        // thread is spawned lazily on first Trigger() / CollectGarbage()
        // OR explicitly via __Init() (called by the engine bootstrap
        // at PostStaticInit). The lazy-init is the principled MVP
        // posture; explicit __Init lets the engine deterministically
        // place the thread spawn at a known initialisation point.
        // =============================================================
        [[nodiscard]] static FXObjectCollector& Get() noexcept;

        // =============================================================
        // __Init -- explicit initialisation (engine bootstrap path).
        //
        // Spawns the dedicated marker thread (per Rev 2 FIX-A-MED-36;
        // MVP pre-XTaskGraph). Idempotent: a second call is a no-op.
        //
        // Called from XEngineInit's PostStaticInit phase. Failures
        // (thread spawn fails) are diagnostic in Dev/Debug; in
        // Shipping the missing marker thread degrades the GC to
        // synchronous-on-trigger (CollectGarbage runs entirely on the
        // calling thread).
        // =============================================================
        void __Init() noexcept;

        // =============================================================
        // Shutdown -- engine teardown.
        //
        // Signals the marker thread to exit + joins. Drains residual
        // gray queue + SATB log + dirty cards via a final synchronous
        // CollectGarbage(kSkipDeferredDestruction) on the calling
        // thread. Idempotent.
        //
        // Called from XCore module teardown; MUST run AFTER any
        // FXObjectArray::ForEachObject visitor that touches GC state.
        // =============================================================
        void Shutdown() noexcept;

        // =============================================================
        // Test-only reset. Drops state; resets cycle counter and
        // reachability index; clears all queues.
        // =============================================================
        void __ResetForTests() noexcept;

        // =============================================================
        // CollectGarbage -- synchronous trigger (per spec §4.7
        // "explicit request" path).
        //
        // Runs the FULL collection cycle on the calling thread:
        //   1. EnterSafePointHandshake
        //   2. EnterRootEnumeration
        //   3. EnterConcurrentMark
        //   4. EnterFinalMarkDrain
        //   5. EnterSweepHandoff
        //   6. EnterIdle (cycle counter ++; reachability index ++ mod 3)
        //
        // The synchronous variant exists for: (a) explicit
        // GC.Collect()-style triggers; (b) shutdown; (c) testing.
        // The marker thread's async path (Trigger) is the production
        // happy-path.
        // =============================================================
        void CollectGarbage(EXGCOptions Opts = EXGCOptions::kDefault) noexcept;

        // =============================================================
        // Trigger -- heuristic-driven trigger (per spec §4.7).
        //
        // Signals the marker thread to run a cycle. Returns
        // immediately; the cycle runs on the marker thread.
        //
        // Multiple Triggers while one cycle is already running are
        // coalesced (only one extra cycle is queued; further triggers
        // before that cycle completes are silently ignored).
        //
        // If the marker thread is not spawned (Shutdown was called or
        // __Init failed), Trigger falls back to a synchronous
        // CollectGarbage call.
        // =============================================================
        void Trigger(EXGCTriggerReason Reason) noexcept;

        // =============================================================
        // IsMarking / IsMarkingActive -- probes consumed by the write
        // barrier (XGCWriteBarrier.h) + diagnostic consumers.
        //
        // IsMarking returns true when EXGCPhase is in {ConcurrentMark,
        // FinalMarkDrain}. The write barrier's SATB pre-store check
        // SHOULD fire across this entire window.
        //
        // IsMarkingActive is the alias name the prompt + the existing
        // XGCWriteBarrier.h header references. Same predicate.
        //
        // The probe is a single relaxed atomic load on a uint32 phase
        // word. ~1 cycle on Win64 / Linux / Android ARM64.
        //
        // PHASE 5.g WIRES the write barrier: the barrier currently
        // reads g_XGCIsConcurrentMarkActive (atomic<bool>); the
        // collector keeps that flag in sync with the phase state
        // machine. Specifically:
        //   * EnterConcurrentMark sets g_XGCIsConcurrentMarkActive = true
        //   * EnterIdle (post-drain) sets it = false
        // The phase load + the bool load both return the same answer
        // across the mark window; the bool is preserved for the
        // write barrier's hot-path simplicity.
        // =============================================================
        [[nodiscard]] XPACT_FORCEINLINE bool IsMarking() const noexcept
        {
            const ::std::uint32_t Phase = m_phase.load(::std::memory_order_acquire);
            return Phase == static_cast<::std::uint32_t>(EXGCPhase::kConcurrentMark) ||
                   Phase == static_cast<::std::uint32_t>(EXGCPhase::kFinalMarkDrain);
        }

        [[nodiscard]] XPACT_FORCEINLINE bool IsMarkingActive() const noexcept
        {
            return IsMarking();
        }

        // =============================================================
        // GetPhase -- diagnostic accessor for the current state.
        // =============================================================
        [[nodiscard]] XPACT_FORCEINLINE EXGCPhase GetPhase() const noexcept
        {
            return static_cast<EXGCPhase>(
                m_phase.load(::std::memory_order_acquire));
        }

        // =============================================================
        // GetCycleCounter -- monotonically-increasing cycle counter.
        //
        // Advances on every successful collection cycle. The
        // reachability index = (counter % 3); see spec §4.2.1.
        // =============================================================
        [[nodiscard]] XPACT_FORCEINLINE ::std::uint32_t GetCycleCounter() const noexcept
        {
            return m_cycleCounter.load(::std::memory_order_acquire);
        }

        // =============================================================
        // GetCurrentReachabilityIndex / CurrentCycleReachabilityMask.
        //
        // The cycle counter modulo 3 selects which of XObject.
        // ReachabilityFlag bits 0..2 is the "this-cycle's" mark bit.
        // =============================================================
        [[nodiscard]] XPACT_FORCEINLINE ::std::uint8_t
            GetCurrentReachabilityIndex() const noexcept
        {
            return g_CurrentReachabilityIndex.load(::std::memory_order_acquire);
        }

        [[nodiscard]] XPACT_FORCEINLINE ::std::uint32_t
            CurrentCycleReachabilityMask() const noexcept
        {
            return 1u << GetCurrentReachabilityIndex();
        }

        // =============================================================
        // MarkObject -- atomic first-mark probe.
        //
        // Atomically sets the current-cycle's reachability bit on
        // Object->ReachabilityFlag. Returns true iff the bit
        // transitioned from CLEAR to SET on this call (first-mark this
        // cycle); returns false on already-marked OR nullptr.
        //
        // Pattern (canonical CAS-via-fetch_or):
        //   const uint32_t Mask = CurrentCycleReachabilityMask();
        //   const uint32_t Old = obj->ReachabilityFlag.fetch_or(Mask,
        //       std::memory_order_acq_rel);
        //   return (Old & Mask) == 0;
        //
        // The acq_rel ordering ensures:
        //   * Acquire side: observers reading with acquire after the
        //     CAS see a happens-before-correct view of any state
        //     updated alongside the bit (e.g., the FXObjectAllocator's
        //     cell-bound writes preceding this object's first-visit).
        //   * Release side: subsequent visitors observing this bit
        //     have a happens-before view of any state set BEFORE the
        //     CAS by the marking thread.
        //
        // CALLER DISCIPLINE: the caller (the mark loop's visitor)
        // pushes to the gray queue iff this returns true.
        // =============================================================
        [[nodiscard]] bool MarkObject(XObject* Object) noexcept;

        // =============================================================
        // IsMarked -- atomic probe of the current-cycle reachability
        // bit.
        //
        // const-safe; no mutation. Returns false on nullptr.
        // =============================================================
        [[nodiscard]] bool IsMarked(const XObject* Object) const noexcept;

        // =============================================================
        // Diagnostic / test accessors.
        // =============================================================

        // Marked-this-cycle counter. Bumped by MarkObject on every
        // first-mark; reset at cycle start.
        [[nodiscard]] ::std::size_t GetMarkedThisCycle() const noexcept
        {
            return m_markedThisCycle.load(::std::memory_order_acquire);
        }

        // Last cycle's mark duration in microseconds.
        [[nodiscard]] ::std::int64_t GetLastMarkDurationUs() const noexcept
        {
            return m_lastMarkDurationUs.load(::std::memory_order_acquire);
        }

        // Last cycle's safe-point duration in microseconds.
        [[nodiscard]] ::std::int64_t GetLastSafePointDurationUs() const noexcept
        {
            return m_lastSafePointDurationUs.load(::std::memory_order_acquire);
        }

        // Was the LAST cycle a saturated full-heap-scan fallback?
        [[nodiscard]] bool LastCycleWasSaturationFallback() const noexcept
        {
            return m_lastCycleSaturationFallback.load(::std::memory_order_acquire);
        }

        // -------------------------------------------------------------
        // Sweep-phase diagnostics (Phase 5.h).
        //
        // GetLastSweepDurationUs           -- microseconds spent in the
        //                                       sweep body (sweep candidate
        //                                       queue drain + EliminateGarbage
        //                                       Refs pass) on the last cycle.
        // GetLastReclaimedCount            -- number of slots that
        //                                       transitioned through
        //                                       BeginDestroy on the last
        //                                       cycle. The actual slot
        //                                       release count is observable
        //                                       via FXDeferredDestructionQueue
        //                                       across sim-tick drain passes.
        // GetLastGarbageRefsClearedCount   -- number of XObject reference
        //                                       slots nulled by the
        //                                       kEliminateGarbageRefs pass on
        //                                       the last cycle.
        // -------------------------------------------------------------
        [[nodiscard]] ::std::int64_t GetLastSweepDurationUs() const noexcept
        {
            return m_lastSweepDurationUs.load(::std::memory_order_acquire);
        }

        [[nodiscard]] ::std::size_t GetLastReclaimedCount() const noexcept
        {
            return m_lastReclaimedCount.load(::std::memory_order_acquire);
        }

        [[nodiscard]] ::std::size_t GetLastGarbageRefsClearedCount() const noexcept
        {
            return m_lastGarbageRefsClearedCount.load(::std::memory_order_acquire);
        }

    private:
        FXObjectCollector() noexcept;
        ~FXObjectCollector() noexcept;

        FXObjectCollector(const FXObjectCollector&)            = delete;
        FXObjectCollector(FXObjectCollector&&)                 = delete;
        FXObjectCollector& operator=(const FXObjectCollector&) = delete;
        FXObjectCollector& operator=(FXObjectCollector&&)      = delete;

        // -------------------------------------------------------------
        // Sub-phase bodies. Each runs in its own time-bounded window
        // per spec §4.8.
        // -------------------------------------------------------------
        void EnterIdle() noexcept;
        void EnterTriggering(EXGCTriggerReason Reason) noexcept;
        void EnterSafePointHandshake() noexcept;
        void EnterRootEnumeration() noexcept;
        void EnterConcurrentMark() noexcept;
        void EnterFinalMarkDrain() noexcept;
        void EnterSweepHandoff() noexcept;
        void EnterSweep(EXGCOptions Opts) noexcept;

        // -------------------------------------------------------------
        // RunCycle -- executes the full state-machine sequence. Called
        // by:
        //   * CollectGarbage (synchronous): on the caller's thread.
        //   * MarkerThreadMain (asynchronous): on the dedicated marker
        //     thread.
        // -------------------------------------------------------------
        void RunCycle(EXGCOptions Opts, EXGCTriggerReason Reason) noexcept;

        // -------------------------------------------------------------
        // MarkerThreadMain -- the dedicated marker thread's entry
        // point. Sleeps on m_triggerEvent; wakes on Trigger; runs a
        // cycle; loops until m_shutdownRequested.
        // -------------------------------------------------------------
        void MarkerThreadMain() noexcept;

        // -------------------------------------------------------------
        // Sub-helpers used by mark sub-phases.
        // -------------------------------------------------------------

        // Enumerate roots into the gray queue. Walks XGCRoot, the
        // FXObjectArray sparse-iter for refcounted entries, and
        // XGCRootSpanRegistry. AddReferencedObjects dispatch fires
        // per-class via the lifecycle table.
        ::std::size_t EnumerateRoots() noexcept;

        // Drain the gray queue until empty. The "drain everything"
        // body: TLS pop, refill from global overflow, walk schema,
        // visit Outer, repeat.
        ::std::size_t DrainGrayQueue() noexcept;

        // Drain the SATB log + every per-thread SATB queue. Each
        // captured OLD reference is marked + pushed to gray queue.
        ::std::size_t DrainSatbLog() noexcept;

        // Drain the dirty-card list. For each dirty card, scan the
        // heap region for live objects and re-walk their schema.
        // Returns the number of new objects marked.
        ::std::size_t DrainDirtyCards() noexcept;

        // Saturation-fallback full-heap scan. Walks every committed
        // FXObjectArray entry, marks reachables, ignores card state.
        // Emits kFullScanFallback telemetry.
        ::std::size_t FullHeapScanFallback() noexcept;

        // End-of-mark candidate enumeration. Walks every committed
        // entry; appends unreachable + non-pinned + non-refcounted +
        // bound entries to FXSweepCandidateQueue.
        ::std::size_t EnumerateSweepCandidates() noexcept;

        // -------------------------------------------------------------
        // Phase 5.h sweep-phase sub-helpers.
        // -------------------------------------------------------------

        // Drain FXSweepCandidateQueue; for each candidate dispatch the
        // BeginDestroy lifecycle slot + set EObjectFlags::BeginDestroyed
        // + set kPendingDestroyBit + enqueue on FXDeferredDestructionQueue.
        // Returns the count of BeginDestroy dispatches that fired.
        ::std::size_t DrainSweepCandidates() noexcept;

        // Walk every reachable XObject; for each schema-vector reference
        // slot whose target has EObjectFlags::MarkedAsGarbage (or
        // kGarbageBit on the array entry), null the slot in-place.
        // Returns the count of slots nulled.
        ::std::size_t EliminateGarbageRefsPass() noexcept;

        // -------------------------------------------------------------
        // State.
        // -------------------------------------------------------------

        // Current phase (EXGCPhase cast to uint32 for atomic ops).
        ::std::atomic<::std::uint32_t> m_phase;

        // Cycle counter; advances monotonically.
        ::std::atomic<::std::uint32_t> m_cycleCounter;

        // Marked-this-cycle running count.
        ::std::atomic<::std::size_t>   m_markedThisCycle;

        // Last-cycle mark duration (microseconds).
        ::std::atomic<::std::int64_t>  m_lastMarkDurationUs;

        // Last-cycle safe-point duration (microseconds).
        ::std::atomic<::std::int64_t>  m_lastSafePointDurationUs;

        // Last-cycle saturation-fallback flag.
        ::std::atomic<bool>            m_lastCycleSaturationFallback;

        // Last-cycle sweep duration (microseconds; Phase 5.h).
        ::std::atomic<::std::int64_t>  m_lastSweepDurationUs;

        // Last-cycle reclaimed slot count (Phase 5.h). Bumps once per
        // BeginDestroy dispatch in the sweep phase. The actual slot-
        // release count is the FXDeferredDestructionQueue's province
        // (it varies per sim-tick pass).
        ::std::atomic<::std::size_t>   m_lastReclaimedCount;

        // Last-cycle EliminateGarbageRefs count (Phase 5.h).
        ::std::atomic<::std::size_t>   m_lastGarbageRefsClearedCount;

        // Trigger event the marker thread sleeps on. Heap-allocated
        // via FEvent::CreateAutoReset; freed in dtor via Destroy.
        // nullptr until __Init spawns the marker thread (lazy alloc).
        ::XCore::HAL::FEvent*          m_triggerEvent;

        // Coalesced-trigger flag: set on Trigger; cleared on cycle
        // start. Prevents Trigger spamming from queuing more than one
        // pending cycle.
        ::std::atomic<bool>            m_triggerPending;

        // Last trigger reason (for telemetry).
        ::std::atomic<::std::uint32_t> m_lastTriggerReason;

        // Shutdown flag the marker thread polls.
        ::std::atomic<bool>            m_shutdownRequested;

        // Marker thread join handle. Default-constructed (not-joinable)
        // until __Init spawns. We use std::thread; the engine's
        // FPlatformProcess wrappers don't yet expose a generic
        // background-thread spawn primitive.
        ::std::thread                  m_markerThread;

        // Mutex guarding marker-thread spawn/join. Avoids races
        // between concurrent __Init / Shutdown calls (should never
        // happen but defence-in-depth).
        mutable ::XCore::HAL::FCriticalSection m_threadLifecycleLock;

        // Cycle-active guard mutex. Prevents two threads from
        // running RunCycle simultaneously (e.g., CollectGarbage on
        // main thread + Trigger waking marker thread). The marker
        // thread acquires this mutex for the duration of its cycle;
        // CollectGarbage acquires the same mutex.
        mutable ::XCore::HAL::FCriticalSection m_cycleLock;
    };

} // namespace XCore
