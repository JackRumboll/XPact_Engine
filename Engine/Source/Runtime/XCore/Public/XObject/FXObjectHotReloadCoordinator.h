// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FXObjectHotReloadCoordinator.h -- the process-singleton hot-reload
// cascade coordinator (XCoreXObject Rev 4 §9 + §11.5; Phase 5.j).
// =====================================================================
//
// XCoreXObject Rev 4 §9 ("Hot-reload class replacement") + §1.4
// (cross-system commitment: XLiveCoding API forward-commitment) +
// §1.6 (cross-system integration L0) + §3.6 trailing prose (per-FClass
// sub-pool rebind per FIX-A-MIN-40) + §5.6 (SATB queue management
// during quiesce) + §6.4 (XObjectKey rebind protocol) + §10.10.1
// (cache invalidation on class replacement) + §11.5 (Phase 5.j scope).
//
// PURPOSE: XCoreXObject's side of the XLiveCoding-orchestrated
// hot-reload class-replacement cascade. The coordinator owns the
// process-singleton state that brackets the cascade:
//
//   * Quiesce window: GC paused, SATB drain paused, FXObjectArray
//                      AllocLock quiesced, OnHotReloadStart fired.
//
//   * Per-class apply: ClassPrivate atomic store on every instance of
//                      OldClass, FXObjectAllocator sub-pool rebind,
//                      OnClassReplaced fired per class, telemetry
//                      emitted.
//
//   * Finish: SATB drain resumed, GC unblocked, AllocLock released,
//             OnHotReloadComplete fired, deferred GC triggered.
//
// =====================================================================
//
// XLIVECODING CONTRACT (Spec §1.4 + §9.2):
//
// XLiveCoding (forthcoming; not yet shipped) MUST call these three
// entry points in this order:
//
//   1. BeginHotReloadQuiesce(const FHotReloadThreadEnumeration&)
//   2. ApplyClassReplacement(OldClass, NewClass)  -- once per replaced
//      FClass; the map is built by XLiveCoding's cascade driver.
//   3. FinishHotReloadCascade(const FClassReplacementMap&)
//
// XLiveCoding's Phase 1 pre-commit gate (per FIX-A-HIGH-14) checks
// layout drift BEFORE calling BeginHotReloadQuiesce; XCoreXObject
// assumes any patch reaching FinishHotReloadCascade has cleared
// upstream gates.
//
// Single-method hot-patches (§9.4) do NOT route through this
// coordinator; they go through XLiveCoding's atomic-pointer-store
// path directly against FClass::LifecycleTable slots, bypassing the
// quiesce window. This coordinator is for the FULL CASCADE path
// (multi-class replacement).
//
// =====================================================================
//
// CONCURRENCY:
//
// The coordinator is itself NOT lock-protected (the singleton's state
// is changed only by the XLiveCoding orchestrator thread). Bracket
// methods acquire the FXObjectArray lock and the FXObjectAllocator
// lock as needed; the bracket flag g_XHotReloadInProgress is the
// SHARED state that mutator threads observe.
//
// The expectation is that XLiveCoding's orchestrator thread is the
// sole caller of BeginHotReloadQuiesce / ApplyClassReplacement /
// FinishHotReloadCascade. Concurrent cascades are a programming error
// and would corrupt the bracket state; we do NOT defend against this
// in Phase 5.j (the orchestrator is presumed single-threaded).
//
// =====================================================================
//
// HOT-RELOAD: NO virtual methods. The singleton is a function-local
// static.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "Macros/XErrorTypes.h"
#include "Macros/XResult.h"

#include <atomic>
#include <cstddef>
#include <cstdint>

// Forward declarations.
namespace XCore { class XObject; }
namespace XCore::Reflect { struct FClass; }

namespace XCore
{

    // -----------------------------------------------------------------
    // EHotReloadThreadRole -- the 10 core thread roles XCoreXObject
    // pre-registers with XLiveCoding's quiesce framework (per spec
    // §9.2 + FIX-A-HIGH-14).
    //
    // Each role identifies a thread (or thread group) that must be
    // parked during the cascade. The matching ParkHook + UnparkHook
    // pair fires at quiesce open / close.
    //
    // Future plugins register additional roles via XLiveCoding's
    // RegisterThreadRole API (per Rev 3 FIX-M-R2-9 extension-hook
    // discipline). The 10 core roles are the SPEC-COMPLETE foundation
    // set; plugin extensions are forward-only.
    //
    // The enum is uint8 so the FHotReloadThreadEnumeration's per-role
    // slot is index-addressable in O(1) by role number.
    // -----------------------------------------------------------------
    enum class EHotReloadThreadRole : ::std::uint8_t
    {
        // Game thread -- parked at frame boundary.
        kGameThread          = 0,

