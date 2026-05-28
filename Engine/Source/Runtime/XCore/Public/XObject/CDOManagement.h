// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// CDOManagement.h -- Class Default Object lifecycle (XCoreXObject Rev 4
// §8 + §8.1 + §8.1.1 + Rev 3 FIX-H-R2-6).
// =====================================================================
//
// XCoreXObject Rev 4 Section 8 ("CDO Pattern"). The CDO (Class Default
// Object) is the per-class template instance whose property values are
// the defaults for any NewObject of that class. Per spec §8.1, XPact
// uses a PER-CLASS policy:
//
//   * Eager CDO  -- constructed at PostStaticInit boundary (after the
//                    XObject heap + FXObjectArray are ready). Used for
//                    classes whose CDO is consumed by FClass::Link
//                    itself or by static-init-tier subsystems. Opt-in
//                    via EClassFlags::CLASS_EagerCDO annotation; this
//                    is the minority.
//
//   * Lazy CDO   -- default policy; no annotation. The CDO is
//                    constructed on the first GetClassDefaultObject
//                    call OR on the first NewObject<T> call for that
//                    class. Construction happens under a per-class
//                    atomic CAS on FClass::ClassDefaultObject to
//                    ensure thread-safety.
//
// IMMUTABLE-CDO DISCIPLINE (per spec §8.1.1 + Rev 3 FIX-M-R2-30 +
// FIX-A-MED-30): the CDO is immutable post-construction. The
// FClass::ClassDefaultObject field is the storage; XPact's type-
// system enforces immutability via the `const XObject*` accessor +
// the compile-time `const FClass` declaration. UE's experimental
// `UE_WITH_IMMUTABLE_CDO` path is XPact's default.
//
// EAGER CDO POSTSTATICINIT ORDERING (Rev 3 per FIX-H-R2-6):
//
//   The Rev 2 design had a subtle bootstrap ordering bug: constinit
//   const FClass instances are created at static-init time
//   (pre-EInitPhase ladder), but the NewObject entry path asserts
//   XPACT_CHECK(EngineInitPhase() >= EInitPhase::PostStaticInit). If
//   an EagerCDO class's `Z_Construct_FClass_*` (static-init phase)
//   tried to construct the CDO via NewObject<CDO>, the assert would
//   fire + the engine would abort before main().
//
//   Rev 3 resolves this with:
//     1. Static-init: Z_Construct_FClass_* registers the FClass with
//        XReflectionRuntime AND calls XCoreXObject's LinkClass. For
//        EagerCDO classes, LinkClass enqueues the CDO-construction
//        request onto a deferred list (g_PendingEagerCDOs) instead of
//        calling NewObject immediately.
//     2. PostStaticInit: XCoreXObject's __Init walks
//        g_PendingEagerCDOs and constructs each queued CDO via the
//        standard NewObject path (now safe).
//
// PHASE 5.d SCOPE: ships the queue + the drain function + the lazy
// constructor + the eager-flag detection. The XHT-emit side that
// SETS EagerCDO on user-class FClasses is XHT-emit work (Phase 5.f+);
// the EnqueueEagerCDO entry point ships at Phase 5.d so the queue is
// ready for that emit. The DrainPendingEagerCDOs is called from
// XCoreXObject::__Init (engine bring-up); Phase 5.d ships the
// function body + the user-callable XPACT_DRAIN_EAGER_CDOS macro
// (which the engine bring-up TU calls at the PostStaticInit boundary).
//
// HOT-RELOAD: NO virtual methods on this surface. The CDO is replaced
// during hot-reload via an atomic pointer swap (XLiveCoding's cascade
// per spec §9.3); the old CDO is retired to the deferred-destruction
// queue; the new CDO is published atomically.
//
// SIM-PATH: CDO construction goes through NewObject<T> which carries
// the sim-path runtime invariant guards. CDO construction MAY happen
// off the sim-path serial executor (the CDO is non-sim-path state),
// so the guard short-circuits for non-sim-path TUs.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Reflection/EClassFlags.h"
#include "Reflection/FName.h"             // ComposeCDOName returns FName by value
#include "XObject/EObjectFlags.h"

