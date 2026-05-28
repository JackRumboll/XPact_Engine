// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XInsightsEvents.h -- canonical FName-typed event identifiers per
// XCoreXObject Rev 4 §10.12 + FIX-A-MED-32.
// =====================================================================
//
// Spec §10.12 lists the complete set of XInsights events XCoreXObject
// emits, grouped by Category. Phase 5.k publishes one accessor per
// event (returns the interned FName) plus one accessor per category
// (returns the "XCoreXObject.<Category>" FName).
//
// LAZY-INIT DESIGN (Prime Directive):
//
//   FName(const char*) is NOT constexpr (it touches the FNamePool
//   intern table). Compile-time-emitted FName globals therefore cannot
//   be `constinit`. The spec at §10.5 calls for "FName-typed event
//   identifiers" -- the principled posture is FUNCTION-LOCAL-STATIC
//   accessors using the C++11 magic-statics rule. Each accessor
//   constructs the FName exactly once, on first call, and returns a
//   const-ref thereafter.
//
//   Cost per accessor call after first-call: one acquire-load + one
//   stable-pointer return (~3 cycles). The first-call interns the
//   bytes via FNamePool's shared-read fast path (~30 ns).
//
//   Each accessor is `inline` so the linker dedupes across TUs; the
//   magic-static guard variable lives in exactly one .rdata slot per
//   accessor across the whole program.
//
// PHASE GATING:
//
//   Accessors require `EInitPhase >= PostStaticInit` (the FNamePool
//   is alive after PostStaticInit; pre-PostStaticInit access would
//   touch a not-yet-initialised intern table). Phase 5.k accessors
//   include the XPACT_CHECK from FName's own ctor (which is debug+dev
//   only; shipping accepts the early call as UB matching the
//   FString discipline).
//
//   Production emit sites (the Phase 5.k FXObjectAllocator + Phase 5.f
//   FXObjectGCCardTable hooks) all run AFTER PostStaticInit so the
//   accessors are safe by construction.
//
// CATEGORY NAMES:
//
//   The category top-level FName is "XCoreXObject.<Sub>" (e.g.,
//   "XCoreXObject.GC", "XCoreXObject.Allocator", "XCoreXObject.
//   HotReload"). The dot separator matches XLog's category naming
//   convention (spec §10.11) so a single FName can identify both the
//   XInsights category AND the XLog category for the same subsystem.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Reflection/FName.h"

namespace XCore::HAL::XInsightsEvents
{
    // =================================================================
    // Category accessors.
    //
    // Returns the top-level "XCoreXObject.<Sub>" FName. Used as the
    // Category parameter to XInsightsBridge::Emit.
    //
    // The naming uses a function-call surface (not a const FName&
    // variable) so the magic-static initialisation is enforced at
    // first-use, not at static-init time.
    // =================================================================

    // -----------------------------------------------------------------
    // CategoryGC -- "XCoreXObject.GC".
    //
    // Used by the FXObjectCollector mark + sweep phases (Phase 5.g +
    // 5.h) AND by the Phase 5.f card-table saturation event.
    // -----------------------------------------------------------------
    [[nodiscard]] inline const ::XCore::Reflect::FName& CategoryGC() noexcept
    {
        static const ::XCore::Reflect::FName Name("XCoreXObject.GC");
        return Name;
    }

    // -----------------------------------------------------------------
    // CategoryAllocator -- "XCoreXObject.Allocator".
    //
    // Used by Phase 5.b FXObjectAllocator emit sites: allocation
    // failure, pool grew, scenario-boundary release, fragmentation
    // threshold crossed.
    // -----------------------------------------------------------------
    [[nodiscard]] inline const ::XCore::Reflect::FName& CategoryAllocator() noexcept
    {
        static const ::XCore::Reflect::FName Name("XCoreXObject.Allocator");
        return Name;
    }

    // -----------------------------------------------------------------
    // CategoryHotReload -- "XCoreXObject.HotReload".
    //
    // Used by Phase 5.j hot-reload integration (forward commitment).
    // Phase 5.k publishes the FName so Phase 5.j call sites compile
    // against the same surface; no emit sites in Phase 5.k itself.
    // -----------------------------------------------------------------
    [[nodiscard]] inline const ::XCore::Reflect::FName& CategoryHotReload() noexcept
    {
        static const ::XCore::Reflect::FName Name("XCoreXObject.HotReload");
        return Name;
    }

