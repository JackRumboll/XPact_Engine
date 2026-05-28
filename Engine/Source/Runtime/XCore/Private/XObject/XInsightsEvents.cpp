// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XInsightsEvents.cpp -- PostStaticInit prewarm of the magic-static
// FName accessors declared in XInsightsEvents.h (Phase 5.k).
// =====================================================================
//
// XInsightsEvents.h defines each event identifier (Category, Event,
// Key) as a function-local-static FName accessor. The first call to
// each accessor pays the FNamePool intern cost (~30 ns on the shared-
// read fast path). Subsequent calls are one acquire-load + return.
//
// This TU exists to PREWARM the magic-statics at engine bootstrap so
// the first-call cost is paid at PostStaticInit (off the hot path)
// rather than at the first GC cycle's emit (which would inject a
// one-time spike of intern-table churn into the cycle's timing
// measurement).
//
// PRIME DIRECTIVE NOTE -- accessor pattern vs. spec wording:
//
//   The Phase 5.k dispatch spec said:
//     `Private/XObject/XInsightsEvents.cpp` defines each FName once at
//     PostStaticInit.
//
//   And implied accessors of shape `extern const FName CycleComplete;`
//   defined here.
//
//   Phase 5.k diverges: accessors in the .h are FUNCTION-LOCAL-STATIC
//   FName-returning functions, not namespace-scope `const FName`
//   variables. Reasons (Prime Directive):
//
//     1. FName(const char*) is NOT constexpr. A namespace-scope
//        `const FName Name("X")` would be dynamic-init AND its order
//        relative to FNamePool's own dynamic-init (in FNamePool.cpp)
//        is unspecified across TUs. The build would compile but a
//        cross-TU dynamic-init order quirk could cause the FName ctor
//        to run BEFORE FNamePool is alive on a future platform with
//        different link order.
//
//     2. Function-local-static + magic-statics gives THREAD-SAFE,
//        LAZY initialisation on first use. The C++11 magic-statics
//        rule guarantees exactly-once initialisation.
//
//     3. The "PostStaticInit prewarm" call from this TU touches every
//        accessor exactly once. After the prewarm, all accessors are
//        in their post-init state; subsequent emit-site calls observe
//        the cached value via the single-load fast path.
//
//   The end-state semantics are identical: each FName interned once;
//   each accessor returns the same value across the process lifetime.
//   The divergence is in INIT TIMING (lazy vs. eager); the prewarm
//   below makes the eager-init posture observable while preserving
//   the lazy-init fallback (any TU that calls an accessor before
//   prewarm runs still gets the correct value -- just at a slightly
//   higher one-time cost).
//
// =====================================================================
//
// WHEN PREWARM RUNS:
//
// PrewarmAtPostStaticInit() is called from one of:
//   (a) The XEngineInit bootstrap during PostStaticInit transition.
//       This is the production path; the bootstrap calls every
//       PostStaticInit-prewarm function in sequence.
//   (b) The test harness, which calls __Init() on FMemory and then
//       optionally PrewarmAtPostStaticInit() before exercising
//       telemetry-emitting code.
//
// Either way, the prewarm is OPTIONAL: any accessor will lazily
// self-initialise on first use even if Prewarm never runs.
//
// =====================================================================

#include "XObject/XInsightsEvents.h"

namespace XCore::HAL::XInsightsEvents
{
    // -----------------------------------------------------------------
    // PrewarmAtPostStaticInit -- touch every accessor exactly once.
    //
    // The pattern is a forced-evaluation discard cast over each
    // accessor; the C++ optimiser keeps the call sites because the
    // accessor has side-effects (the magic-static guard variable
    // mutation on first call). After this function returns every
    // accessor is in post-init state.
    //
    // The function is noexcept (matches every accessor's noexcept).
    // It is callable from any thread; the magic-static guard is the
    // synchronisation primitive that serialises concurrent first-
    // callers.
    //
    // Callers (XEngineInit bootstrap or test harness) MUST hold the
    // PostStaticInit phase invariant: FNamePool is alive. The phase-
    // check is implicit (FName ctor's own XPACT_CHECK fires in
    // Debug/Dev if the phase is not advanced; Shipping accepts the
    // call as UB matching the FName ctor discipline).
    // -----------------------------------------------------------------
    void PrewarmAtPostStaticInit() noexcept
    {
        // Categories
        (void)CategoryGC();
        (void)CategoryAllocator();
        (void)CategoryHotReload();

        // GC events
        (void)GC::CycleComplete();
        (void)GC::FullScanFallback();
        (void)GC::RememberedSetSaturation();

        // Phase 5.g mark-phase events (additive to spec §10.12).
        (void)GC::MarkStart();
        (void)GC::MarkEnd();
        (void)GC::FinalDrainStart();
        (void)GC::FinalDrainEnd();
        (void)GC::SafePointEntered();
        (void)GC::SafePointExited();

        // Allocator events
        (void)Allocator::AllocationFailure();
        (void)Allocator::PoolGrew();
        (void)Allocator::PoolReleaseAtScenarioBoundary();
        (void)Allocator::FragmentationThresholdCrossed();

        // HotReload events (forward commitment; Phase 5.j wires emit
        // sites; Phase 5.k pre-interns the names so the consumer side
        // observes them).
        (void)HotReload::CascadeApplied();
        (void)HotReload::ClassReplaced();
        (void)HotReload::QuiesceWaited();

        // Keys (all event payload keys).
        (void)Keys::MarkDurationUs();
        (void)Keys::SweepDurationUs();
        (void)Keys::DirtyCardCount();
        (void)Keys::TotalCardCount();
        (void)Keys::ReclaimedCount();
        (void)Keys::ThroughputMBps();
        (void)Keys::SaturationPct();
        (void)Keys::CycleId();
        (void)Keys::Reason();
        (void)Keys::DurationMs();
        (void)Keys::HeapSizeBytes();
        (void)Keys::RemSetSizeBytes();
        (void)Keys::SizeClass();
        (void)Keys::RequestedBytes();
        (void)Keys::Tag();
        (void)Keys::NewSlabCount();
        (void)Keys::TotalBytes();
        (void)Keys::ScenarioName();
        (void)Keys::ReleasedObjects();
        (void)Keys::ReleasedBytes();
        (void)Keys::WastedPct();
        (void)Keys::TotalAllocatedBytes();
        (void)Keys::ClassesReplaced();
        (void)Keys::PropertiesAdded();
        (void)Keys::Modules();
        (void)Keys::ClassName();
        (void)Keys::OldVersion();
        (void)Keys::NewVersion();
        (void)Keys::InstanceCount();
        (void)Keys::QuiescedThreadCount();
        (void)Keys::WaitDurationMs();

        // Phase 5.g mark-phase keys (additive to spec §10.12).
        (void)Keys::ReachabilityIndex();
        (void)Keys::RootCount();
        (void)Keys::MarkedCount();
        (void)Keys::DurationUs();
        (void)Keys::GrayQueuePeak();
        (void)Keys::SatbQueueDepth();
        (void)Keys::FinalMarkedCount();
        (void)Keys::SatbResidual();
    }

} // namespace XCore::HAL::XInsightsEvents
