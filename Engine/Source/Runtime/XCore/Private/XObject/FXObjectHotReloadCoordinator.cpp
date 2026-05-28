// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectHotReloadCoordinator.cpp -- hot-reload cascade orchestrator
// body (XCoreXObject Rev 4 §9 + §11.5; Phase 5.j).
// =====================================================================
//
// Implements the FXObjectHotReloadCoordinator's singleton accessor +
// the BeginHotReloadQuiesce / ApplyClassReplacement /
// FinishHotReloadCascade bracket per spec §9.2.
//
// CROSS-SYSTEM WIRING:
//
//   * BeginHotReloadQuiesce sets g_XHotReloadInProgress = true and
//     clears g_XGCAcceptDrains = false. Mutator threads observe both
//     flags (FXObjectArray::ReserveSlot reads g_XHotReloadInProgress;
//     FXObjectSatbQueue::Push reads g_XGCAcceptDrains via XGCWait
//     ForDrainsAccepted).
//
//   * ApplyClassReplacement walks the FXObjectArray via ForEachObject
//     and atomically stores the new FClass pointer into each instance's
//     ClassPrivate via std::atomic_ref (preserves byte-identity ABI).
//
//   * FinishHotReloadCascade triggers a deferred GC via FXObjectCollector
//     ::Trigger(kManual) (post-cascade reclamation per spec §9.2 step 5
//     trailing bullet).
//
// =====================================================================

#include "XObject/FXObjectHotReloadCoordinator.h"

#include "HAL/FPlatformTime.h"

#include "Reflection/FClass.h"
#include "Reflection/FStruct.h"

#include "XObject/EObjectFlags.h"
#include "HAL/FPlatformProcess.h"

#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectArray.h"
#include "XObject/FXObjectCollector.h"
#include "XObject/FXObjectHotReloadState.h"
#include "XObject/XCoreDelegates_OnClassReplaced.h"
#include "XObject/XCoreDelegates_OnHotReload.h"
#include "XObject/XGCConcurrentState.h"
#include "XObject/XInsightsEmitHelpers.h"
#include "XObject/XObject.h"

#include <atomic>
#include <cstddef>
#include <cstdint>

namespace XCore
{

// =====================================================================
// Helpers.
// =====================================================================

namespace
{
    // Convert a double "seconds since start" delta to int64 microseconds
    // with clamp-to-zero. Used for telemetry payload widths.
    [[nodiscard]] XPACT_FORCEINLINE ::std::int64_t SecondsToMicros(
        double DeltaSeconds) noexcept
    {
        if (DeltaSeconds <= 0.0)
        {
            return 0;
        }
        const double Micros = DeltaSeconds * 1'000'000.0;
        return static_cast<::std::int64_t>(Micros);
    }

