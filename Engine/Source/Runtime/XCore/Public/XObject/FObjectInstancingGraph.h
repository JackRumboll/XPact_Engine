// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FObjectInstancingGraph.h -- archetype-to-instance mapping during
// XObject construction (XCoreXObject Rev 4 §8.4).
// =====================================================================
//
// XCoreXObject Rev 4 Section 8.4 ("FXObjectInitializer + Default
// Sub-objects (DSO)") trailing prose:
//
//   "FObjectInstancingGraph (UE mirror at UObjectInstancingGraph.h):
//    tracks archetype mappings during construction. When a deserialize
//    path constructs an XActor whose archetype is a Blueprint-derived
//    class, the FObjectInstancingGraph maps each sub-object in the
//    archetype's CDO to a sub-object instance in the new XActor. Sub-
//    object templating: CDO sub-objects are deep-copied for non-CDO
//    instances unless overridden."
//
// PURPOSE: during NewObject<T>, if an archetype is supplied (the most
// common case is "the CDO of T"), the constructor body's
// CreateDefaultSubobject calls must produce sub-object instances that
// are DEEP-COPIES of the archetype's sub-objects (not aliases of them;
// not freshly-defaulted instances). The instancing graph holds the
// archetype-to-instance mapping so the CreateDefaultSubobject's internal
// resolution can:
//
//   1. Look up the archetype's sub-object by its template name.
//   2. Construct the instance's sub-object as a copy of the archetype's
//      sub-object (the deep-copy walks the property tree).
//   3. Register the {archetype-subobject, instance-subobject} pair in
//      the graph so nested CreateDefaultSubobject calls resolve
//      consistently.
//
// USE CASES (per spec §8.4):
//
//   * NewObject<XCharacter>(Outer, "MyChar") with archetype = CDO of
//     XCharacter. The XCharacter ctor body calls
//     `CreateDefaultSubobject<XMeshComponent>("Mesh")`; the graph
//     pairs the CDO's Mesh with the new instance's Mesh so subsequent
//     property writes on the instance's Mesh do not leak into the
//     CDO's Mesh.
//
//   * Future Duplicate<T>(Source) path (post-Phase 5.d): the graph
//     pairs each sub-object of Source with the corresponding new
//     sub-object in the duplicate. Deep-copy of the object tree.
//
//   * Future Deserialize path: the loader constructs each XObject from
//     archive bytes; the instancing graph pairs the archetype's sub-
//     objects (from the source class's CDO) with the deserialized
//     instance's sub-objects so the property-set values from the
//     archive override the archetype defaults correctly.
//
// PHASE 5.d SCOPE: the FXObjectInitializer holds an instance of
// FObjectInstancingGraph by value; the graph is constructed at
// FXObjectInitializer ctor + discarded at FXObjectInitializer dtor.
// The single CreateDefaultSubobject path (Phase 5.d's primary
// consumer) uses the graph for the "this archetype + this name -> use
// this instance for the sub-object" resolution. Future Duplicate /
// Deserialize paths reuse this type without modification.
//
// CONCURRENCY: the graph is NOT thread-safe. The expected usage is one
// graph per NewObject call, owned by the stack-allocated
// FXObjectInitializer. The instancing operations happen entirely on
// the calling thread.
//
// HOT-RELOAD: NO virtual methods. The graph is a simple TMap wrapper;
// hot-reload survival is trivial (no per-class layout / vtable / FClass
// pointer dependencies; the FObjectInstancingGraph is purely a runtime
// data structure for the duration of a single NewObject call).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Containers/TMap.h"
#include "XObject/XObject.h"

namespace XCore
{

    // -----------------------------------------------------------------
    // FObjectInstancingGraph -- archetype-to-instance mapping.
    //
    // Phase 5.d shipped surface (per spec §8.4):
    //
    //   * RegisterArchetype(Archetype, Instance) -- pair the two
    //     pointers in the map so subsequent lookups for `Archetype`
    //     return `Instance`.
    //   * GetInstance(Archetype) -- return the paired Instance for the
    //     given Archetype; returns nullptr if no pair was registered.
    //   * IsEmpty() -- predicate (true iff the map has no entries).
    //
    // The map uses XCore-4a's TMap<XObject*, XObject*> open-addressing
    // implementation. Hash + equality on `XObject*` are pointer-value
    // operations; the hot path is one cache miss per lookup against
    // the map's control bytes.
    //
    // OWNERSHIP: the graph stores raw pointers (no XPtr / XStrongPtr /
    // XWeakPtr); the lifetimes of the referenced XObjects are
    // guaranteed by the surrounding NewObject hot-path (the archetype
    // is the CDO, which is root-pinned; the instance is the just-
    // constructed XObject which is bound to the FXObjectArray BEFORE
    // CreateDefaultSubobject can fire). The graph is discarded at
    // ~FXObjectInitializer scope exit; no dangling-pointer hazard
    // beyond the FXObjectInitializer's lifetime.
    // -----------------------------------------------------------------

    class FObjectInstancingGraph
    {
    public:
        // -------------------------------------------------------------
        // Default ctor: empty map.
        //
        // The TMap default ctor initialises an empty open-addressing
        // table with zero capacity; the first insert triggers the
        // initial allocation. This is the cheap path for the common
        // case where the graph is created but never populated (a
        // NewObject<T> call with no CreateDefaultSubobject calls).
        // -------------------------------------------------------------
        FObjectInstancingGraph() noexcept = default;