#include <cstdint>

// Forward declarations.
namespace XCore { class XObject; }
namespace XCore::Reflect { struct FClass; }

namespace XCore
{

    // -----------------------------------------------------------------
    // GetClassDefaultObject -- lazy CDO accessor (per spec §8.1 +
    // §8.1.1 + §8.2).
    //
    // Returns the CDO for the given FClass, constructing it on first
    // call. The construction is guarded by an atomic CAS on
    // FClass::ClassDefaultObject so concurrent first-callers are
    // serialised: exactly one CDO is constructed; concurrent winners
    // observe the freshly-published CDO; losers discard their attempt.
    //
    // ALGORITHM (per spec §8.2 reference body):
    //   1. Acquire-load FClass::ClassDefaultObject.
    //   2. If non-null: return it (the common path; one atomic load).
    //   3. Otherwise: NewObject<XObject>(nullptr, ComposeCDOName(Class),
    //      EObjectFlags::ClassDefaultObject | ArchetypeObject |
    //      Public | MarkAsRootSet).
    //   4. compare_exchange the slot from nullptr to our CDO pointer.
    //   5. If we win: return our CDO.
    //   6. If we lose: discard our CDO (mark BeginDestroyed + free
    //      via FXObjectArray::FreeEntry), return the winner's CDO.
    //
    // The CDO is pinned in the GC root set (RF_MarkAsRootSet flag) so
    // it survives every collection cycle. The Standalone flag (a
    // synonym for MarkAsRootSet per the EObjectFlags spec) is also
    // set so the lifetime semantics are explicit.
    //
    // Returns nullptr if Class is nullptr (defensive; production code
    // should never pass nullptr here).
    //
    // SIM-PATH: this function MAY allocate via NewObject. The sim-path
    // guard is in NewObject's body; CDO construction is non-sim-path
    // by convention (CDOs are created at engine init / first-use, not
    // during sim-tick execution).
    //
    // CONST-RETURN (per spec §8.1.1 + Rev 3 FIX-M-R2-30): the returned
    // pointer is `const XObject*` -- the type system enforces the
    // immutable-CDO discipline. Callers that need a non-const view
    // MUST use const_cast at the call site (and document the
    // motivation; the editor's "Reset to Default" affordance is one
    // of the few legitimate consumers).
    // -----------------------------------------------------------------
    [[nodiscard]] const XObject* GetClassDefaultObject(
        const ::XCore::Reflect::FClass* Class) noexcept;

    // -----------------------------------------------------------------
    // ComposeCDOName -- the "Default__<ClassName>" name composition
    // (per spec §8.2 reference body).
    //
    // The CDO's FName is "Default__" prepended to the FClass's Name.
    // For "XActor" the CDO name is "Default__XActor". The convention
    // mirrors UE (UObjectGlobals.cpp's CDO naming).
    //
    // The result is an FName interned via the FNamePool; first-call
    // for a class allocates a new pool entry, subsequent calls return
    // the existing handle.
    //
    // Returns NAME_None if Class is nullptr.
    // -----------------------------------------------------------------
    [[nodiscard]] ::XCore::Reflect::FName ComposeCDOName(
        const ::XCore::Reflect::FClass* Class) noexcept;

    // -----------------------------------------------------------------
    // EnqueueEagerCDO -- queue an EagerCDO class for PostStaticInit
    // construction (Rev 3 per FIX-H-R2-6).
    //
    // Called from XCoreXObject's LinkClass path for FClasses that
    // carry EClassFlags::CLASS_EagerCDO. The class is appended to a
    // global TArray<const FClass*> g_PendingEagerCDOs.
    //
    // DrainPendingEagerCDOs (below) walks this list at the
    // PostStaticInit boundary and constructs each queued CDO via the
    // standard NewObject path.
    //
    // Idempotent: a second EnqueueEagerCDO for the same Class is a
    // no-op (the queue tracks per-class enrolment via the FClass*
    // identity).
    //
    // THREAD-SAFE: the queue mutation is guarded by a process-global
    // FCriticalSection. Calls from concurrent module-load paths
    // (typically rare; module loading is generally serialised by
    // XLiveCoding) are correctly serialised.
    //
    // No-op if Class is nullptr or if Class does NOT have the
    // EagerCDO flag (defensive; the caller should not enqueue non-
    // EagerCDO classes).
    // -----------------------------------------------------------------
    void EnqueueEagerCDO(const ::XCore::Reflect::FClass* Class) noexcept;