        // Render thread -- parked after pending RenderCommand flush.
        kRenderThread        = 1,

        // RHI thread -- parked after pending GPU fence wait.
        kRHIThread           = 2,

        // Audio thread -- parked after audio mixer flush.
        kAudioThread         = 3,

        // Audio mixer thread -- parked after sample-frame flush.
        kAudioMixerThread    = 4,

        // Loading thread (async asset I/O workers) -- parked at next
        // sleep point.
        kLoadingThread       = 5,

        // Worker pool threads -- each worker drains its queue then
        // parks.
        kWorkerPoolThread    = 6,

        // GC marker thread -- completes in-progress cycle then parks.
        kGCMarkerThread      = 7,

        // PSO compile threads -- parked at next sleep point.
        kPSOCompileThread    = 8,

        // Asset worker threads (texture-streaming, mesh-streaming) --
        // parked at next sleep point.
        kAssetWorkerThread   = 9,

        // Count sentinel; used to size the enumeration table.
        kCount               = 10,
    };

    // -----------------------------------------------------------------
    // FHotReloadThreadEnumeration -- the explicit thread-role-to-
    // hook-pair table XLiveCoding passes to BeginHotReloadQuiesce
    // (per Rev 2 FIX-A-HIGH-14).
    //
    // Each entry is a {ParkHook, UnparkHook} pair. ParkHook is invoked
    // during quiesce open; UnparkHook is invoked during cascade
    // finish. nullptr ParkHook means "this role is not active in the
    // current cascade" (skipped at quiesce open).
    //
    // The 10 core roles are pre-allocated as fixed array slots indexed
    // by EHotReloadThreadRole. Plugin-registered extension roles are
    // handled by XLiveCoding's RegisterThreadRole registry (not stored
    // in this enumeration struct; XLiveCoding walks the registry
    // before/after this struct).
    //
    // The struct is trivially-copyable (function-pointer slots only)
    // so XLiveCoding can pass by value at the call site.
    //
    // SHAPE NOTE: per Rev 2 FIX-A-HIGH-14 "explicit enumeration", the
    // 10 core roles are the SPEC-COMPLETE foundation set. The struct
    // is the canonical surface for the foundation set; extensions
    // ride through XLiveCoding's separate registry per FIX-M-R2-9.
    // -----------------------------------------------------------------
    struct FHotReloadThreadEnumeration
    {
        // The 10 fixed slots, indexed by EHotReloadThreadRole. Each
        // {Park, Unpark} pair may be nullptr (the role is inactive for
        // this cascade).
        struct FHookPair
        {
            // Invoked at BeginHotReloadQuiesce. The hook MUST drain
            // its system's work queue, set the system to a quiescent
            // state, and signal completion (return) before BeginHot
            // ReloadQuiesce continues.
            void (*ParkHook)()    = nullptr;

            // Invoked at FinishHotReloadCascade. The hook MUST restore
            // normal operation (resume the parked thread group).
            void (*UnparkHook)()  = nullptr;
        };

        FHookPair Hooks[static_cast<::std::size_t>(EHotReloadThreadRole::kCount)];

        // Default ctor: all hooks nullptr (no roles active).
        FHotReloadThreadEnumeration() noexcept = default;
    };

    static_assert(sizeof(FHotReloadThreadEnumeration::FHookPair) == 16,
                  "FHookPair layout: two function pointers = 16 bytes "
                  "on 64-bit platforms.");
    static_assert(sizeof(FHotReloadThreadEnumeration) == 160,
                  "FHotReloadThreadEnumeration layout: 10 FHookPair "
                  "entries * 16 bytes = 160 bytes.");

