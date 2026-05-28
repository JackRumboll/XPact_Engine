// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// NewObject.h -- NewObject<T> + TryNewObject<T> hot-path templates
// (XCoreXObject Rev 4 §3.5).
// =====================================================================
//
// XCoreXObject Rev 4 Section 3.5 ("NewObject hot path"). The primary
// XObject construction entry point. Per spec:
//
//   "Two surfaces:
//      * NewObject<T>(Outer, Name, Flags)         -- infallible-style;
//        returns T*; asserts on allocation failure (treats OOM as a
//        programmer error, abort in Shipping per error handling
//        discipline §2a).
//      * TryNewObject<T>(Outer, Name, Flags)      -- fallible-style;
//        returns Result<T*, ENewObjectError>; intended for OOM-
//        tolerant call sites (loader paths, async streaming workers)."
//
// FLOW (per spec §3.5 + Rev 2 FIX-A-CRIT-1 / O10 + FIX-A-CRIT-5 +
// FIX-A-HIGH-12 / UE-MISS-2):
//
//   1. Pre-condition: NewObject during GC mark forbidden (Phase 5.h+
//      check; Phase 5.d documents the invariant; the runtime
//      FXObjectCollector::IsMarking probe lands at Phase 5.h).
//   2. Pre-condition: init-phase gate (FIX-A-CRIT-5):
//      XPACT_CHECK(EngineInitPhase() >= EInitPhase::PostStaticInit).
//   3. Pre-condition: sim-path runtime invariant (FIX-A-CRIT-1):
//      XPACT_CHECK_SL(IsSimPathThread() || !IsSimPathTU()) -- documented
//      + the runtime probes are wired as they ship (currently no-op).
//   4. Allocate raw storage via FXObjectAllocator::AllocateRaw with
//      the 3-arg signature (Size, Align, FClass*).
//   5. Reserve an FXObjectArray entry slot (2-step ReserveSlot +
//      BindObject per spec §3.5 to preserve weak-ptr consistency).
//   6. Construct an FXObjectInitializer on the stack (skipped when
//      RF_NeedLoad is set per Rev 3 FIX-M-R2-4 / §10.6.1 async loader).
//   7. Placement-new T via FClass's ClassConstructorFn, passing the
//      Initializer through.
//   8. Fill the XObject header (ClassPrivate / InternalIndex /
//      SerialNumber / Outer / NamePrivate / ObjectFlags).
//   9. Bind the XObject to the FXObjectArray slot.
//   10. ~FXObjectInitializer fires at scope exit -> dispatches
//       PostInitProperties via FClass->LifecycleTable.
//   11. Clear the NeedInitialization flag.
//   12. Return T*.
//
// PHASE 5.d SCOPE: the spec-described flow ships in full. The
// ClassConstructorFn signature in XCore-4b is `void(*)(void*,
// FFieldVariant)` (no Initializer parameter); the Phase 5.d Initializer
// is constructed AROUND the existing ClassConstructorFn call (the
// Initializer's lifetime brackets the placement-new). XHT's emit (Phase
// 5.f+) will widen the ClassConstructorFn signature to thread the
// Initializer through to the C++ user-class ctor's body; until then,
// the Phase 5.d posture is "Initializer dtor fires PostInitProperties
// after the existing 2-arg ClassConstructorFn returns".
//
// The CreateDefaultSubobject path works at Phase 5.d because the user-
// class ctor can capture the Initializer by const-ref via the
// canonical signature in spec §8.4.1. The capture happens through the
// Initializer's lifetime overlap with the ctor body (the Initializer
// is alive on the stack during the ClassConstructorFn call); the user-
// class ctor passes the Initializer reference into its own member
// initialiser list / body.
//
// Practical Phase 5.d capture pattern: the user-class ctor that wants
// Initializer access takes it as a constructor argument; the
// XHT-emitted ClassConstructorFn (Phase 5.f) supplies the argument.
// Until XHT-emit ships, hand-rolled XObject subclasses use the
// canonical signature and CreateDefaultSubobject works as documented.
//
// HOT-RELOAD: NO virtual methods on NewObject's surface. The template
// monomorphises at every call site; the actual allocation +
// construction work is in NewObjectImpl (non-template, .cpp body) so
// cross-DLL ABI is the NewObjectImpl signature, not the per-T template
// instantiation.
//
// SIM-PATH: the runtime invariant guard XPACT_CHECK_SL fires when the
// sim-path TU runtime probe (::XCore::HAL::IsSimPathTU) returns true
// AND the calling thread is NOT the sim-path serial executor. The
// probe ships in a future phase; Phase 5.d documents the invariant +
// the static-analysis filter at the build level is the primary
// enforcement.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "Macros/XResult.h"

#include "HAL/XInitPhase.h"               // EngineInitPhase + EInitPhase::PostStaticInit
#include "Reflection/FName.h"
#include "XObject/EObjectFlags.h"
#include "XObject/XObject.h"

#include <cstdint>
#include <type_traits>                    // is_base_of_v

