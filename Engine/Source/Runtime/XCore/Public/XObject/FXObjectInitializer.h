// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FXObjectInitializer.h -- destructor-driven PostInitProperties
// construct (XCoreXObject Rev 4 §8.4 + Rev 2 FIX-A-HIGH-12 /
// UE-MISS-2 + Rev 3 FIX-M-R2-5 / FIX-M-R2-6).
// =====================================================================
//
// XCoreXObject Rev 4 Section 8.4 ("FXObjectInitializer + Default
// Sub-objects (DSO)") + the canonical inheritance pattern in §8.4.1.
//
// PURPOSE: FXObjectInitializer is the destructor-driven finalization
// construct used by the NewObject<T> hot path. The pattern (mirroring
// UE's FObjectInitializer at UObjectGlobals.h:1315-1346):
//
//   1. NewObject<T> allocates raw storage + reserves an FXObjectArray
//      slot.
//   2. NewObject<T> constructs an FXObjectInitializer on the stack.
//   3. NewObject<T> placement-news T into the storage via FClass's
//      ClassConstructorFn, passing the FXObjectInitializer through.
//   4. The T constructor body calls
//      `Initializer.CreateDefaultSubobject<U>(Name)` to register
//      stable-named sub-objects.
//   5. The T constructor body completes. Control returns to NewObject.
//   6. ~FXObjectInitializer fires at NewObject's scope exit. The
//      destructor dispatches PostInitProperties via the FClass's
//      lifecycle FakeVTable (per spec §2.4).
//
// IDEMPOTENCY (Rev 3 FIX-M-R2-5): the destructor checks-and-sets a
// `bPostInitFired` flag so PostInitProperties fires EXACTLY ONCE per
// FXObjectInitializer, even in inheritance chains where the same
// Initializer pass-through hits multiple destructors. The flag is
// `mutable bool` because the canonical signature passes the
// Initializer by const-ref through inheritance chains (per Rev 3
// FIX-M-R2-6).
//
// CANONICAL CONSTRUCTOR SIGNATURE (Rev 3 FIX-M-R2-6):
//
//   class XMyActor : public XObject {
//   public:
//       explicit XMyActor(const FXObjectInitializer& Initializer)
//           : XObject(Initializer)
//       {
//           MyComponent = Initializer.CreateDefaultSubobject<XMyComponent>(
//               FName("MyComponent"));
//       }
//   };
//
// The const-ref signature gives the Initializer the mutability it
// needs internally (via the `mutable` keyword on its private state)
// while preventing user code from reassigning or moving it. Sub-
// objects registered via CreateDefaultSubobject from any level of the
// inheritance chain all attribute to the same Initializer; the
// Initializer knows the target object's identity from construction
// time.
//
// PHASE 5.d SCOPE:
//
//   * Initializer ctor binds Target / Class / Archetype + initialises
//     the instancing graph + sets bPostInitFired = false.
//   * Initializer dtor checks bPostInitFired; if not set, dispatches
//     PostInitProperties via the FClass's lifecycle table and sets
//     the flag.
//   * CreateDefaultSubobject<T>(Name) allocates a sub-object via
//     NewObject, sets its Outer to the target object, registers the
//     {archetype-subobject, instance-subobject} pair in the instancing
//     graph (when an archetype is supplied), and returns the
//     constructed sub-object pointer.
//   * Non-copyable, non-movable (lifetime tied to the NewObject stack
//     frame).
//
// HOT-RELOAD: NO virtual methods on this surface. The Initializer is
// stack-allocated; cross-DLL stability is not load-bearing. The
// PostInitProperties dispatch goes through the FClass's lifecycle
// table which IS hot-reload-safe.
//
// SIM-PATH: the Initializer is created on the same thread as the
// NewObject call. Sim-path TUs MAY use the Initializer through the
// canonical ctor signature; the underlying NewObject path enforces
// the sim-path runtime invariant per spec §3.5 + FIX-A-CRIT-1.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Reflection/FName.h"
#include "XObject/XObject.h"
#include "XObject/EObjectFlags.h"
#include "XObject/FObjectInstancingGraph.h"

#include <type_traits>          // is_base_of_v

// Forward declarations.
namespace XCore::Reflect { struct FClass; }

namespace XCore
{