    // =================================================================
    // GC event names (spec §10.12 row block).
    //
    // Payload schemas per the §10.12 table:
    //   * CycleComplete:       {markDurationMs:f64, sweepDurationMs:f64,
    //                            dirtyCardCount:i64, reclaimedCount:i64,
    //                            throughputObjPerMs:f64, cycleId:i64}
    //   * FullScanFallback:    {reason:FName-or-string, durationMs:f64,
    //                            heapSizeBytes:i64, remSetSizeBytes:i64,
    //                            cycleId:i64}
    //   * RememberedSetSaturation:
    //                          {dirtyCardCount:i64, totalCardCount:i64,
    //                            saturationPct:f64, cycleId:i64}
    //
    // Phase 5.k publishes the FNames + the EmitHelpers; the production
    // emit sites land at Phase 5.g (CycleComplete + FullScanFallback)
    // and Phase 5.f's card-table saturation hook
    // (RememberedSetSaturation).
    // =================================================================

    namespace GC
    {
        [[nodiscard]] inline const ::XCore::Reflect::FName& CycleComplete() noexcept
        {
            static const ::XCore::Reflect::FName Name("CycleComplete");
            return Name;
        }

        [[nodiscard]] inline const ::XCore::Reflect::FName& FullScanFallback() noexcept
        {
            static const ::XCore::Reflect::FName Name("FullScanFallback");
            return Name;
        }

        [[nodiscard]] inline const ::XCore::Reflect::FName& RememberedSetSaturation() noexcept
        {
            static const ::XCore::Reflect::FName Name("RememberedSetSaturation");
            return Name;
        }

        // -----------------------------------------------------------------
        // Phase 5.g mark-phase events (additive to spec §10.12).
        //
        // The spec §10.12 table enumerates three GC events (CycleComplete,
        // FullScanFallback, RememberedSetSaturation). Phase 5.g adds six
        // more events that surface the mark-phase substructure to
        // telemetry consumers. Justification per Prime Directive: the
        // mark phase is the single most expensive GC sub-phase; the
        // spec's `CycleComplete` carries aggregated mark/sweep durations
        // but does NOT expose per-sub-phase pause budget breakdowns. The
        // spec §4.8 acceptance table itemises mark / safe-point / final-
        // drain budgets independently; emitting per-sub-phase events is
        // required to verify those budgets are honoured at runtime.
        //
        // The spec-side editorial pass can adopt these events into §10.12
        // without churn (the FName accessor surface is forward-compatible).
        // -----------------------------------------------------------------

        // Mark phase begin. Payload: {cycleId:i64, reachabilityIndex:i64,
        // rootCount:i64}.
        [[nodiscard]] inline const ::XCore::Reflect::FName& MarkStart() noexcept
        {
            static const ::XCore::Reflect::FName Name("MarkStart");
            return Name;
        }

        // Mark phase end. Payload: {cycleId:i64, markedCount:i64,
        // durationUs:i64, grayQueuePeak:i64}.
        [[nodiscard]] inline const ::XCore::Reflect::FName& MarkEnd() noexcept
        {
            static const ::XCore::Reflect::FName Name("MarkEnd");
            return Name;
        }

        // Final-mark drain begin. Payload: {cycleId:i64, satbQueueDepth:i64,
        // dirtyCardCount:i64}.
        [[nodiscard]] inline const ::XCore::Reflect::FName& FinalDrainStart() noexcept
        {
            static const ::XCore::Reflect::FName Name("FinalDrainStart");
            return Name;
        }

        // Final-mark drain end. Payload: {cycleId:i64, finalMarkedCount:i64,
        // satbResidual:i64, durationUs:i64}.
        [[nodiscard]] inline const ::XCore::Reflect::FName& FinalDrainEnd() noexcept
        {
            static const ::XCore::Reflect::FName Name("FinalDrainEnd");
            return Name;
        }

        // Safe-point entered (the synchronous-with-mutator window opens).
        // Payload: {cycleId:i64}.
        [[nodiscard]] inline const ::XCore::Reflect::FName& SafePointEntered() noexcept
        {
            static const ::XCore::Reflect::FName Name("SafePointEntered");
            return Name;
        }

        // Safe-point exited (the synchronous-with-mutator window closes).
        // Payload: {cycleId:i64, durationUs:i64}.
        [[nodiscard]] inline const ::XCore::Reflect::FName& SafePointExited() noexcept
        {
            static const ::XCore::Reflect::FName Name("SafePointExited");
            return Name;
        }