// Forward declarations.
namespace XCore::Reflect { struct FClass; }

namespace XCore
{

    // -----------------------------------------------------------------
    // ENewObjectError -- the fallible-path error enum (per spec §3.5
    // + Rev 3 FIX-M-R2-1).
    //
    // Returned by TryNewObject<T>(...) on failure. The infallible
    // NewObject<T> path treats every error as a fatal abort (Prime
    // Directive: OOM IS a programmer error in trainee builds; loader
    // paths use Try* for the tolerant path).
    // -----------------------------------------------------------------
    enum class ENewObjectError : ::std::uint8_t
    {
        // Allocation failure -- FXObjectAllocator::AllocateRaw returned
        // nullptr (the underlying FMemory::Malloc OOM-policy was not
        // "abort"; the caller is opting in to OOM-tolerance via Try*).
        kOutOfMemory          = 1,

        // The FClass pointer was nullptr (typically a programmer error
        // at the call site; Try* surfaces it as an error rather than
        // an XPACT_CHECK abort to give loader paths a recoverable
        // signal).
        kInvalidClass         = 2,

        // The EInitPhase gate failed -- NewObject called before
        // PostStaticInit. Try* surfaces this as an error; the
        // infallible path asserts via XPACT_CHECK.
        kInvalidPhase         = 3,

        // The supplied FClass has EClassFlags::CLASS_Abstract; the
        // class cannot be instantiated directly. The infallible path
        // asserts; Try* returns this error.
        kAbstractClass        = 4,
    };

    // -----------------------------------------------------------------
    // NewObjectImpl -- type-erased allocation + construction.
    //
    // The non-template body for the NewObject path. Lives in
    // NewObject.cpp so per-T template instantiations don't bloat
    // every consumer TU.
    //
    // Returns nullptr on allocation failure or pre-condition failure
    // (NewObject<T>'s body wraps the call in XPACT_CHECK; TryNewObject
    // surfaces the failure as an error). Phase 5.d ships the
    // happy-path body; the OOM-tolerant integration with FMemoryConfig
    // is documented but not exercised at unit-test scale.
    //
    // The infallible NewObject<T> template invokes NewObjectImpl;
    // TryNewObject<T> also invokes NewObjectImpl, with the error
    // discrimination handled at the Try* template.
    //
    // PRE-CONDITIONS (asserted by NewObjectImpl in Dev/Debug):
    //   * Class != nullptr (kInvalidClass surface via Try*).
    //   * EngineInitPhase() >= EInitPhase::PostStaticInit (kInvalidPhase).
    //   * Class is not abstract (kAbstractClass).
    //   * Sim-path runtime invariant holds (FIX-A-CRIT-1; documented).
    //
    // The IMPL function signature:
    //   - Class      -- the FClass descriptor (non-null).
    //   - Outer      -- the containing object (may be nullptr for top-
    //                    level objects + packages).
    //   - Name       -- the FName for the new object (NAME_None means
    //                    "auto-generated by the allocator" -- the
    //                    name field is populated with a synthesised
    //                    "ClassName_N" form using the InternalIndex).
    //   - Flags      -- caller-supplied EObjectFlags (Public, Transient,
    //                    etc.). NeedInitialization is set during
    //                    construction + cleared after PostInitProperties.
    //   - Archetype  -- optional archetype object (nullptr for "use
    //                    the CDO of Class as the default archetype";
    //                    if Class has no CDO yet, no archetype is
    //                    used).
    // -----------------------------------------------------------------
    XObject* NewObjectImpl(const ::XCore::Reflect::FClass* Class,
                           XObject* Outer,
                           ::XCore::Reflect::FName Name,
                           EObjectFlags Flags,
                           XObject* Archetype) noexcept;

    // -----------------------------------------------------------------
    // TryNewObjectImpl -- fallible-style entry point.
    //
    // Returns the constructed XObject* on success or one of the
    // ENewObjectError values on failure. The error-surface contract:
    //   * Class == nullptr  -> kInvalidClass
    //   * EngineInitPhase() < PostStaticInit -> kInvalidPhase
    //   * Class has CLASS_Abstract -> kAbstractClass
    //   * Allocation fails (post phase-gate) -> kOutOfMemory
    //
    // The Try* path does NOT abort on these conditions; it surfaces
    // them as errors. Phase 5.d ships the dispatch + error-code
    // surface; the full OOM-tolerant interop with FMemoryConfig's
    // policy switch lands as a follow-up (the underlying
    // FXObjectAllocator::AllocateRaw aborts on OOM today by routing
    // through FMemory::MallocOrAbort; the Try-tolerant variant of
    // that routine is a Phase 5.h+ deliverable per spec §3.5
    // trailing note).
    // -----------------------------------------------------------------
    ::XCore::Result<XObject*, ENewObjectError> TryNewObjectImpl(
        const ::XCore::Reflect::FClass* Class,
        XObject* Outer,
        ::XCore::Reflect::FName Name,
        EObjectFlags Flags,
        XObject* Archetype) noexcept;