    // -----------------------------------------------------------------
    // FXObjectInitializer -- destructor-driven PostInitProperties
    // construct (per spec §8.4).
    //
    // alignas(8) -- matches the natural alignment of the contained
    // pointers + the FObjectInstancingGraph (which holds a TMap
    // internally).
    //
    // The Initializer is owned by the NewObject<T> stack frame; its
    // lifetime ends when NewObject<T> returns. Misuse (storing the
    // Initializer beyond the scope; copying or moving it) is a compile
    // error per the deleted copy / move ops.
    //
    // NO XPACT_*_LAYOUT_TAG: FXObjectInitializer is NOT in the ABI-
    // lock set (its layout depends on the FObjectInstancingGraph which
    // depends on TMap, which depends on XCore-4a Phase 1d internals).
    // The Initializer is a stack-only runtime data structure; cross-
    // DLL stability is not load-bearing.
    // -----------------------------------------------------------------
    class alignas(8) FXObjectInitializer
    {
    public:
        // =============================================================
        // Construction (per spec §8.4 reference signature).
        //
        // Binds:
        //   * Target    -- the XObject being constructed (the storage
        //                  pointer returned by FXObjectAllocator's
        //                  AllocateRaw, cast to XObject*; the placement-
        //                  new has run, the XObject header is filled).
        //   * Class     -- the FClass descriptor for Target's class.
        //                  Used for the PostInitProperties dispatch
        //                  through Class->LifecycleTable.
        //   * Archetype -- optional archetype object (the CDO of Class,
        //                  or a user-supplied archetype). nullptr is
        //                  valid: the construction proceeds with no
        //                  archetype templating (sub-objects get
        //                  default-constructed values from their
        //                  classes' CDOs at their CreateDefaultSubobject
        //                  call sites, recursively).
        //
        // The ctor initialises the instancing graph empty + sets
        // bPostInitFired = false.
        //
        // noexcept: the ctor only stores pointers + initialises the
        // graph (empty TMap; no allocation). No throw paths.
        // =============================================================
        FXObjectInitializer(XObject* InTarget,
                            const ::XCore::Reflect::FClass* InClass,
                            XObject* InArchetype = nullptr) noexcept
            : Target(InTarget)
            , Class(InClass)
            , Archetype(InArchetype)
            , InstancingGraph()
            , bPostInitFired(false)
        {
            // Pre-conditions: the Target + Class pointers MUST be
            // non-null for a well-formed Initializer (the destructor
            // dereferences Class to read its LifecycleTable). The
            // Archetype MAY be null (the no-archetype path).
            //
            // XPACT_CHECK fires in Dev/Debug; compiles out in Shipping
            // where the caller (NewObject<T>) has already done the
            // equivalent checks at its entry.
            XPACT_CHECK(InTarget != nullptr);
            XPACT_CHECK(InClass  != nullptr);
        }

        // =============================================================
        // Destructor: fire PostInitProperties exactly once (Rev 3
        // FIX-M-R2-5 idempotency).
        //
        // Body in FXObjectInitializer.cpp -- the dispatch through the
        // FClass's LifecycleTable touches Class.LifecycleTable +
        // Slots[0] and dispatches the typed function pointer. The
        // non-inline body avoids pulling FXObjectLifecycleTable.h into
        // every TU that #includes FXObjectInitializer.h.
        //
        // noexcept: PostInitProperties is itself noexcept (per the
        // FXObjectPostInitPropertiesFn typedef in
        // FXObjectLifecycleTable.h); the destructor's only allocation
        // path is the implicit InstancingGraph teardown (TMap dtor)
        // which is noexcept-on-trivial-key+value (XObject* keys, XObject*
        // values).
        // =============================================================
        ~FXObjectInitializer() noexcept;

        // =============================================================
        // Non-copyable, non-movable.
        //
        // The Initializer's lifetime IS the NewObject<T> stack frame.
        // Copying or moving would either:
        //   * Re-fire PostInitProperties on destruction (the
        //     bPostInitFired flag would not be preserved if the move
        //     "transferred" ownership; the source's dtor would still
        //     fire if the flag was copied).
        //   * Or skip the fire entirely (if the source-flag-cleared
        //     pattern was followed, but the destination would never
        //     fire either, breaking the post-construction contract).
        //
        // Both modes are bugs. The discipline is "stack-allocated,
        // non-transferable" -- the dtor fires exactly once at the
        // single defined destruction point.
        // =============================================================
        FXObjectInitializer(const FXObjectInitializer&)            = delete;
        FXObjectInitializer(FXObjectInitializer&&)                 = delete;
        FXObjectInitializer& operator=(const FXObjectInitializer&) = delete;
        FXObjectInitializer& operator=(FXObjectInitializer&&)      = delete;

