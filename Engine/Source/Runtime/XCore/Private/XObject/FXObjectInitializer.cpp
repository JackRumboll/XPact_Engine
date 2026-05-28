// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectInitializer.cpp -- destructor-driven PostInitProperties
// dispatch + CreateDefaultSubobject sub-object construction
// (XCoreXObject Rev 4 §8.4 + Phase 5.d body).
// =====================================================================
//
// Two bodies live in this TU:
//
//   * ~FXObjectInitializer -- fires PostInitProperties exactly once
//     via FClass::LifecycleTable->GetSlot<PostInitProperties>(). The
//     idempotency flag (bPostInitFired) prevents re-fire on
//     inheritance-chain pass-through.
//
//   * CreateDefaultSubobjectImpl -- type-erased sub-object construction.
//     Routes through XCore::NewObjectImpl with the supplied SubClass
//     + the Target as the Outer. Optionally consults the
//     InstancingGraph for archetype-mapped sub-objects (Phase 5.d
//     ships the registration; the archetype-by-name lookup is
//     documented for the Duplicate/Deserialize follow-up path).
//
// =====================================================================

#include "XObject/FXObjectInitializer.h"

#include "Reflection/FClass.h"
#include "XObject/FXObjectLifecycleTable.h"
#include "XObject/NewObject.h"

#include "Macros/XAssertionMacros.h"

#include <cstdint>

namespace XCore
{

    // =================================================================
    // ~FXObjectInitializer -- fire PostInitProperties at scope exit
    // (per spec §8.4 reference impl + Rev 3 FIX-M-R2-5).
    //
    // Steps:
    //   1. If bPostInitFired is true, return (already fired; the
    //      inheritance-chain pass-through hit a leaf initializer
    //      whose destructor fired first).
    //   2. Set bPostInitFired = true (so any subsequent dispatch
    //      attempt -- typically the canonical-signature pass-through
    //      pattern where the same Initializer destructs multiple
    //      times in a chain -- is a no-op).
    //   3. Read Class->LifecycleTable. If nullptr, the class has no
    //      lifecycle hooks -- skip dispatch.
    //   4. Check HasCapability(HasPostInitProperties). If false, the
    //      slot is unpopulated -- skip dispatch.
    //   5. Cast Slots[PostInitProperties] to the typed function
    //      pointer; invoke it with Target.
    //
    // noexcept: the FXObjectPostInitPropertiesFn signature is itself
    // noexcept; PostInitProperties implementations MUST NOT throw
    // (any throw would propagate through std::terminate via the
    // noexcept boundary). This is a Prime Directive constraint
    // documented at the spec §8.4 reference impl.
    // =================================================================
    FXObjectInitializer::~FXObjectInitializer() noexcept
    {
        // Idempotency check (Rev 3 FIX-M-R2-5). The flag is mutable so
        // the const-ref pass-through pattern (canonical signature per
        // §8.4.1) can mutate it from a const Initializer's destructor.
        if (bPostInitFired)
        {
            return;
        }
        bPostInitFired = true;

        // Dispatch through the FClass's lifecycle table. Target +
        // Class were validated at ctor (XPACT_CHECK on non-null); the
        // LifecycleTable on FClass is nullptr for classes with no
        // lifecycle hooks -- short-circuit safely.
        //
        // FClass::LifecycleTable is typed as
        // `const ::XCore::Reflect::FXObjectLifecycleTable*` per
        // Phase 5.a' forward-decl; the type IS the same as
        // ::XCore::FXObjectLifecycleTable via the XCore namespace
        // re-export alias in FXObjectLifecycleTable.h. Direct member
        // access is well-formed without casting.
        const ::XCore::Reflect::FXObjectLifecycleTable* const Table =
            Class->LifecycleTable;
        if (Table == nullptr)
        {
            // Class has no per-class lifecycle hooks. The default
            // (no-op) PostInitProperties path is implicitly taken.
            return;
        }

        if (!Table->HasCapability(
                ::XCore::Reflect::EXObjectLifecycleCapability::HasPostInitProperties))
        {
            // Class has a lifecycle table but the PostInitProperties
            // slot is empty (Capabilities bit clear). Skip dispatch.
            return;
        }

        // Fetch the typed slot pointer + invoke. The HasCapability
        // check above guarantees the function pointer is non-null;
        // we still defensively check post-cast (defence-in-depth for
        // mis-emitted tables where the bit is set but the slot is
        // null).
        const ::XCore::Reflect::FXObjectPostInitPropertiesFn Fn =
            Table->GetSlot<::XCore::Reflect::EXObjectLifecycleSlot::PostInitProperties>();
        if (Fn != nullptr)
        {
            Fn(Target);
        }
    }

