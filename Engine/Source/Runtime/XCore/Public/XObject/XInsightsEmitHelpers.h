// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XInsightsEmitHelpers.h -- per-event emit-site convenience wrappers
// (XCoreXObject Rev 4 §10.5 + §10.12; Phase 5.k).
// =====================================================================
//
// Each helper packs the spec-defined payload schema for one event
// (per the §10.12 table), then dispatches through the
// XInsightsBridge::Emit weak-symbol bridge. The helpers are
// XPACT_FORCEINLINE so production emit sites compile to:
//
//   1. Construct FXInsightsPayload on stack (zero allocation; empty
//      TArray).
//   2. Add() per field (each call appends to the TArray; the first
//      Add allocates the initial capacity-1 buffer).
//   3. Call XInsightsBridge::Emit (single CALL to extern "C" stub).
//
// When XInsights is NOT linked, the bridge body is a single RET; the
// per-event cost is dominated by the payload construction (~50-100 ns
// per emit for 3-5 fields). When XInsights IS linked, the additional
// cost is whatever the strong provider does with the payload (the
// spec wording is "callbacks copy into thread-local buffers OR enqueue
// MPSC"; either way bounded by O(payload field count)).
//
// =====================================================================
//
// HELPER DESIGN INVARIANT:
//
// Every helper:
//   * Is XPACT_FORCEINLINE.
//   * Constructs a fresh FXInsightsPayload on stack (no shared state).
//   * Adds fields in a fixed order matching the §10.12 schema.
//   * Calls XInsightsBridge::Emit with the proper Category + Event
//     FName pair.
//   * Returns void.
//
// The fixed Add order is documented per-helper. Consumers reading
// the payload via ForEach observe the same field order across emits
// of the same event type, so a downstream UI can format the fields
// without per-payload schema discovery.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Reflection/FName.h"
#include "XObject/FXInsightsPayload.h"
#include "XObject/XInsightsBridge.h"
#include "XObject/XInsightsEvents.h"

#include <cstddef>
#include <cstdint>