        // -------------------------------------------------------------
        // Non-copyable, non-movable.
        //
        // The graph is owned by the FXObjectInitializer; the
        // initializer is itself non-copyable / non-movable (per spec
        // §8.4); the graph inherits the same discipline. Misuse is a
        // compile error.
        // -------------------------------------------------------------
        FObjectInstancingGraph(const FObjectInstancingGraph&)            = delete;
        FObjectInstancingGraph(FObjectInstancingGraph&&)                 = delete;
        FObjectInstancingGraph& operator=(const FObjectInstancingGraph&) = delete;
        FObjectInstancingGraph& operator=(FObjectInstancingGraph&&)      = delete;

        // -------------------------------------------------------------
        // Destructor: drop the map.
        //
        // TMap's destructor frees its backing storage (FMemTag::XObject).
        // The graph owns no XObjects + no external resources; the dtor
        // is structurally trivial beyond the TMap teardown.
        // -------------------------------------------------------------
        ~FObjectInstancingGraph() noexcept = default;

        // =============================================================
        // RegisterArchetype -- pair an archetype with its instance.
        //
        // After this call, GetInstance(Archetype) returns Instance.
        //
        // Pre-conditions:
        //   * Archetype != nullptr (the null sentinel is meaningless
        //     as an archetype; pre-Phase-2 we reject explicitly).
        //   * Instance MAY be nullptr (e.g., "this archetype maps to
        //     no instance in the current graph" -- the lookup will
        //     return nullptr indistinguishably from an unregistered
        //     archetype; the explicit nullptr-insert is supported for
        //     callers that want to record a "skip this sub-object"
        //     decision).
        //
        // If Archetype was previously registered, the registration is
        // OVERWRITTEN with the new Instance. The expected workflow is
        // a single registration per archetype per graph; overwriting
        // is supported for the rare deserialize re-entry path.
        // =============================================================
        void RegisterArchetype(XObject* Archetype, XObject* Instance) noexcept
        {
            XPACT_CHECK(Archetype != nullptr);
            m_archetypeToInstance.Add(Archetype, Instance);
        }

        // =============================================================
        // GetInstance -- look up the instance for a given archetype.
        //
        // Returns the registered Instance for Archetype, or nullptr if
        // no registration exists. The caller distinguishes "not
        // registered" from "registered with explicit nullptr" only by
        // calling Contains; the lookup itself returns nullptr for both
        // cases.
        //
        // The lookup is O(1) (TMap open-addressing); the hash + compare
        // are pointer-value operations.
        // =============================================================
        [[nodiscard]] XObject* GetInstance(XObject* Archetype) const noexcept
        {
            if (Archetype == nullptr)
            {
                return nullptr;
            }
            XObject* const* Found = m_archetypeToInstance.Find(Archetype);
            return (Found != nullptr) ? *Found : nullptr;
        }

        // =============================================================
        // Contains -- predicate for "this archetype has a registration".
        //
        // Distinguishes "not registered" from "registered with explicit
        // nullptr instance" (GetInstance alone cannot make the
        // distinction).
        // =============================================================
        [[nodiscard]] bool Contains(XObject* Archetype) const noexcept
        {
            if (Archetype == nullptr)
            {
                return false;
            }
            return m_archetypeToInstance.Find(Archetype) != nullptr;
        }

        // =============================================================
        // IsEmpty -- predicate for "no registrations".
        //
        // O(1) (reads the TMap's Num() counter).
        // =============================================================
        [[nodiscard]] bool IsEmpty() const noexcept
        {
            return m_archetypeToInstance.Num() == 0;
        }

        // =============================================================
        // Num -- the count of registered archetype-to-instance pairs.
        //
        // Diagnostic / test API. The map's Num() counter is updated by
        // every insert; the return is O(1).
        // =============================================================
        [[nodiscard]] ::int32 Num() const noexcept
        {
            return m_archetypeToInstance.Num();
        }

    private:
        // -------------------------------------------------------------
        // The archetype-to-instance map.
        //
        // Keys: archetype XObject* (typically a CDO's sub-object; never
        // null per RegisterArchetype's check).
        // Values: instance XObject* (typically the just-constructed
        // sub-object on the target instance; may be null for "skip
        // this sub-object" registrations).
        //
        // The TMap uses XCore-4a's SwissTable-style open-addressing
        // implementation (Containers/TMap.h); the per-entry storage is
        // TPair<XObject*, XObject*> = 16 bytes. At typical graph sizes
        // (1-20 archetype pairs per NewObject call) the map's load
        // factor stays low; the map fits in a single cache line per
        // bucket group.
        // -------------------------------------------------------------
        ::XCore::TMap<XObject*, XObject*> m_archetypeToInstance;
    };

    // -----------------------------------------------------------------
    // Trait locks.
    //
    // FObjectInstancingGraph is NOT trivially copyable (TMap holds
    // heap-allocated storage). It IS non-polymorphic (no virtual
    // methods) so the hot-reload commitment is preserved.
    //
    // No XPACT_*_LAYOUT_TAG: FObjectInstancingGraph is NOT in the ABI-
    // lock set (its size depends on the underlying TMap layout which
    // is internal to XCore-4a Phase 1d). The instancing graph is a
    // per-NewObject runtime data structure; cross-DLL stability is
    // not load-bearing.
    // -----------------------------------------------------------------
    static_assert(!::std::is_polymorphic_v<FObjectInstancingGraph>,
                  "FObjectInstancingGraph must NOT be polymorphic "
                  "(hot-reload commitment + FakeVTable dispatch pattern).");

} // namespace XCore