    // -----------------------------------------------------------------
    // FClassReplacement -- a single OldClass -> NewClass pair (per
    // spec §9.2: "for each old FClass that the new module replaces,
    // XLiveCoding constructs an FClassReplacement {OldClass, NewClass}
    // entry").
    //
    // Trivially-copyable POD (two pointers). The map is built by
    // XLiveCoding from its module-load diff.
    // -----------------------------------------------------------------
    struct FClassReplacement
    {
        const ::XCore::Reflect::FClass* OldClass = nullptr;
        const ::XCore::Reflect::FClass* NewClass = nullptr;
    };

    static_assert(sizeof(FClassReplacement) == 16,
                  "FClassReplacement layout: two pointers = 16 bytes.");

    // -----------------------------------------------------------------
    // FClassReplacementMap -- the cascade's full replacement set (per
    // spec §9.2 + §1.4).
    //
    // Phase 5.j ships the map as a span-like {pointer + count} pair so
    // it can be passed by const-ref across module boundaries without
    // forcing a TArray dependency on the XLiveCoding side. XLiveCoding
    // owns the backing storage (typically a TArray<FClassReplacement>
    // in its cascade driver); XCoreXObject's FinishHotReloadCascade
    // walks the span by index.
    //
    // The map is logically a TMap<OldClass*, NewClass*>; the linear
    // span representation is the right call here because:
    //   1. Iteration is the only access pattern (FinishHotReloadCascade
    //      walks every entry once).
    //   2. The cascade size is small (typically <1000 classes per
    //      patch even on a large module reload).
    //   3. A span avoids forcing a TMap allocation in XLiveCoding's
    //      driver.
    //
    // The Entries pointer is non-owning (XLiveCoding owns the
    // backing); Count is the entry count.
    // -----------------------------------------------------------------
    struct FClassReplacementMap
    {
        const FClassReplacement* Entries = nullptr;
        ::std::size_t            Count   = 0;
    };

    static_assert(sizeof(FClassReplacementMap) == 16,
                  "FClassReplacementMap layout: pointer + size_t = 16 "
                  "bytes on 64-bit platforms.");

    // -----------------------------------------------------------------
    // FHotReloadError -- failure modes for ApplyClassReplacement.
    //
    // ApplyClassReplacement returns Result<void, FHotReloadError>. The
    // error variants describe WHY the per-class rebind could not
    // complete. The caller (XLiveCoding) decides whether to continue
    // the cascade or abort.
    //
    // Variants are uint8-backed for the engine-wide ABI discipline
    // (matches FScenarioBoundaryError; Result<T, E> packs to 2 bytes).
    // -----------------------------------------------------------------
    enum class FHotReloadError : ::std::uint8_t
    {
        // The OldClass argument was nullptr.
        kNullOldClass = 0,

        // The NewClass argument was nullptr.
        kNullNewClass = 1,

        // OldClass == NewClass: identity rebind is a no-op. Returned
        // as an error so the caller can detect orchestration bugs.
        kIdentityReplacement = 2,

        // The coordinator's quiesce window is NOT open (Begin
        // HotReloadQuiesce was not called before ApplyClassReplacement).
        // Defensive check.
        kQuiesceNotActive = 3,
    };

    static_assert(sizeof(FHotReloadError) == 1,
                  "FHotReloadError ABI lock: uint8 underlying.");

    // -----------------------------------------------------------------
    // FXObjectHotReloadCoordinator -- the process-singleton cascade
    // orchestrator.
    //
    // Accessed via FXObjectHotReloadCoordinator::Get(). The function-
    // local-static initialiser runs at first call (typically from
    // XLiveCoding's cascade driver; pre-XLiveCoding the singleton is
    // never accessed in production).
    //
    // No virtual methods. The class is non-copyable + non-movable.
    // -----------------------------------------------------------------
    class FXObjectHotReloadCoordinator
    {
    public:
        // =============================================================
        // Singleton accessor.
        //
        // Magic-static. C++11 thread-safe initialisation. The
        // returned reference is stable for the process lifetime.
        // =============================================================
        [[nodiscard]] static FXObjectHotReloadCoordinator& Get() noexcept;

        // =============================================================
        // Test-only reset. Drops bracket state (clears in-progress
        // flag, resets running totals). ONLY used by the Phase 5.j
        // test suite.
        // =============================================================
        void __ResetForTests() noexcept;