namespace XCore::HAL::XInsightsEmitHelpers
{
    // =================================================================
    // GC.CycleComplete -- emitted at end of each GC cycle (Phase 5.g+).
    //
    // Spec §10.12 schema:
    //   {markDurationMs:f64, sweepDurationMs:f64, dirtyCardCount:i64,
    //    reclaimedCount:i64, throughputObjPerMs:f64, cycleId:i64}
    //
    // Phase 5.k publishes the helper with markDurationUs (microseconds,
    // i64) + sweepDurationUs (i64) as the integer-microsecond form per
    // the dispatch spec wording. The throughput is MB/s in this signature
    // (the spec table says "throughputObjPerMs" but the dispatch said
    // "throughputMBps"; the dispatch is the more recent direction so
    // Phase 5.k follows it -- spec reconciliation pass can pick either
    // unit without breaking the variant-typed payload).
    //
    // PRIME-DIRECTIVE DIVERGENCE DISCLOSURE: spec wording at §10.12 says
    // "markDurationMs / sweepDurationMs (f64)"; this helper signature
    // says "markDurationUs / sweepDurationUs (i64)". Dispatch wording
    // is the more recent direction; integer-microsecond resolution is
    // bit-exact (no FP rounding at the emit site). The downstream
    // consumer can convert i64 microseconds -> f64 milliseconds in
    // ~3 cycles. Phase 5.k follows the dispatch; spec-side editorial
    // pass can re-align if desired.
    // =================================================================
    XPACT_FORCEINLINE void EmitGCCycleComplete(
        ::std::int64_t MarkDurationUs,
        ::std::int64_t SweepDurationUs,
        ::std::int64_t DirtyCardCount,
        ::std::int64_t ReclaimedCount,
        double         ThroughputMBps,
        ::std::int64_t CycleId) noexcept
    {
        ::XCore::HAL::FXInsightsPayload Payload;
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::MarkDurationUs(),  MarkDurationUs);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::SweepDurationUs(), SweepDurationUs);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::DirtyCardCount(),  DirtyCardCount);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::ReclaimedCount(),  ReclaimedCount);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::ThroughputMBps(),  ThroughputMBps);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::CycleId(),         CycleId);

        ::XCore::HAL::XInsightsBridge::Emit(
            ::XCore::HAL::XInsightsEvents::CategoryGC(),
            ::XCore::HAL::XInsightsEvents::GC::CycleComplete(),
            Payload);
    }

    // =================================================================
    // GC.FullScanFallback -- emitted when the GC falls back to a full-
    // heap scan instead of card-table walk (Phase 5.g+).
    //
    // Spec §10.12 schema:
    //   {reason:string, durationMs:f64, heapSizeBytes:i64,
    //    remSetSizeBytes:i64, cycleId:i64}
    //
    // The reason is an FName (interned identifier; tag-style). The
    // typical values are:
    //   * "RemSetSaturation"     -- 50% card-table saturation hit.
    //   * "ManualTrigger"        -- engineer-station manual full GC.
    //   * "RootSetGrew"          -- heap grew beyond card-table coverage.
    //
    // =================================================================
    XPACT_FORCEINLINE void EmitGCFullScanFallback(
        ::XCore::Reflect::FName Reason,
        ::std::int64_t          DurationMs,
        ::std::int64_t          HeapSizeBytes,
        ::std::int64_t          RemSetSizeBytes,
        ::std::int64_t          CycleId) noexcept
    {
        ::XCore::HAL::FXInsightsPayload Payload;
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::Reason(),          Reason);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::DurationMs(),      DurationMs);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::HeapSizeBytes(),   HeapSizeBytes);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::RemSetSizeBytes(), RemSetSizeBytes);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::CycleId(),         CycleId);

        ::XCore::HAL::XInsightsBridge::Emit(
            ::XCore::HAL::XInsightsEvents::CategoryGC(),
            ::XCore::HAL::XInsightsEvents::GC::FullScanFallback(),
            Payload);
    }

    // =================================================================
    // GC.MarkStart -- emitted at the start of the mark phase (Phase 5.g).
    //
    // Additive to spec §10.12. Payload schema:
    //   {cycleId:i64, reachabilityIndex:i64, rootCount:i64}
    //
    // ReachabilityIndex is 0/1/2 selecting which of XObject.
    // ReachabilityFlag's bits 0..2 is "this-cycle's" mark per the
    // rotating-flag scheme (spec §4.2.1).
    //
    // RootCount is the count of objects enqueued onto the gray queue
    // during root enumeration (pinned roots + RootSpan typed entries +
    // conservative-validated entries + StrongPtr-refcounted entries +
    // Outer chains).
    // =================================================================
    XPACT_FORCEINLINE void EmitGCMarkStart(
        ::std::int64_t CycleId,
        ::std::int64_t ReachabilityIndex,
        ::std::int64_t RootCount) noexcept
    {
        ::XCore::HAL::FXInsightsPayload Payload;
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::CycleId(),           CycleId);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::ReachabilityIndex(), ReachabilityIndex);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::RootCount(),         RootCount);

        ::XCore::HAL::XInsightsBridge::Emit(
            ::XCore::HAL::XInsightsEvents::CategoryGC(),
            ::XCore::HAL::XInsightsEvents::GC::MarkStart(),
            Payload);
    }

    // =================================================================
    // GC.MarkEnd -- emitted at the end of the concurrent mark phase
    // (Phase 5.g).
    //
    // Additive to spec §10.12. Payload schema:
    //   {cycleId:i64, markedCount:i64, durationUs:i64, grayQueuePeak:i64}
    //
    // MarkedCount is the total objects whose ReachabilityFlag was
    // transitioned from clear to set this cycle (the first-mark count;
    // re-marks are NOT counted because the rotating-flag CAS short-
    // circuits the visit).
    //
    // GrayQueuePeak is the maximum observed depth of the gray queue
    // across the mark phase, useful for sizing.
    // =================================================================
    XPACT_FORCEINLINE void EmitGCMarkEnd(
        ::std::int64_t CycleId,
        ::std::int64_t MarkedCount,
        ::std::int64_t DurationUs,
        ::std::int64_t GrayQueuePeak) noexcept
    {
        ::XCore::HAL::FXInsightsPayload Payload;
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::CycleId(),       CycleId);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::MarkedCount(),   MarkedCount);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::DurationUs(),    DurationUs);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::GrayQueuePeak(), GrayQueuePeak);

        ::XCore::HAL::XInsightsBridge::Emit(
            ::XCore::HAL::XInsightsEvents::CategoryGC(),
            ::XCore::HAL::XInsightsEvents::GC::MarkEnd(),
            Payload);
    }

    // =================================================================
    // GC.FinalDrainStart -- emitted at the start of final-mark drain
    // (Phase 5.g).
    //
    // The final drain is the synchronous-with-mutator drain of SATB
    // log + dirty-card re-scan that ends the mark phase. Spec §4.8
    // budget: 1-3 ms.
    //
    // Payload schema: {cycleId:i64, satbQueueDepth:i64, dirtyCardCount:i64}
    // =================================================================
    XPACT_FORCEINLINE void EmitGCFinalDrainStart(
        ::std::int64_t CycleId,
        ::std::int64_t SatbQueueDepth,
        ::std::int64_t DirtyCardCount) noexcept
    {
        ::XCore::HAL::FXInsightsPayload Payload;
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::CycleId(),        CycleId);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::SatbQueueDepth(), SatbQueueDepth);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::DirtyCardCount(), DirtyCardCount);

        ::XCore::HAL::XInsightsBridge::Emit(
            ::XCore::HAL::XInsightsEvents::CategoryGC(),
            ::XCore::HAL::XInsightsEvents::GC::FinalDrainStart(),
            Payload);
    }

    // =================================================================
    // GC.FinalDrainEnd -- emitted at the end of final-mark drain
    // (Phase 5.g).
    //
    // Payload schema:
    //   {cycleId:i64, finalMarkedCount:i64, satbResidual:i64,
    //    durationUs:i64}
    //
    // FinalMarkedCount is the additional objects marked during the
    // final drain (over and above the concurrent-mark count).
    // SatbResidual is the count of SATB entries that arrived AFTER the
    // drain completed (should typically be 0 because the mark phase
    // sets g_XGCIsConcurrentMarkActive to false BEFORE drain end).
    // =================================================================
    XPACT_FORCEINLINE void EmitGCFinalDrainEnd(
        ::std::int64_t CycleId,
        ::std::int64_t FinalMarkedCount,
        ::std::int64_t SatbResidual,
        ::std::int64_t DurationUs) noexcept
    {
        ::XCore::HAL::FXInsightsPayload Payload;
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::CycleId(),          CycleId);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::FinalMarkedCount(), FinalMarkedCount);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::SatbResidual(),     SatbResidual);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::DurationUs(),       DurationUs);

        ::XCore::HAL::XInsightsBridge::Emit(
            ::XCore::HAL::XInsightsEvents::CategoryGC(),
            ::XCore::HAL::XInsightsEvents::GC::FinalDrainEnd(),
            Payload);
    }

    // =================================================================
    // GC.SafePointEntered / GC.SafePointExited -- safe-point pause
    // boundary events (Phase 5.g).
    //
    // The safe-point handshake is the synchronous-with-mutator window
    // during which the root snapshot is taken + dirty cards are
    // captured. Spec §4.8 budget: 50-200 us.
    //
    // SafePointEntered payload: {cycleId:i64}
    // SafePointExited payload:  {cycleId:i64, durationUs:i64}
    // =================================================================
    XPACT_FORCEINLINE void EmitGCSafePointEntered(
        ::std::int64_t CycleId) noexcept
    {
        ::XCore::HAL::FXInsightsPayload Payload;
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::CycleId(), CycleId);

        ::XCore::HAL::XInsightsBridge::Emit(
            ::XCore::HAL::XInsightsEvents::CategoryGC(),
            ::XCore::HAL::XInsightsEvents::GC::SafePointEntered(),
            Payload);
    }

    XPACT_FORCEINLINE void EmitGCSafePointExited(
        ::std::int64_t CycleId,
        ::std::int64_t DurationUs) noexcept
    {
        ::XCore::HAL::FXInsightsPayload Payload;
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::CycleId(),    CycleId);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::DurationUs(), DurationUs);

        ::XCore::HAL::XInsightsBridge::Emit(
            ::XCore::HAL::XInsightsEvents::CategoryGC(),
            ::XCore::HAL::XInsightsEvents::GC::SafePointExited(),
            Payload);
    }

    // =================================================================
    // GC.RememberedSetSaturation -- emitted when the card table's
    // dirty-card count crosses the 50% saturation threshold (Phase
    // 5.f's FXObjectGCCardTable).
    //
    // Spec §10.12 schema:
    //   {dirtyCardCount:i64, totalCardCount:i64, saturationPct:f64,
    //    cycleId:i64}
    //
    // Phase 5.k Phase-1 emit does NOT have cycleId (the FXObjectGCCardTable
    // does not know the GC cycle counter; that lives in Phase 5.g's
    // FXObjectCollector). Phase 5.k passes 0 for CycleId; Phase 5.g's
    // collector overrides via re-emit if needed.
    // =================================================================
    XPACT_FORCEINLINE void EmitGCRememberedSetSaturation(
        ::std::int64_t DirtyCardCount,
        ::std::int64_t TotalCardCount,
        double         SaturationPct,
        ::std::int64_t CycleId) noexcept
    {
        ::XCore::HAL::FXInsightsPayload Payload;
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::DirtyCardCount(), DirtyCardCount);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::TotalCardCount(), TotalCardCount);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::SaturationPct(),  SaturationPct);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::CycleId(),        CycleId);

        ::XCore::HAL::XInsightsBridge::Emit(
            ::XCore::HAL::XInsightsEvents::CategoryGC(),
            ::XCore::HAL::XInsightsEvents::GC::RememberedSetSaturation(),
            Payload);
    }

    // =================================================================
    // Allocator.AllocationFailure -- emitted just before the
    // FXObjectAllocator aborts on OOM.
    //
    // Spec §10.12 schema:
    //   {sizeClass:i64, requestedBytes:i64, tag:FName-or-string}
    //
    // Phase 5.b's FXObjectAllocator currently uses FMemory::Malloc
    // OrAbort which aborts BEFORE returning. The emit fires just
    // before the abort so a telemetry consumer with a sync-on-abort
    // pipeline sees the failure context.
    // =================================================================
    XPACT_FORCEINLINE void EmitAllocFailure(
        ::std::int32_t          SizeClass,
        ::std::size_t           RequestedBytes,
        ::XCore::Reflect::FName Tag) noexcept
    {
        ::XCore::HAL::FXInsightsPayload Payload;
        Payload.Add(
            ::XCore::HAL::XInsightsEvents::Keys::SizeClass(),
            static_cast<::std::int64_t>(SizeClass));
        Payload.Add(
            ::XCore::HAL::XInsightsEvents::Keys::RequestedBytes(),
            static_cast<::std::int64_t>(RequestedBytes));
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::Tag(), Tag);

        ::XCore::HAL::XInsightsBridge::Emit(
            ::XCore::HAL::XInsightsEvents::CategoryAllocator(),
            ::XCore::HAL::XInsightsEvents::Allocator::AllocationFailure(),
            Payload);
    }

    // =================================================================
    // Allocator.PoolGrew -- emitted from the slab-grow path inside
    // FXObjectAllocator::AllocateRaw, immediately after a new slab is
    // integrated into the size-class pool.
    //
    // Spec §10.12 schema:
    //   {sizeClass:i64, newSlabCount:i64, totalBytes:i64}
    //
    // sizeClass is the size-class INDEX (0..9) not the byte width.
    // newSlabCount is the post-grow slab count for the size class.
    // totalBytes is the cumulative slab byte total for the size class.
    // =================================================================
    XPACT_FORCEINLINE void EmitPoolGrew(
        ::std::int32_t SizeClass,
        ::std::int64_t NewSlabCount,
        ::std::int64_t TotalBytes) noexcept
    {
        ::XCore::HAL::FXInsightsPayload Payload;
        Payload.Add(
            ::XCore::HAL::XInsightsEvents::Keys::SizeClass(),
            static_cast<::std::int64_t>(SizeClass));
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::NewSlabCount(), NewSlabCount);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::TotalBytes(),   TotalBytes);

        ::XCore::HAL::XInsightsBridge::Emit(
            ::XCore::HAL::XInsightsEvents::CategoryAllocator(),
            ::XCore::HAL::XInsightsEvents::Allocator::PoolGrew(),
            Payload);
    }

    // =================================================================
    // Allocator.PoolReleaseAtScenarioBoundary -- emitted from
    // FXObjectAllocator::EndScenarioBoundary when a scope closes.
    //
    // Spec §10.12 schema:
    //   {scenarioName:FName-or-string, releasedObjects:i64,
    //    releasedBytes:i64}
    //
    // Phase 5.k Phase-1 emit reports the scope-close name; the actual
    // mark-region-clearing release count is gated until Phase 5.h
    // (which provides the reachability oracle). Phase 5.k passes 0
    // for ReleasedObjects + ReleasedBytes when the action is gated.
    // =================================================================
    XPACT_FORCEINLINE void EmitPoolReleaseAtScenarioBoundary(
        ::XCore::Reflect::FName ScenarioName,
        ::std::int64_t          ReleasedObjects,
        ::std::int64_t          ReleasedBytes) noexcept
    {
        ::XCore::HAL::FXInsightsPayload Payload;
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::ScenarioName(),    ScenarioName);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::ReleasedObjects(), ReleasedObjects);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::ReleasedBytes(),   ReleasedBytes);

        ::XCore::HAL::XInsightsBridge::Emit(
            ::XCore::HAL::XInsightsEvents::CategoryAllocator(),
            ::XCore::HAL::XInsightsEvents::Allocator::PoolReleaseAtScenarioBoundary(),
            Payload);
    }

    // =================================================================
    // Allocator.FragmentationThresholdCrossed -- emitted by
    // FXObjectAllocator::CoalesceIdleSlabs when the post-coalesce
    // fragmentation percent crosses the spec's 25% threshold.
    //
    // Spec §10.12 schema:
    //   {sizeClass:i64, wastedPct:f64, totalAllocatedBytes:i64}
    //
    // Phase 5.k Phase-1 emit reports the AGGREGATE (sizeClass == -1
    // sentinel meaning "across all size classes"). Future phases can
    // refine to per-size-class emit if the consumer-side panel needs
    // the granularity.
    // =================================================================
    XPACT_FORCEINLINE void EmitFragmentationThresholdCrossed(
        ::std::int32_t SizeClass,
        double         WastedPct,
        ::std::int64_t TotalAllocatedBytes) noexcept
    {
        ::XCore::HAL::FXInsightsPayload Payload;
        Payload.Add(
            ::XCore::HAL::XInsightsEvents::Keys::SizeClass(),
            static_cast<::std::int64_t>(SizeClass));
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::WastedPct(),           WastedPct);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::TotalAllocatedBytes(), TotalAllocatedBytes);

        ::XCore::HAL::XInsightsBridge::Emit(
            ::XCore::HAL::XInsightsEvents::CategoryAllocator(),
            ::XCore::HAL::XInsightsEvents::Allocator::FragmentationThresholdCrossed(),
            Payload);
    }

    // =================================================================
    // HotReload.CascadeApplied -- emitted from Phase 5.j hot-reload
    // path. Phase 5.k publishes the helper for forward compatibility.
    //
    // Spec §10.12 schema:
    //   {classesReplaced:i64, propertiesAdded:i64, modules:FName-or-string,
    //    durationMs:f64}
    // =================================================================
    XPACT_FORCEINLINE void EmitHotReloadCascadeApplied(
        ::std::int64_t          ClassesReplaced,
        ::std::int64_t          PropertiesAdded,
        ::XCore::Reflect::FName Modules,
        double                  DurationMs) noexcept
    {
        ::XCore::HAL::FXInsightsPayload Payload;
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::ClassesReplaced(), ClassesReplaced);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::PropertiesAdded(), PropertiesAdded);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::Modules(),         Modules);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::DurationMs(),
                    static_cast<::std::int64_t>(DurationMs));

        ::XCore::HAL::XInsightsBridge::Emit(
            ::XCore::HAL::XInsightsEvents::CategoryHotReload(),
            ::XCore::HAL::XInsightsEvents::HotReload::CascadeApplied(),
            Payload);
    }

    // =================================================================
    // HotReload.ClassReplaced -- emitted from Phase 5.j per class
    // replaced. Phase 5.k publishes the helper for forward compatibility.
    //
    // Spec §10.12 schema:
    //   {className:FName-or-string, oldVersion:i64, newVersion:i64,
    //    instanceCount:i64}
    // =================================================================
    XPACT_FORCEINLINE void EmitHotReloadClassReplaced(
        ::XCore::Reflect::FName ClassName,
        ::std::int64_t          OldVersion,
        ::std::int64_t          NewVersion,
        ::std::int64_t          InstanceCount) noexcept
    {
        ::XCore::HAL::FXInsightsPayload Payload;
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::ClassName(),     ClassName);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::OldVersion(),    OldVersion);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::NewVersion(),    NewVersion);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::InstanceCount(), InstanceCount);

        ::XCore::HAL::XInsightsBridge::Emit(
            ::XCore::HAL::XInsightsEvents::CategoryHotReload(),
            ::XCore::HAL::XInsightsEvents::HotReload::ClassReplaced(),
            Payload);
    }

    // =================================================================
    // HotReload.QuiesceWaited -- emitted from Phase 5.j quiesce
    // synchronization. Phase 5.k publishes the helper.
    //
    // Spec §10.12 schema:
    //   {quiescedThreadCount:i64, waitDurationMs:f64}
    // =================================================================
    XPACT_FORCEINLINE void EmitHotReloadQuiesceWaited(
        ::std::int64_t QuiescedThreadCount,
        double         WaitDurationMs) noexcept
    {
        ::XCore::HAL::FXInsightsPayload Payload;
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::QuiescedThreadCount(), QuiescedThreadCount);
        Payload.Add(::XCore::HAL::XInsightsEvents::Keys::WaitDurationMs(),      WaitDurationMs);

        ::XCore::HAL::XInsightsBridge::Emit(
            ::XCore::HAL::XInsightsEvents::CategoryHotReload(),
            ::XCore::HAL::XInsightsEvents::HotReload::QuiesceWaited(),
            Payload);
    }

} // namespace XCore::HAL::XInsightsEmitHelpers