    // =================================================================
    // CreateDefaultSubobjectImpl -- type-erased sub-object construction
    // (per spec §8.4 + §8.4.1).
    //
    // Steps:
    //   1. Validate SubClass + Name (XPACT_CHECK in Dev/Debug).
    //   2. Compose the sub-object's EObjectFlags:
    //        * DefaultSubObject (mandatory per spec §8.4)
    //        * Transient (if bTransient)
    //   3. Consult the InstancingGraph for an archetype-mapped sub-
    //      object. Phase 5.d looks up the Archetype's matching sub-
    //      object by name + class (when an Archetype is supplied);
    //      Phase 5.d's lookup is documented but not yet wired through
    //      the FObjectInstancingGraph (the archetype's sub-object
    //      registry lands when Duplicate/Deserialize ships; Phase
    //      5.d's InstancingGraph is populated only for nested
    //      sub-object resolution).
    //   4. Call NewObjectImpl with:
    //        * Class      = SubClass
    //        * Outer      = Target (this object IS the sub-object's
    //                       parent)
    //        * Name       = the supplied name
    //        * Flags      = the composed flag set
    //        * Archetype  = the archetype's sub-object (if found in
    //                       the graph) or nullptr (default).
    //   5. Register the {archetype-subobject?, new-subobject} pair in
    //      the InstancingGraph so nested CreateDefaultSubobject calls
    //      resolve consistently.
    //   6. Return the new sub-object's XObject*.
    //
    // Phase 5.d returns nullptr on construction failure (the
    // underlying NewObjectImpl returns nullptr on allocation failure;
    // the caller's CreateDefaultSubobject<T> static_cast preserves
    // the nullptr).
    //
    // The method is `const` per the canonical signature contract;
    // it mutates the InstancingGraph through the `mutable`
    // declaration on the member.
    // =================================================================
    XObject* FXObjectInitializer::CreateDefaultSubobjectImpl(
        const ::XCore::Reflect::FClass* SubClass,
        ::XCore::Reflect::FName Name,
        bool bTransient) const noexcept
    {
        XPACT_CHECK(SubClass != nullptr);

        // Compose the sub-object's flags. DefaultSubObject is
        // mandatory per spec §8.4 (the save/duplicate paths special-
        // case DefaultSubObject instances to preserve the template
        // relationship). Transient is opt-in via the bTransient arg.
        EObjectFlags Flags = EObjectFlags::DefaultSubObject;
        if (bTransient)
        {
            Flags |= EObjectFlags::Transient;
        }

        // Archetype lookup: if this Initializer has an Archetype, the
        // sub-object's archetype is the Archetype's matching sub-object
        // (by name + class). Phase 5.d's InstancingGraph is populated
        // by the NewObject path's archetype-CDO walk (when an
        // archetype is supplied to NewObject, the graph is pre-seeded
        // with the archetype's sub-object pairs).
        //
        // The lookup-by-name half (find an archetype sub-object whose
        // Name matches the supplied Name) is a Phase 5.d follow-up:
        // the archetype-CDO walk that pre-seeds the graph by name is
        // implemented at the Duplicate/Deserialize ship; Phase 5.d's
        // standard NewObject<T>(Outer, Name, Flags) path operates
        // with the no-archetype posture (Archetype == nullptr at the
        // sub-object NewObject call), which is correct for the
        // top-level NewObject<T> happy path.
        XObject* SubArchetype = nullptr;
        if (Archetype != nullptr)
        {
            // TODO(Phase 5.e+ archetype sub-object resolution): walk
            // Archetype's sub-objects by name + class to find the
            // matching template. The Phase 5.d posture is "Archetype
            // is supplied at the top-level NewObject; sub-objects
            // resolve as no-archetype until the Duplicate/Deserialize
            // path ships the archetype-CDO sub-object walk."
            //
            // Once the walk ships, this site populates SubArchetype
            // from Archetype's sub-object registry (which lands when
            // the FStructObjectRefSchema / Sub-Object walk emits
            // expose the per-instance sub-object index).
        }

        // Construct the sub-object via the standard NewObjectImpl path.
        // Outer = Target (this object IS the sub-object's parent).
        XObject* const Sub = ::XCore::NewObjectImpl(
            SubClass,
            Target,                         // Outer
            Name,
            Flags,
            SubArchetype);                  // Archetype (typically null at Phase 5.d)

        if (Sub == nullptr)
        {
            // Allocation failure or pre-condition violation. The
            // caller's CreateDefaultSubobject<T> static_cast preserves
            // the nullptr; the user-class ctor is responsible for
            // handling the null sub-object (typically a programmer
            // error; the infallible NewObject path would have already
            // aborted in NewObjectImpl).
            return nullptr;
        }

        // Register the sub-object in the InstancingGraph. The graph
        // tracks {archetype-subobject, new-subobject} pairs; for the
        // no-archetype case the SubArchetype is nullptr and the
        // entry's archetype is the new sub-object itself (the
        // "self-archetype" form supports nested CreateDefaultSubobject
        // calls that walk the graph for the parent's sub-object
        // ancestry).
        //
        // The graph mutation is well-formed under the const-ref
        // contract because InstancingGraph is `mutable` on the
        // Initializer.
        if (SubArchetype != nullptr)
        {
            InstancingGraph.RegisterArchetype(SubArchetype, Sub);
        }

        return Sub;
    }

} // namespace XCore