    // Microseconds since process start. Wraps FPlatformTime::Seconds()
    // + the rate calibration; returns int64 microseconds.
    [[nodiscard]] ::std::int64_t NowUs() noexcept
    {
        return SecondsToMicros(::XCore::HAL::FPlatformTime::Seconds());
    }
} // namespace

// =====================================================================
// FXObjectHotReloadCoordinator -- singleton + ctor / dtor.
// =====================================================================

FXObjectHotReloadCoordinator& FXObjectHotReloadCoordinator::Get() noexcept
{
    static FXObjectHotReloadCoordinator s_instance;
    return s_instance;
}

FXObjectHotReloadCoordinator::FXObjectHotReloadCoordinator() noexcept
    : m_lastEnum()
    , m_cascadeStartUs(0)
    , m_replacedClassCount(0)
    , m_totalInstancesRebound(0)
{
}

FXObjectHotReloadCoordinator::~FXObjectHotReloadCoordinator() noexcept = default;

// =====================================================================
// __ResetForTests -- destructive reset.
// =====================================================================
void FXObjectHotReloadCoordinator::__ResetForTests() noexcept
{
    m_lastEnum = FHotReloadThreadEnumeration{};
    m_cascadeStartUs.store(0, ::std::memory_order_release);
    m_replacedClassCount.store(0, ::std::memory_order_release);
    m_totalInstancesRebound.store(0, ::std::memory_order_release);

    // Force the global flags to their default (post-cascade) state so
    // a test that aborted mid-cascade leaves the next test in a clean
    // state.
    g_XHotReloadInProgress.store(false, ::std::memory_order_release);
    g_XGCAcceptDrains.store(true,       ::std::memory_order_release);
}

// =====================================================================
// IsQuiesceActive -- diagnostic accessor.
// =====================================================================
bool FXObjectHotReloadCoordinator::IsQuiesceActive() const noexcept
{
    return g_XHotReloadInProgress.load(::std::memory_order_acquire);
}

// =====================================================================
// GetReplacedClassCount / GetTotalInstancesRebound -- diagnostics.
// =====================================================================
::std::int64_t FXObjectHotReloadCoordinator::GetReplacedClassCount() const noexcept
{
    return m_replacedClassCount.load(::std::memory_order_acquire);
}

::std::int64_t FXObjectHotReloadCoordinator::GetTotalInstancesRebound() const noexcept
{
    return m_totalInstancesRebound.load(::std::memory_order_acquire);
}

// =====================================================================
// BeginHotReloadQuiesce -- open the cascade window.
// =====================================================================
void FXObjectHotReloadCoordinator::BeginHotReloadQuiesce(
    const FHotReloadThreadEnumeration& Enumeration) noexcept
{
    // Defensive: double-Begin is a programming error in the XLiveCoding
    // orchestrator. In Dev/Debug we'd XPACT_CHECK; the production
    // posture is to short-circuit (clobbering the active cascade's
    // bracket state would be worse than no-op).
    if (g_XHotReloadInProgress.load(::std::memory_order_acquire))
    {
        return;
    }

    const ::std::int64_t QuiesceStartUs = NowUs();
    m_cascadeStartUs.store(QuiesceStartUs, ::std::memory_order_release);

    // Reset per-cascade running totals.
    m_replacedClassCount.store(0,    ::std::memory_order_release);
    m_totalInstancesRebound.store(0, ::std::memory_order_release);

    // Cache the enumeration so FinishHotReloadCascade can invoke the
    // UnparkHooks against the same table.
    m_lastEnum = Enumeration;

    // -----------------------------------------------------------------
    // Step 1: Invoke ParkHooks for every active role in the enumeration.
    //
    // XCoreXObject does NOT define the hook bodies themselves;
    // XLiveCoding owns them + supplies them via the enumeration table.
    // -----------------------------------------------------------------
    ::std::int64_t QuiescedThreadCount = 0;
    for (::std::size_t I = 0;
         I < static_cast<::std::size_t>(EHotReloadThreadRole::kCount);
         ++I)
    {
        if (Enumeration.Hooks[I].ParkHook != nullptr)
        {
            Enumeration.Hooks[I].ParkHook();
            ++QuiescedThreadCount;
        }
    }

    // -----------------------------------------------------------------
    // Step 2: Wait for any in-progress GC cycle to complete.
    //
    // The collector's marker thread observes IsMarking() == true while
    // a cycle is mid-mark. Spin-yield until the phase returns to
    // not-marking. The collector's RunCycle is the only mutator of
    // m_phase; once we observe IsMarking() == false (with acquire
    // ordering via the collector's atomic phase load), no concurrent
    // cycle is running.
    // -----------------------------------------------------------------
    const ::std::int64_t GcWaitStartUs = NowUs();
    FXObjectCollector& Coll = FXObjectCollector::Get();
    // Spin briefly first, then yield. The collector's mark cycle is
    // typically <50 ms; the spin path covers the common case where no
    // cycle is in progress at all (IsMarking returns false on the first
    // read).
    while (Coll.IsMarking())
    {
        ::XCore::HAL::FPlatformProcess::Sleep(0.0f);
    }
    const ::std::int64_t GcWaitUs = NowUs() - GcWaitStartUs;

    // -----------------------------------------------------------------
    // Step 3: Block new GC cycles from starting + quiesce the
    // FXObjectArray AllocLock by setting the global flag.
    //
    // release-store pairs with the acquire-load in
    // FXObjectCollector::Trigger / FXObjectArray::ReserveSlot /
    // ReleaseSlot. Mutator threads that observe the set flag will
    // spin-wait via WaitWhileHotReloadInProgress.
    // -----------------------------------------------------------------
    g_XHotReloadInProgress.store(true, ::std::memory_order_release);

    // -----------------------------------------------------------------
    // Step 4: Pause the SATB log drain by clearing g_XGCAcceptDrains.
    //
    // Per-thread SATB queues continue to receive entries (the write
    // barrier doesn't fail). The GLOBAL log drain (the path that
    // empties per-thread queues into the global log) stops. Per-thread
    // queues that fill up during quiesce will spin-wait via
    // XGCWaitForDrainsAccepted until FinishHotReloadCascade clears
    // this flag.
    //
    // (Per FIX-A-MED-37: the per-thread queue bound is 256 entries;
    // 2 KB per thread; 16 KB on 8 threads. The cascade is engineer-
    // station-only and bounded by §11 acceptance criterion (e) at
    // <120 s nominal; the queue cannot overflow in practice.)
    // -----------------------------------------------------------------
    g_XGCAcceptDrains.store(false, ::std::memory_order_release);

    // -----------------------------------------------------------------
    // Step 5: Fire OnHotReloadStart delegate.
    // -----------------------------------------------------------------
    ::XCore::CoreDelegates::FHotReloadContext StartCtx;
    StartCtx.ReplacedClassCount    = 0;
    StartCtx.TotalInstancesRebound = 0;
    ::XCore::CoreDelegates::GetOnHotReloadStart().Broadcast(StartCtx);

    // -----------------------------------------------------------------
    // Step 6: Emit HotReload.QuiesceWaited telemetry.
    // -----------------------------------------------------------------
    const ::std::int64_t TotalQuiesceUs = NowUs() - QuiesceStartUs;
    (void)TotalQuiesceUs;  // captured for the emit; not stored.

    // The QuiesceWaited emit helper takes (quiescedThreadCount,
    // waitDurationMs). Convert microseconds to milliseconds at the
    // emit site (the helper signature uses ms per the §10.12 schema).
    const double WaitDurationMs =
        static_cast<double>(GcWaitUs) / 1000.0;

    ::XCore::HAL::XInsightsEmitHelpers::EmitHotReloadQuiesceWaited(
        QuiescedThreadCount,
        WaitDurationMs);
}

// =====================================================================
// ApplyClassReplacement -- rebind one FClass.
// =====================================================================
::XCore::Result<void, FHotReloadError>
FXObjectHotReloadCoordinator::ApplyClassReplacement(
    const ::XCore::Reflect::FClass* OldClass,
    const ::XCore::Reflect::FClass* NewClass) noexcept
{
    // -----------------------------------------------------------------
    // Pre-condition checks.
    // -----------------------------------------------------------------
    if (OldClass == nullptr)
    {
        return ::XCore::Unexpected(FHotReloadError::kNullOldClass);
    }
    if (NewClass == nullptr)
    {
        return ::XCore::Unexpected(FHotReloadError::kNullNewClass);
    }
    if (OldClass == NewClass)
    {
        return ::XCore::Unexpected(FHotReloadError::kIdentityReplacement);
    }
    if (!g_XHotReloadInProgress.load(::std::memory_order_acquire))
    {
        return ::XCore::Unexpected(FHotReloadError::kQuiesceNotActive);
    }

    // -----------------------------------------------------------------
    // Step 1: Walk FXObjectArray for instances of OldClass; atomically
    // rebind ClassPrivate.
    //
    // PRIME-DIRECTIVE NOTE on atomicity. XObject::ClassPrivate is a
    // `const FClass*` (non-atomic storage). The spec §9.2 says
    // "rebinds ClassPrivate = NewClass (atomic store)". The
    // structurally-correct approach (preserving byte-identity ABI) is
    // std::atomic_ref<const FClass*>: overlays the existing storage,
    // provides atomic store semantics, does NOT change layout.
    //
    // Per the XCore codebase convention (FPlatformAtomics.cpp uses
    // std::atomic_ref for the int32/int64 atomic ops on non-atomic
    // storage), this is the established pattern.
    //
    // The mark phase reads ClassPrivate via the plain non-atomic
    // pointer read; the value observed during the cascade is either
    // OldClass or NewClass (atomic_ref's store is atomic; a torn read
    // is structurally impossible on x64 / ARM64 for 8-byte aligned
    // pointers). The quiesce protocol ensures NO concurrent GC mark is
    // active during ApplyClassReplacement (we waited for IsMarking()
    // == false in BeginHotReloadQuiesce + the in-progress flag blocks
    // new cycles).
    //
    // The walk uses FXObjectArray::ForEachObject which acquires the
    // shared lock for the iteration; we cannot acquire the exclusive
    // lock from inside the visitor (lock recursion would deadlock the
    // FRWLock). The shared lock is sufficient because:
    //   (a) ClassPrivate writes go via atomic_ref (lock-free).
    //   (b) The structural FXObjectArray storage (Entries + capacity)
    //       is not mutated (no FreeEntry / AllocateEntry during the
    //       quiesce window per spec §9.2).
    //   (c) Concurrent readers (collector mark, weak-ptr deref) are
    //       blocked by the quiesce flag or the marker is quiesced.
    // -----------------------------------------------------------------
    ::std::int64_t InstanceCount = 0;

    FXObjectArray& Arr = FXObjectArray::Get();
    Arr.ForEachObject(
        [OldClass, NewClass, &InstanceCount](::int32 /*Index*/, ::XCore::XObject* Object) noexcept
        {
            if (Object == nullptr) return;
            // Read the current ClassPrivate. Plain read is safe within
            // the quiesce window (no concurrent writer except us).
            if (Object->ClassPrivate == OldClass)
            {
                // Atomic store via atomic_ref over the existing storage.
                // The pointer storage is 8 bytes on 64-bit platforms;
                // std::atomic_ref<const FClass*>::is_always_lock_free
                // holds on every supported platform.
                ::std::atomic_ref<const ::XCore::Reflect::FClass*> ClassRef(
                    Object->ClassPrivate);
                ClassRef.store(NewClass, ::std::memory_order_release);

                // Set the diagnostic HotReloadReplaced flag.
                Object->SetFlags(::XCore::EObjectFlags::HotReloadReplaced);

                ++InstanceCount;
            }
        });

    // -----------------------------------------------------------------
    // Step 2: Rebind the FXObjectAllocator's per-FClass sub-pool
    // (FIX-A-MIN-40; §3.6).
    //
    // Cells do NOT move; only the per-cell OwnerClass tag flips +
    // free-list ownership migrates from OldClass's pool to NewClass's
    // pool.
    // -----------------------------------------------------------------
    FXObjectAllocator::Get().RebindClassPool(OldClass, NewClass);

    // -----------------------------------------------------------------
    // Step 3: Fire OnClassReplaced delegate (Phase 5.k surface).
    //
    // The Phase 5.k signature is Broadcast(OldClass, NewClass) -- we
    // call it directly (the existing delegate is `(const FClass*,
    // const FClass*)`).
    // -----------------------------------------------------------------
    ::XCore::CoreDelegates::GetOnClassReplaced().Broadcast(OldClass, NewClass);

    // -----------------------------------------------------------------
    // Step 4: Emit HotReload.ClassReplaced telemetry.
    //
    // Payload: {className, oldVersion, newVersion, instanceCount}.
    // Phase 5.j ships the class-name + instance-count fields; version
    // information is XLiveCoding-owned metadata (the patch versioning
    // is XLiveCoding's responsibility per spec §9), so we pass 0/0 for
    // oldVersion / newVersion until XLiveCoding's version-tracking
    // ships.
    // -----------------------------------------------------------------
    const ::XCore::Reflect::FName ClassName =
        (OldClass != nullptr) ? OldClass->GetFName() : ::XCore::Reflect::FName{};

    ::XCore::HAL::XInsightsEmitHelpers::EmitHotReloadClassReplaced(
        ClassName,
        /*OldVersion=*/ 0,
        /*NewVersion=*/ 0,
        InstanceCount);

    // -----------------------------------------------------------------
    // Step 5: Update per-cascade running totals.
    // -----------------------------------------------------------------
    m_replacedClassCount.fetch_add(1, ::std::memory_order_acq_rel);
    m_totalInstancesRebound.fetch_add(InstanceCount, ::std::memory_order_acq_rel);

    return {};
}

// =====================================================================
// FinishHotReloadCascade -- close the cascade window.
// =====================================================================
void FXObjectHotReloadCoordinator::FinishHotReloadCascade(
    const FClassReplacementMap& Map) noexcept
{
    // Defensive: Finish without an open quiesce is a programming error
    // in the XLiveCoding orchestrator. Short-circuit rather than
    // mutate the flag state.
    if (!g_XHotReloadInProgress.load(::std::memory_order_acquire))
    {
        return;
    }

    // -----------------------------------------------------------------
    // Step 1: Resume SATB log drain.
    //
    // Writers spin-waiting on XGCWaitForDrainsAccepted observe the
    // release-store and proceed.
    // -----------------------------------------------------------------
    g_XGCAcceptDrains.store(true, ::std::memory_order_release);

    // -----------------------------------------------------------------
    // Step 2: Unblock new GC cycles + release the FXObjectArray
    // AllocLock by clearing the global flag.
    //
    // FXObjectArray::ReserveSlot / ReleaseSlot waiters observe the
    // release-store and proceed. FXObjectCollector::Trigger no longer
    // refuses to start cycles.
    // -----------------------------------------------------------------
    g_XHotReloadInProgress.store(false, ::std::memory_order_release);

    // -----------------------------------------------------------------
    // Step 3: Invoke UnparkHooks from the cached enumeration.
    //
    // The order is the same as ParkHook iteration (game thread first,
    // asset workers last). XLiveCoding owns the hook bodies; we just
    // invoke.
    // -----------------------------------------------------------------
    for (::std::size_t I = 0;
         I < static_cast<::std::size_t>(EHotReloadThreadRole::kCount);
         ++I)
    {
        if (m_lastEnum.Hooks[I].UnparkHook != nullptr)
        {
            m_lastEnum.Hooks[I].UnparkHook();
        }
    }

    // -----------------------------------------------------------------
    // Step 4: Trigger a deferred GC cycle.
    //
    // Per spec §9.2 step 5 trailing bullet: "Triggers a deferred GC
    // cycle to reclaim any objects that are now unreachable post-
    // cascade (e.g., temporary objects from the old module that the
    // new module doesn't reference)."
    //
    // We use kManual reason (the post-hot-reload trigger is closest to
    // an explicit request; the spec §4.7 trigger heuristic enum doesn't
    // have a dedicated kPostHotReload variant -- the prompt mentioned
    // it but the existing EXGCTriggerReason enum doesn't include it,
    // so we use kManual as the closest semantic match). Future phases
    // may add a dedicated kPostHotReload reason to the enum if the
    // telemetry consumer panel wants to pivot on it.
    // -----------------------------------------------------------------
    FXObjectCollector::Get().Trigger(EXGCTriggerReason::kManual);

    // -----------------------------------------------------------------
    // Step 5: Read per-cascade totals for the OnHotReloadComplete +
    // CascadeApplied payloads.
    // -----------------------------------------------------------------
    const ::std::int64_t ReplacedCount =
        m_replacedClassCount.load(::std::memory_order_acquire);
    const ::std::int64_t InstancesRebound =
        m_totalInstancesRebound.load(::std::memory_order_acquire);

    // -----------------------------------------------------------------
    // Step 6: Fire OnHotReloadComplete delegate.
    // -----------------------------------------------------------------
    ::XCore::CoreDelegates::FHotReloadContext CompleteCtx;
    CompleteCtx.ReplacedClassCount    = ReplacedCount;
    CompleteCtx.TotalInstancesRebound = InstancesRebound;
    ::XCore::CoreDelegates::GetOnHotReloadComplete().Broadcast(CompleteCtx);

    // -----------------------------------------------------------------
    // Step 7: Emit HotReload.CascadeApplied telemetry.
    //
    // Payload: {classesReplaced, propertiesAdded, modules, durationMs}.
    // propertiesAdded is forward-commitment (Phase 1 gate ensures
    // layout drift is rejected; we pass 0 to satisfy the schema).
    // modules is forward-commitment (XLiveCoding-owned metadata;
    // NAME_None until XLiveCoding's module-tracking surface lands).
    // -----------------------------------------------------------------
    (void)Map;  // The map is the XLiveCoding-side ground truth; we
                // already have our local running totals.

    const ::std::int64_t CascadeStartUs =
        m_cascadeStartUs.load(::std::memory_order_acquire);
    const double DurationMs =
        static_cast<double>(NowUs() - CascadeStartUs) / 1000.0;

    ::XCore::HAL::XInsightsEmitHelpers::EmitHotReloadCascadeApplied(
        ReplacedCount,
        /*PropertiesAdded=*/ 0,
        /*Modules=*/ ::XCore::Reflect::FName{},
        DurationMs);

    // -----------------------------------------------------------------
    // Step 8: Reset per-cascade state.
    //
    // m_lastEnum is cleared so a subsequent FinishHotReloadCascade
    // without a fresh Begin does NOT invoke stale hooks.
    // -----------------------------------------------------------------
    m_lastEnum = FHotReloadThreadEnumeration{};
    m_cascadeStartUs.store(0, ::std::memory_order_release);
}

} // namespace XCore