        // =============================================================
        // BeginHotReloadQuiesce -- open the cascade window (per spec
        // §9.2 step 1; XLiveCoding-callable).
        //
        // STEPS:
        //   1. Verify no concurrent cascade in progress (XPACT_CHECK
        //      against the in-progress flag; double-Begin is a
        //      programming error in the XLiveCoding orchestrator).
        //   2. Invoke each ParkHook in the FHotReloadThreadEnumeration
        //      (XCoreXObject does NOT define the hooks themselves;
        //      XLiveCoding owns the hook bodies + supplies them via
        //      the enumeration table).
        //   3. Wait for any in-progress GC cycle to complete by
        //      polling FXObjectCollector::IsMarking() until false.
        //   4. Block new GC cycles from starting by setting
        //      g_XHotReloadInProgress = true (release-store). The
        //      FXObjectCollector::Trigger consults this flag and
        //      refuses to start a new cycle.
        //   5. Pause the SATB log drain by clearing g_XGCAcceptDrains
        //      = false (release-store). Per-thread SATB queues
        //      continue to receive entries; the global log drain
        //      stops.
        //   6. Quiesce the FXObjectArray AllocLock by setting the
        //      g_XHotReloadInProgress flag (mutator threads observe
        //      via IsHotReloadInProgress(); ReserveSlot / ReleaseSlot
        //      spin-wait on the flag).
        //   7. Fire OnHotReloadStart delegate with an empty
        //      FHotReloadContext.
        //   8. Emit HotReload.QuiesceWaited telemetry with the wait
        //      duration.
        //
        // PRE-CONDITION: no concurrent cascade is active.
        // POST-CONDITION: g_XHotReloadInProgress == true; the cascade
        //                  window is open; subsequent ApplyClass
        //                  Replacement calls are permitted.
        // =============================================================
        void BeginHotReloadQuiesce(
            const FHotReloadThreadEnumeration& Enumeration) noexcept;

        // =============================================================
        // ApplyClassReplacement -- rebind one FClass (per spec §9.2
        // step 4; XLiveCoding-callable; one call per replaced class).
        //
        // STEPS:
        //   1. Pre-condition checks. nullptr / identity-replacement /
        //      quiesce-not-active return Err.
        //   2. Walk the FXObjectArray for instances whose
        //      ClassPrivate == OldClass. For each:
        //      a. Atomically store ClassPrivate = NewClass via
        //         std::atomic_ref. The byte layout of the
        //         XObject::ClassPrivate field is preserved
        //         (atomic_ref overlays the existing storage); only
        //         the access semantics are atomic.
        //      b. Bump the instance count for the per-cascade total.
        //      c. Set EObjectFlags::HotReloadReplaced on the instance
        //         (diagnostic flag per spec §2.3.1).
        //   3. Per FIX-A-MIN-40: rebind the FXObjectAllocator's
        //      per-FClass sub-pool ownership via RebindClassPool.
        //      Cells do NOT move; only the sub-pool ownership
        //      migrates.
        //   4. Fire OnClassReplaced(FClassReplacementContext)
        //      delegate (Phase 5.k shipped this; Phase 5.j calls it).
        //      The context carries (OldClass, NewClass).
        //   5. Emit HotReload.ClassReplaced telemetry with the
        //      instance count.
        //
        // Layout-drift check is NOT performed here -- per spec §9.2
        // "XCoreXObject assumes any patch reaching FinishHotReload
        // Cascade has cleared layout-drift checks upstream"
        // (XLiveCoding's Phase 1 gate per FIX-A-HIGH-14).
        //
        // XObjectKey resolution survives because InternalIndex +
        // SerialNumber are NOT touched (per spec §6.4 XObjectKey
        // rebind protocol).
        //
        // CDO REPLACEMENT (per spec §9.3): callers that wish to
        // replace the CDO MUST call XCoreXObject's CDO-replacement
        // path SEPARATELY; ApplyClassReplacement deliberately does
        // NOT swap FClass::ClassDefaultObject because the CDO swap
        // requires the new CDO to be constructed against NewClass
        // before the swap (a multi-step protocol; Phase 5.d's lazy
        // CDO path is the construction primitive).
        //
        // RETURNS: Result<void, FHotReloadError>. On Ok the void
        // value carries no payload; on Err the variant identifies
        // why the rebind was rejected.
        // =============================================================
        [[nodiscard]] ::XCore::Result<void, FHotReloadError>
            ApplyClassReplacement(
                const ::XCore::Reflect::FClass* OldClass,
                const ::XCore::Reflect::FClass* NewClass) noexcept;