        // -----------------------------------------------------------------
        // Phase 5.h sweep-phase events (additive to spec §10.12).
        //
        // The spec §10.12 table's `CycleComplete` event carries the
        // aggregated sweep duration + reclaimed count but does NOT expose
        // per-sub-phase pause-budget breakdowns for the SWEEP phase. The
        // spec §4.8 acceptance table itemises sweep / deferred-destruction
        // budgets independently; emitting per-sub-phase events lets the
        // runtime telemetry verify each budget independently.
        //
        // The spec-side editorial pass can adopt these events into §10.12
        // without churn (the FName accessor surface is forward-compatible).
        // -----------------------------------------------------------------

        // Sweep phase begin. Payload: {cycleId:i64, candidateCount:i64}.
        // candidateCount is the number of InternalIndex entries the sweep
        // is about to process (the FXSweepCandidateQueue's drained size).
        [[nodiscard]] inline const ::XCore::Reflect::FName& SweepStart() noexcept
        {
            static const ::XCore::Reflect::FName Name("SweepStart");
            return Name;
        }

        // Sweep phase end. Payload: {cycleId:i64, beginDestroyCount:i64,
        // garbageRefsCleared:i64, durationUs:i64}.
        // beginDestroyCount is the number of BeginDestroy dispatches that
        // fired this sweep. garbageRefsCleared is the number of slots
        // nulled by the kEliminateGarbageRefs pass.
        [[nodiscard]] inline const ::XCore::Reflect::FName& SweepEnd() noexcept
        {
            static const ::XCore::Reflect::FName Name("SweepEnd");
            return Name;
        }
    } // namespace GC

    // =================================================================
    // Allocator event names (spec §10.12 row block).
    //
    // Payload schemas per the §10.12 table:
    //   * AllocationFailure:
    //       {sizeClass:i64, requestedBytes:i64, tag:FName-or-string}
    //   * PoolGrew:
    //       {sizeClass:i64, newSlabCount:i64, totalBytes:i64}
    //   * PoolReleaseAtScenarioBoundary:
    //       {scenarioName:FName-or-string, releasedObjects:i64,
    //        releasedBytes:i64}
    //   * FragmentationThresholdCrossed:
    //       {sizeClass:i64, wastedPct:f64, totalAllocatedBytes:i64}
    //
    // Phase 5.k wires the FXObjectAllocator emit sites:
    //   * AllocationFailure       -- emitted from the large-slab OOM
    //                                 fallback path AND from the
    //                                 size-class grow OOM path. Phase
    //                                 5.b's allocator currently uses
    //                                 FMemory::MallocOrAbort which
    //                                 aborts before returning; the
    //                                 emit fires JUST BEFORE the
    //                                 abort so the telemetry consumer
    //                                 sees the failure context. (Phase
    //                                 5.b's MVP path does not have a
    //                                 try-Malloc; the abort posture is
    //                                 documented at the call site.
    //                                 Phase 5.k DOES NOT change the
    //                                 abort semantics; the emit is
    //                                 additive.)
    //   * PoolGrew                -- emitted from the slab-grow path
    //                                 inside AllocateRaw, immediately
    //                                 after the new slab is integrated
    //                                 into the size-class pool.
    //   * PoolReleaseAtScenarioBoundary
    //                              -- emitted from EndScenarioBoundary.
    //                                 Phase 5.k's emit reports the
    //                                 scope name (NAME_None on the
    //                                 current Phase 5.b stack since the
    //                                 actual scenario-name-on-Begin is
    //                                 a forward commitment); future
    //                                 phases populate the real name.
    //   * FragmentationThresholdCrossed
    //                              -- emitted from CoalesceIdleSlabs
    //                                 when the empty-slab release
    //                                 brings the fragmentation percent
    //                                 BELOW the spec's 25% threshold
    //                                 (the inverse direction is the
    //                                 actual "crossed" signal -- the
    //                                 emit fires when the system
    //                                 EXITS the high-fragmentation
    //                                 regime). Phase 5.k Phase-1
    //                                 emit is on the post-coalesce
    //                                 transition.
    // =================================================================

    namespace Allocator
    {
        [[nodiscard]] inline const ::XCore::Reflect::FName& AllocationFailure() noexcept
        {
            static const ::XCore::Reflect::FName Name("AllocationFailure");
            return Name;
        }