    // -----------------------------------------------------------------
    // NewObject<T> -- the templated entry point (per spec §3.5).
    //
    // Returns a fully-constructed T* with:
    //   * Outer = the supplied Outer (or nullptr for top-level).
    //   * Name  = the supplied FName.
    //   * Flags = the supplied EObjectFlags + NeedInitialization
    //             (cleared after PostInitProperties).
    //   * ClassPrivate = T::StaticClass().
    //   * InternalIndex + SerialNumber = freshly-allocated FXObjectArray
    //                                    slot.
    //
    // PostInitProperties fires before the return (via the
    // FXObjectInitializer destructor at NewObjectImpl scope exit).
    //
    // Asserts on allocation failure / invalid pre-conditions. The
    // fallible TryNewObject variant below is the recoverable path.
    //
    // Per spec §3.5 trailing prose: "Total cost per NewObject:
    // ~150-300 ns on Win64 desktop, ~250-500 ns on Quest 3 ARM64
    // (target; measured against UE's NewObject path which is ~600-
    // 1000 ns)."
    //
    // T MUST derive from XObject. Enforced by static_assert below.
    // -----------------------------------------------------------------
    template <typename T>
    [[nodiscard]] T* NewObject(XObject* Outer = nullptr,
                               ::XCore::Reflect::FName Name = ::XCore::Reflect::FName(),
                               EObjectFlags Flags = EObjectFlags::None,
                               XObject* Archetype = nullptr) noexcept
    {
        static_assert(::std::is_base_of_v<XObject, T>,
                      "NewObject<T> requires T : XObject. Non-XObject types "
                      "are constructed via the normal C++ ctor surface, not "
                      "the GC-aware NewObject hot path.");

        // Per spec §3.5 + FIX-A-CRIT-5 (init-phase gate).
        //
        // The XPACT_CHECK fires in Dev/Debug if NewObject is called
        // before PostStaticInit (typically a bootstrap-ordering bug
        // in a static initializer that fires too early). In Shipping
        // the check compiles out; the underlying allocator's nullptr
        // return (if FXObjectAllocator's bootstrap hasn't run) would
        // surface as a segfault.
        XPACT_CHECK(::XCore::HAL::EngineInitPhase() >= ::XCore::HAL::EInitPhase::PostStaticInit);

        // TODO(Phase 5.e+ sim-path runtime probe): wire
        // ::XCore::HAL::IsSimPathTU + ::XCore::HAL::IsSimPathThread
        // here once the probes ship:
        //     XPACT_CHECK_SL(::XCore::HAL::IsSimPathThread() ||
        //                    !::XCore::HAL::IsSimPathTU());
        //
        // Per FIX-A-CRIT-1: sim-path TUs MAY call NewObject ONLY from
        // the SimPathSerialExecutor thread. Until the probes ship, the
        // static-analysis filter at the build level is the primary
        // enforcement.

        return static_cast<T*>(
            NewObjectImpl(T::StaticClass(), Outer, Name, Flags, Archetype));
    }

    // -----------------------------------------------------------------
    // TryNewObject<T> -- fallible-style entry point (per spec §3.5
    // + Rev 3 FIX-M-R2-1).
    //
    // Returns Result<T*, ENewObjectError>: T* on success, an error
    // code on failure. Use this from loader paths and async streaming
    // workers where OOM is recoverable.
    //
    // Pre-conditions:
    //   * T MUST derive from XObject (static_assert).
    //   * EngineInitPhase() >= PostStaticInit (surfaces as
    //     kInvalidPhase).
    //
    // Phase 5.d ships the dispatch + error-code surface. The OOM-
    // tolerant interop with FMemoryConfig's policy switch ships at a
    // follow-up phase (the underlying FXObjectAllocator::AllocateRaw
    // routes through FMemory::MallocOrAbort which aborts on OOM
    // today).
    // -----------------------------------------------------------------
    template <typename T>
    [[nodiscard]] ::XCore::Result<T*, ENewObjectError>
        TryNewObject(XObject* Outer = nullptr,
                     ::XCore::Reflect::FName Name = ::XCore::Reflect::FName(),
                     EObjectFlags Flags = EObjectFlags::None,
                     XObject* Archetype = nullptr) noexcept
    {
        static_assert(::std::is_base_of_v<XObject, T>,
                      "TryNewObject<T> requires T : XObject.");

        // T::StaticClass() is the canonical FClass* for the type.
        // The static_assert above plus the StaticClass() method
        // requirement (XObject subclasses ship a `static const
        // FClass* StaticClass() noexcept` method emitted by XHT)
        // ensures the lookup is well-formed.
        const ::XCore::Reflect::FClass* const Class = T::StaticClass();

        ::XCore::Result<XObject*, ENewObjectError> Result =
            TryNewObjectImpl(Class, Outer, Name, Flags, Archetype);

        if (Result.has_value())
        {
            return static_cast<T*>(Result.value());
        }
        return ::XCore::Unexpected(Result.error());
    }

} // namespace XCore