    // -----------------------------------------------------------------
    // DrainPendingEagerCDOs -- walk the queue + construct every CDO
    // (Rev 3 per FIX-H-R2-6).
    //
    // Called once at the PostStaticInit boundary (typically from
    // XCoreXObject's __Init or an equivalent engine bring-up TU).
    // Walks g_PendingEagerCDOs in registration order; for each entry
    // calls GetClassDefaultObject to materialise the CDO.
    //
    // POST-CONDITION: g_PendingEagerCDOs is empty after the drain.
    // Subsequent EnqueueEagerCDO calls (e.g., from a hot-reloaded
    // module) populate the queue again; the next __Init pass (or an
    // explicit drain call) materialises them.
    //
    // The drain order matches the registration order (FIFO; first-
    // registered first-constructed). The order IS deterministic per
    // Master Plan's TU-init policy; cross-module ordering is
    // guaranteed by the static-init order of the producing TUs.
    //
    // RETURNS the count of CDOs that were materialised in this drain.
    // The return is diagnostic: an X-INIT-EAGER acceptance gate test
    // checks the count matches the EagerCDO-flagged FClass count.
    //
    // THREAD-SAFE: the drain holds the same FCriticalSection as
    // EnqueueEagerCDO; the queue is consumed under the lock. The CDO
    // construction itself happens OUTSIDE the lock (per the engine-
    // wide lock-discipline contract -- no NewObject calls under a
    // mutex).
    // -----------------------------------------------------------------
    ::int32 DrainPendingEagerCDOs() noexcept;

    // -----------------------------------------------------------------
    // GetPendingEagerCDOCount -- diagnostic accessor.
    //
    // Returns the current count of FClasses in the g_PendingEagerCDOs
    // queue. Diagnostic / test API; the X-INIT-EAGER acceptance gate
    // uses this to verify the queue is fully drained after __Init.
    // -----------------------------------------------------------------
    [[nodiscard]] ::int32 GetPendingEagerCDOCount() noexcept;

    // -----------------------------------------------------------------
    // IsEagerCDO -- predicate against EClassFlags::CLASS_EagerCDO.
    //
    // Returns true iff Class has the EagerCDO flag set. The flag is
    // populated by XHT-emit (Phase 5.f+) for user-classes annotated
    // with XCLASS(EagerCDO); programmatic FClass construction may
    // also set the flag directly via the ClassFlags field.
    //
    // Returns false if Class is nullptr.
    // -----------------------------------------------------------------
    [[nodiscard]] bool IsEagerCDO(const ::XCore::Reflect::FClass* Class) noexcept;

    // -----------------------------------------------------------------
    // __ResetCDOsForTests -- test-only reset of the eager-CDO queue
    // and per-FClass CDO slot pointers.
    //
    // Drops every queued FClass from g_PendingEagerCDOs + nulls every
    // FClass::ClassDefaultObject slot for classes the test harness
    // has registered. ONLY used by the Phase 5.d test suite to ensure
    // each test starts from a clean state.
    //
    // The reset does NOT destroy the CDO XObjects themselves (their
    // raw memory in FXObjectAllocator is the harness's concern); it
    // only clears the bookkeeping that GetClassDefaultObject /
    // EnqueueEagerCDO consult.
    //
    // Production callers MUST NOT call this method. The name + the
    // function-name comment document the constraint.
    // -----------------------------------------------------------------
    void __ResetCDOsForTests() noexcept;

} // namespace XCore