        // =============================================================
        // CreateDefaultSubobject<T>(Name) -- register a stable-named
        // sub-object (per spec §8.4 + §8.4.1).
        //
        // Called from the user-class constructor body. The sub-object's
        // properties:
        //   * Outer  = Target (this object IS the sub-object's parent)
        //   * Class  = T::StaticClass()
        //   * Name   = the provided FName
        //   * Flags  = EObjectFlags::DefaultSubObject (mandatory) plus
        //              any caller-supplied additional flags.
        //
        // Phase 5.d SCOPE: the function delegates to
        // CreateDefaultSubobjectImpl which routes through XCore::
        // NewObjectImpl (the type-erased NewObject path). If an
        // archetype is supplied AND the archetype has a sub-object
        // with a matching name, the new sub-object is templated from
        // the archetype's sub-object (deep-copied properties). The
        // {archetype-subobject, new-subobject} pair is registered in
        // the InstancingGraph for nested CreateDefaultSubobject calls.
        //
        // The `bTransient` parameter selects whether the sub-object is
        // marked Transient. Transient sub-objects are not serialised;
        // useful for editor-only state or runtime caches that the
        // save path should skip.
        //
        // Returns the constructed sub-object cast to T*. The cast is
        // guaranteed by the static_assert below (T must derive from
        // XObject).
        //
        // The Initializer is `const` (by the canonical ctor signature
        // contract); the `mutable` private state (InstancingGraph,
        // bPostInitFired) allows the method to mutate the graph
        // without violating const-correctness at the user-facing
        // call site.
        // =============================================================
        template <typename T>
        T* CreateDefaultSubobject(::XCore::Reflect::FName Name,
                                  bool bTransient = false) const noexcept
        {
            static_assert(::std::is_base_of_v<XObject, T>,
                          "CreateDefaultSubobject<T> requires T : XObject. "
                          "Sub-objects MUST derive from XObject (the GC walk "
                          "relies on this for object-graph traversal).");
            return static_cast<T*>(
                CreateDefaultSubobjectImpl(T::StaticClass(), Name, bTransient));
        }

        // =============================================================
        // CreateDefaultSubobjectImpl -- type-erased sub-object creation.
        //
        // Routes through XCore::NewObjectImpl (NewObject.h) with:
        //   * SubClass = the supplied FClass*
        //   * Outer    = Target (this object)
        //   * Name     = the supplied FName
        //   * Flags    = EObjectFlags::DefaultSubObject (+ Transient
        //                conditionally)
        //   * Archetype = the archetype's matching sub-object if
        //                  present in the InstancingGraph (lazy lookup
        //                  by name); nullptr otherwise.
        //
        // Body in FXObjectInitializer.cpp -- the routing through
        // NewObjectImpl keeps the implementation in a single TU.
        //
        // The method is `const` per the canonical signature contract;
        // it MAY mutate the InstancingGraph through the `mutable`
        // declaration.
        // =============================================================
        XObject* CreateDefaultSubobjectImpl(
            const ::XCore::Reflect::FClass* SubClass,
            ::XCore::Reflect::FName Name,
            bool bTransient) const noexcept;

        // =============================================================
        // Accessors.
        // =============================================================

        // The XObject being constructed.
        [[nodiscard]] XPACT_FORCEINLINE XObject* GetTarget() const noexcept
        {
            return Target;
        }

        // The FClass descriptor for the target's class.
        [[nodiscard]] XPACT_FORCEINLINE const ::XCore::Reflect::FClass* GetClass() const noexcept
        {
            return Class;
        }

        // The supplied archetype (may be nullptr).
        [[nodiscard]] XPACT_FORCEINLINE XObject* GetArchetype() const noexcept
        {
            return Archetype;
        }

        // True iff PostInitProperties has fired (either by the
        // destructor or by a previous pass-through that triggered it).
        // Diagnostic / test API.
        [[nodiscard]] XPACT_FORCEINLINE bool HasFiredPostInit() const noexcept
        {
            return bPostInitFired;
        }

        // The instancing graph (mutable internal state; exposed for
        // diagnostic / test inspection).
        [[nodiscard]] XPACT_FORCEINLINE const FObjectInstancingGraph& GetInstancingGraph() const noexcept
        {
            return InstancingGraph;
        }

