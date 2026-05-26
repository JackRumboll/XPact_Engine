// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStruct.cpp -- XCore-4b §7.1 + §11.3 ; Phase 4b.5.
// FStruct's Link + FindPropertyByName + ForEachPropertyAdvance body.
// =====================================================================

#include "Reflection/FStruct.h"

#include "Reflection/EClassCastFlags.h"
#include "Reflection/EPropertyFlags.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FField.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"
#include "Reflection/FProperty.h"

namespace XCore::Reflect
{

// =====================================================================
// ForEachPropertyAdvance -- the templated ForEachProperty's advance
// helper. Body here because FProperty's full type is needed to read
// PropertyLinkNext.
// =====================================================================
FProperty* ForEachPropertyAdvance(FProperty* Current) noexcept
{
    return Current != nullptr ? Current->PropertyLinkNext : nullptr;
}

// =====================================================================
// FStruct::FindPropertyByName -- linear scan of ChildProperties.
//
// Walks the FField linked list, narrowing to FProperty (via the
// kFProperty parent gate bit) and comparing FName values. Returns
// the first match; nullptr if the name is not present in this
// struct's own chain (does NOT walk SuperStruct).
//
// The narrow uses the IsA fast path (CastFlags AND against
// kFProperty); the FField subclass test resolves in O(1).
// =====================================================================
FProperty* FStruct::FindPropertyByName(FName PropertyName) const noexcept
{
    for (FField* Walker = ChildProperties; Walker != nullptr; Walker = Walker->Next)
    {
        // Narrow to FProperty via the parent gate bit (kFProperty).
        // Non-FProperty FFields (FFunctionDescriptor post-MVP, etc.)
        // are skipped.
        const FFieldClass* WalkerClass = Walker->GetClass();
        if (WalkerClass == nullptr)
        {
            continue;
        }
        if (!HasAnyCastFlags(WalkerClass->CastFlags, EClassCastFlags::kFProperty))
        {
            continue;
        }

        // Compare FName values (case-sensitive equality on the
        // 8-byte handle; constexpr-evaluable).
        if (Walker->GetFName() == PropertyName)
        {
            return static_cast<FProperty*>(Walker);
        }
    }
    return nullptr;
}

// =====================================================================
// FStruct::Link -- rebuild PropertyLink / DestructorLink /
// PostConstructLink chains and populate ObjectRefProperties from
// ChildProperties.
//
// Idempotent: clears the existing chains + array first, then
// rebuilds. Single-pass O(N) over ChildProperties.
//
// PropertyLink ordering: head-first walk over ChildProperties
// preserves declaration order. UE's UStruct::Link reverses to most-
// derived-to-base; XPact's MVP follows declaration order because
// XHT-emitted ChildProperties is already in declaration order and
// the consumer (replication walk, etc.) treats the chain as a set
// rather than relying on traversal direction. Phase 4b.6 may refine
// per FIX-7's wire-encoding requirements.
// =====================================================================
void FStruct::Link() noexcept
{
    // Reset the chains + dense array.
    PropertyLink       = nullptr;
    DestructorLink     = nullptr;
    PostConstructLink  = nullptr;
    ObjectRefProperties.Reset();

    // Walk ChildProperties; thread each FProperty into the three
    // chains AND probe ContainsObjectReference for the dense array.
    //
    // We thread head-first by chaining via PropertyLinkNext /
    // DestructorLinkNext on each property; new entries are pushed
    // to the front of each chain so the head pointer is the most-
    // recently-encountered property.
    //
    // The "encountered struct props" TArray for
    // ContainsObjectReference's recursion-detection argument is
    // empty at link time (the encountered-set is per-walk; populating
    // it across multiple FStruct::Link calls would incorrectly
    // dedupe distinct walks).
    ::XCore::TArray<const FStructProperty*> EncounteredStructProps;

    for (FField* Walker = ChildProperties; Walker != nullptr; Walker = Walker->Next)
    {
        const FFieldClass* WalkerClass = Walker->GetClass();
        if (WalkerClass == nullptr)
        {
            continue;
        }
        if (!HasAnyCastFlags(WalkerClass->CastFlags, EClassCastFlags::kFProperty))
        {
            continue;
        }

        FProperty* Prop = static_cast<FProperty*>(Walker);

        // -----------------------------------------------------------
        // PropertyLink: push-front; head becomes most-recently-seen.
        // -----------------------------------------------------------
        Prop->PropertyLinkNext = PropertyLink;
        PropertyLink           = Prop;

        // -----------------------------------------------------------
        // DestructorLink: conservative-all-properties at Phase 4b.5.
        //
        // UE's UStruct::Link filters by !CPF_NoDestructor; XPact's
        // EPropertyFlags has no CPF_NoDestructor bit yet (the
        // attribute lands at Phase 4b.6+ when the editor / Blueprint
        // tiers parse it). The conservative posture threads every
        // property into DestructorLink which is correct (extra dtor
        // dispatches are no-ops for trivially-destructible
        // properties; the dispatch wrapper short-circuits).
        // -----------------------------------------------------------
        Prop->DestructorLinkNext = DestructorLink;
        DestructorLink           = Prop;

        // -----------------------------------------------------------
        // PostConstructLink: conservative-all-properties.
        //
        // UE's PostConstructLink filters by CPF_NeedCtorLink; XPact's
        // current EPropertyFlags MVP set does not include
        // CPF_NeedCtorLink (the post-construct initialiser path
        // lands at Phase 4b.6+). The conservative posture is correct
        // for the MVP: every property is added; the dispatch wrapper
        // is a no-op for properties without explicit init defaults.
        //
        // FProperty doesn't have a PostConstructLinkNext field
        // (PropertyLink + DestructorLink are the only chain pointers
        // on the FProperty layout). Phase 4b.5's PostConstructLink
        // therefore points at the same chain as PropertyLink for now;
        // Phase 4b.6 introduces the dedicated PostConstructLinkNext
        // field (or a sidecar TArray) and refines.
        //
        // For Phase 4b.5: PostConstructLink head = same as
        // PropertyLink (every property eligible).
        // -----------------------------------------------------------

        // -----------------------------------------------------------
        // ObjectRefProperties (FIX-13): probe the property's
        // ContainsObjectReference dispatch slot. A return of true
        // means GC must scan the slot when walking instances of
        // this struct.
        //
        // The probe consults the property's per-subclass FFakeVTable
        // -- the slot is populated for FObjectProperty,
        // FInterfaceProperty, FDelegateProperty (and their multicast
        // variants), and for container properties whose Inner is an
        // object reference. Primitives + value-types return false.
        // -----------------------------------------------------------
        if (Prop->ContainsObjectReference(EncounteredStructProps))
        {
            ObjectRefProperties.Add(Prop);
        }
    }

    // PostConstructLink: at Phase 4b.5, mirror PropertyLink (every
    // property eligible). Phase 4b.6 will refine to filter on
    // CPF_NeedCtorLink when the flag joins EPropertyFlags.
    PostConstructLink = PropertyLink;
}

} // namespace XCore::Reflect