        [[nodiscard]] inline const ::XCore::Reflect::FName& PoolGrew() noexcept
        {
            static const ::XCore::Reflect::FName Name("PoolGrew");
            return Name;
        }

        [[nodiscard]] inline const ::XCore::Reflect::FName& PoolReleaseAtScenarioBoundary() noexcept
        {
            static const ::XCore::Reflect::FName Name("PoolReleaseAtScenarioBoundary");
            return Name;
        }

        [[nodiscard]] inline const ::XCore::Reflect::FName& FragmentationThresholdCrossed() noexcept
        {
            static const ::XCore::Reflect::FName Name("FragmentationThresholdCrossed");
            return Name;
        }
    } // namespace Allocator

    // =================================================================
    // HotReload event names (spec §10.12 row block).
    //
    // Payload schemas per the §10.12 table:
    //   * CascadeApplied:
    //       {classesReplaced:i64, propertiesAdded:i64, modules:string,
    //        durationMs:f64}
    //   * ClassReplaced:
    //       {className:FName-or-string, oldVersion:i64, newVersion:i64,
    //        instanceCount:i64}
    //   * QuiesceWaited:
    //       {quiescedThreadCount:i64, waitDurationMs:f64}
    //
    // Phase 5.k publishes the FNames + the EmitHelpers; the production
    // emit sites land at Phase 5.j hot-reload integration. No emit
    // sites in Phase 5.k itself.
    // =================================================================

    namespace HotReload
    {
        [[nodiscard]] inline const ::XCore::Reflect::FName& CascadeApplied() noexcept
        {
            static const ::XCore::Reflect::FName Name("CascadeApplied");
            return Name;
        }

        [[nodiscard]] inline const ::XCore::Reflect::FName& ClassReplaced() noexcept
        {
            static const ::XCore::Reflect::FName Name("ClassReplaced");
            return Name;
        }

        [[nodiscard]] inline const ::XCore::Reflect::FName& QuiesceWaited() noexcept
        {
            static const ::XCore::Reflect::FName Name("QuiesceWaited");
            return Name;
        }
    } // namespace HotReload

    // =================================================================
    // Payload-key FNames -- canonical field names referenced by the
    // §10.12 table.
    //
    // The Emit-helpers (XInsightsEmitHelpers.h) use these to populate
    // the FXInsightsPayload's Key fields. Sharing the FName accessors
    // between the emit-side AND a future consumer-side (XInsights's
    // strong-symbol provider) means key matching is byte-exact through
    // the FName.Index equality (no string compare at the consumer).
    //
    // The list covers EVERY key in the §10.12 table (no duplicates;
    // some keys appear in multiple events).
    // =================================================================