    private:
        // -------------------------------------------------------------
        // Target -- the XObject being constructed.
        //
        // Set at ctor; never null (the ctor checks). The Initializer
        // dispatches PostInitProperties via Class->LifecycleTable on
        // this pointer at destruction time.
        // -------------------------------------------------------------
        XObject*                              Target;

        // -------------------------------------------------------------
        // Class -- the FClass descriptor.
        //
        // Set at ctor; never null. The Initializer reads
        // Class->LifecycleTable->Slots[PostInitProperties] at
        // destruction time. The Class outlives the Initializer because
        // FClass instances are .rodata-resident; the Class pointer is
        // stable for the process lifetime.
        // -------------------------------------------------------------
        const ::XCore::Reflect::FClass*       Class;

        // -------------------------------------------------------------
        // Archetype -- optional template object.
        //
        // Typically the CDO of Class (when present, the standard
        // NewObject<T> path uses the CDO as the archetype). May be
        // user-supplied for Duplicate / Deserialize paths. nullptr is
        // valid: the Initializer proceeds with no archetype templating.
        // -------------------------------------------------------------
        XObject*                              Archetype;

        // -------------------------------------------------------------
        // InstancingGraph -- archetype-to-instance mapping.
        //
        // mutable: the canonical const-ref signature requires the
        // graph to be mutable through const-Initializer access (the
        // user-class ctor calls CreateDefaultSubobject through const-
        // ref; CreateDefaultSubobject mutates the graph to register
        // the new sub-object's archetype pair).
        //
        // Per spec §8.4: the graph is constructed empty + populated
        // as CreateDefaultSubobject calls fire; discarded at
        // ~FXObjectInitializer scope exit.
        // -------------------------------------------------------------
        mutable FObjectInstancingGraph        InstancingGraph;

        // -------------------------------------------------------------
        // bPostInitFired -- idempotency flag (Rev 3 FIX-M-R2-5).
        //
        // mutable: the destructor (which fires on a const-Initializer
        // when the canonical pass-through pattern is used) checks-and-
        // sets the flag. The const-cast alternative would violate
        // const-correctness at the type-system level.
        //
        // The flag is checked + set under no lock: the Initializer's
        // lifetime is single-threaded (one NewObject<T> stack frame on
        // one thread) so the check-and-set is structurally race-free.
        //
        // PRE-CONDITION at ctor: false (no PostInitProperties has
        // fired). POST-CONDITION at dtor: true (PostInitProperties has
        // fired exactly once OR the slot was empty + no dispatch
        // happened; either way the flag is true so re-dispatch is
        // suppressed).
        // -------------------------------------------------------------
        mutable bool                          bPostInitFired;
    };

    // -----------------------------------------------------------------
    // Trait locks.
    //
    // FXObjectInitializer is NOT trivially copyable (the
    // InstancingGraph holds heap-allocated TMap storage; the ctor +
    // dtor have side effects). It IS non-polymorphic (no virtual
    // methods) so the hot-reload commitment is preserved.
    //
    // No XPACT_*_LAYOUT_TAG: FXObjectInitializer is NOT in the ABI-
    // lock set (its layout depends on TMap which depends on XCore-4a
    // Phase 1d internals). The Initializer is a stack-only runtime
    // data structure; cross-DLL stability is not load-bearing.
    // -----------------------------------------------------------------
    static_assert(!::std::is_polymorphic_v<FXObjectInitializer>,
                  "FXObjectInitializer must NOT be polymorphic "
                  "(hot-reload commitment + FakeVTable dispatch pattern).");
    static_assert(!::std::is_copy_constructible_v<FXObjectInitializer>,
                  "FXObjectInitializer MUST be non-copyable "
                  "(lifetime tied to NewObject stack frame).");
    static_assert(!::std::is_move_constructible_v<FXObjectInitializer>,
                  "FXObjectInitializer MUST be non-movable "
                  "(lifetime tied to NewObject stack frame).");
    static_assert(!::std::is_copy_assignable_v<FXObjectInitializer>,
                  "FXObjectInitializer MUST be non-copy-assignable.");
    static_assert(!::std::is_move_assignable_v<FXObjectInitializer>,
                  "FXObjectInitializer MUST be non-move-assignable.");

} // namespace XCore