        // =============================================================
        // FinishHotReloadCascade -- close the cascade window (per
        // spec §9.2 step 5; XLiveCoding-callable).
        //
        // STEPS:
        //   1. Resume the SATB log drain by setting g_XGCAcceptDrains
        //      = true (release-store). Per-thread queues whose
        //      writers spin-waited on XGCWaitForDrainsAccepted now
        //      proceed.
        //   2. Unblock new GC cycles by clearing g_XHotReloadIn
        //      Progress = false (release-store). The
        //      FXObjectCollector::Trigger consults this flag; new
        //      triggers post-clear may run.
        //   3. Release the FXObjectArray AllocLock by virtue of the
        //      g_XHotReloadInProgress clear (the flag was the gate;
        //      no explicit lock release needed -- the array's
        //      internal m_lock was never held across the cascade).
        //   4. Invoke each UnparkHook in the cascade's enumeration
        //      (XLiveCoding owns the hook bodies + supplied them at
        //      BeginHotReloadQuiesce; we cached the table in m_lastEnum
        //      for the unpark pass).
        //   5. Trigger a deferred GC cycle via FXObjectCollector::
        //      Trigger(EXGCTriggerReason::kManual). Per spec §9.2
        //      step 5 trailing bullet: "Triggers a deferred GC cycle
        //      to reclaim any objects that are now unreachable post-
        //      cascade".
        //   6. Fire OnHotReloadComplete delegate with the cascade's
        //      final FHotReloadContext (ReplacedClassCount + Total
        //      InstancesRebound).
        //   7. Emit HotReload.CascadeApplied telemetry with the
        //      cascade-wide totals + duration.
        //
        // PRE-CONDITION: BeginHotReloadQuiesce was called and the
        // cascade window is open.
        // POST-CONDITION: g_XHotReloadInProgress == false;
        //                  g_XGCAcceptDrains == true; cascade window
        //                  is closed.
        // =============================================================
        void FinishHotReloadCascade(const FClassReplacementMap& Map) noexcept;

        // =============================================================
        // Diagnostics.
        // =============================================================

        // True iff the cascade window is currently open. Acquire-load
        // on the global flag.
        [[nodiscard]] XPACT_FORCEINLINE bool IsQuiesceActive() const noexcept;

        // Running total of classes replaced in the CURRENT cascade.
        // Resets to 0 at BeginHotReloadQuiesce; bumps on each
        // successful ApplyClassReplacement; read by FinishHotReload
        // Cascade for telemetry payload.
        [[nodiscard]] ::std::int64_t GetReplacedClassCount() const noexcept;

        // Running total of XObject instances whose ClassPrivate was
        // rebound in the CURRENT cascade. Resets to 0 at BeginHot
        // ReloadQuiesce; sums per ApplyClassReplacement.
        [[nodiscard]] ::std::int64_t GetTotalInstancesRebound() const noexcept;

    private:
        FXObjectHotReloadCoordinator() noexcept;
        ~FXObjectHotReloadCoordinator() noexcept;

        FXObjectHotReloadCoordinator(const FXObjectHotReloadCoordinator&)            = delete;
        FXObjectHotReloadCoordinator(FXObjectHotReloadCoordinator&&)                 = delete;
        FXObjectHotReloadCoordinator& operator=(const FXObjectHotReloadCoordinator&) = delete;
        FXObjectHotReloadCoordinator& operator=(FXObjectHotReloadCoordinator&&)      = delete;

        // -------------------------------------------------------------
        // State.
        // -------------------------------------------------------------

        // Cached enumeration from the active cascade's Begin. Used by
        // FinishHotReloadCascade to invoke UnparkHooks. Reset at
        // FinishHotReloadCascade exit.
        FHotReloadThreadEnumeration m_lastEnum;

        // Cascade start time (microseconds since epoch). Used for the
        // CascadeApplied telemetry duration field.
        ::std::atomic<::std::int64_t> m_cascadeStartUs;

        // Running totals for the active cascade.
        ::std::atomic<::std::int64_t> m_replacedClassCount;
        ::std::atomic<::std::int64_t> m_totalInstancesRebound;
    };

} // namespace XCore