    namespace Keys
    {
        // GC keys
        [[nodiscard]] inline const ::XCore::Reflect::FName& MarkDurationUs() noexcept
        { static const ::XCore::Reflect::FName Name("markDurationUs"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& SweepDurationUs() noexcept
        { static const ::XCore::Reflect::FName Name("sweepDurationUs"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& DirtyCardCount() noexcept
        { static const ::XCore::Reflect::FName Name("dirtyCardCount"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& TotalCardCount() noexcept
        { static const ::XCore::Reflect::FName Name("totalCardCount"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& ReclaimedCount() noexcept
        { static const ::XCore::Reflect::FName Name("reclaimedCount"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& ThroughputMBps() noexcept
        { static const ::XCore::Reflect::FName Name("throughputMBps"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& SaturationPct() noexcept
        { static const ::XCore::Reflect::FName Name("saturationPct"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& CycleId() noexcept
        { static const ::XCore::Reflect::FName Name("cycleId"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& Reason() noexcept
        { static const ::XCore::Reflect::FName Name("reason"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& DurationMs() noexcept
        { static const ::XCore::Reflect::FName Name("durationMs"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& HeapSizeBytes() noexcept
        { static const ::XCore::Reflect::FName Name("heapSizeBytes"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& RemSetSizeBytes() noexcept
        { static const ::XCore::Reflect::FName Name("remSetSizeBytes"); return Name; }

        // Phase 5.g mark-phase payload keys (additive to spec §10.12).
        [[nodiscard]] inline const ::XCore::Reflect::FName& ReachabilityIndex() noexcept
        { static const ::XCore::Reflect::FName Name("reachabilityIndex"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& RootCount() noexcept
        { static const ::XCore::Reflect::FName Name("rootCount"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& MarkedCount() noexcept
        { static const ::XCore::Reflect::FName Name("markedCount"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& DurationUs() noexcept
        { static const ::XCore::Reflect::FName Name("durationUs"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& GrayQueuePeak() noexcept
        { static const ::XCore::Reflect::FName Name("grayQueuePeak"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& SatbQueueDepth() noexcept
        { static const ::XCore::Reflect::FName Name("satbQueueDepth"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& FinalMarkedCount() noexcept
        { static const ::XCore::Reflect::FName Name("finalMarkedCount"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& SatbResidual() noexcept
        { static const ::XCore::Reflect::FName Name("satbResidual"); return Name; }

        // Phase 5.h sweep-phase payload keys (additive to spec §10.12).
        [[nodiscard]] inline const ::XCore::Reflect::FName& CandidateCount() noexcept
        { static const ::XCore::Reflect::FName Name("candidateCount"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& BeginDestroyCount() noexcept
        { static const ::XCore::Reflect::FName Name("beginDestroyCount"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& GarbageRefsCleared() noexcept
        { static const ::XCore::Reflect::FName Name("garbageRefsCleared"); return Name; }

        // Allocator keys
        [[nodiscard]] inline const ::XCore::Reflect::FName& SizeClass() noexcept
        { static const ::XCore::Reflect::FName Name("sizeClass"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& RequestedBytes() noexcept
        { static const ::XCore::Reflect::FName Name("requestedBytes"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& Tag() noexcept
        { static const ::XCore::Reflect::FName Name("tag"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& NewSlabCount() noexcept
        { static const ::XCore::Reflect::FName Name("newSlabCount"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& TotalBytes() noexcept
        { static const ::XCore::Reflect::FName Name("totalBytes"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& ScenarioName() noexcept
        { static const ::XCore::Reflect::FName Name("scenarioName"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& ReleasedObjects() noexcept
        { static const ::XCore::Reflect::FName Name("releasedObjects"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& ReleasedBytes() noexcept
        { static const ::XCore::Reflect::FName Name("releasedBytes"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& WastedPct() noexcept
        { static const ::XCore::Reflect::FName Name("wastedPct"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& TotalAllocatedBytes() noexcept
        { static const ::XCore::Reflect::FName Name("totalAllocatedBytes"); return Name; }

        // HotReload keys
        [[nodiscard]] inline const ::XCore::Reflect::FName& ClassesReplaced() noexcept
        { static const ::XCore::Reflect::FName Name("classesReplaced"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& PropertiesAdded() noexcept
        { static const ::XCore::Reflect::FName Name("propertiesAdded"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& Modules() noexcept
        { static const ::XCore::Reflect::FName Name("modules"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& ClassName() noexcept
        { static const ::XCore::Reflect::FName Name("className"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& OldVersion() noexcept
        { static const ::XCore::Reflect::FName Name("oldVersion"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& NewVersion() noexcept
        { static const ::XCore::Reflect::FName Name("newVersion"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& InstanceCount() noexcept
        { static const ::XCore::Reflect::FName Name("instanceCount"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& QuiescedThreadCount() noexcept
        { static const ::XCore::Reflect::FName Name("quiescedThreadCount"); return Name; }

        [[nodiscard]] inline const ::XCore::Reflect::FName& WaitDurationMs() noexcept
        { static const ::XCore::Reflect::FName Name("waitDurationMs"); return Name; }
    } // namespace Keys

    // =================================================================
    // PrewarmAtPostStaticInit -- optional eager-init of every accessor.
    //
    // Called from the XEngineInit bootstrap at PostStaticInit transition
    // (or from test harnesses after FMemory::__Init). After this
    // returns every accessor in this header is in its post-init state,
    // so subsequent emit-site calls observe the single-load fast path
    // without ever paying the FNamePool intern cost.
    //
    // Calling Prewarm is NOT REQUIRED for correctness; accessors lazy-
    // initialise on first use. Prewarm exists to shift the one-time
    // intern cost off the first-GC-cycle critical path.
    //
    // Body lives in Private/XObject/XInsightsEvents.cpp.
    // =================================================================
    void PrewarmAtPostStaticInit() noexcept;

} // namespace XCore::HAL::XInsightsEvents
