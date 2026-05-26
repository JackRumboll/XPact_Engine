// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FClass.cpp -- XCore-4b §7.3 + §11.3; Phase 4b.5.
// FClass's FindPropertyByName + Link bodies.
// =====================================================================

#include "Reflection/FClass.h"

#include "Reflection/EClassCastFlags.h"
#include "Reflection/EPropertyFlags.h"
#include "Reflection/FProperty.h"
#include "Reflection/FRepRecord.h"
#include "Reflection/FStruct.h"

namespace XCore::Reflect
{

// =====================================================================
// FClass::FindPropertyByName -- hierarchical FStruct-chain lookup.
//
// Walks this class's local property chain first; on miss, recurses
// into SuperStruct->FindPropertyByName. For multi-level hierarchies
// the recursion depth is bounded (typical <=3, max <=10 per §13
// gate C1).
//
// The SuperStruct may not be an FClass (could be FScriptStruct or
// a plain FStruct in mixed hierarchies); we down-cast safely via
// the FStruct base accessor.
// =====================================================================
FProperty* FClass::FindPropertyByName(FName PropertyName) const noexcept
{
    // Local lookup via FStruct's chain walk.
    if (FProperty* Local = FStruct::FindPropertyByName(PropertyName))
    {
        return Local;
    }

    // Recurse into SuperStruct. The super may or may not be an
    // FClass; FStruct::FindPropertyByName works regardless.
    if (SuperStruct != nullptr)
    {
        return SuperStruct->FindPropertyByName(PropertyName);
    }
    return nullptr;
}

// =====================================================================
// FClass::Link -- FStruct::Link + ClassReps population per FIX-7.
//
// Two-phase:
//
//   Phase 1: FStruct::Link rebuilds PropertyLink chains and the
//   dense ObjectRefProperties array.
//
//   Phase 2: walk PropertyLink; for each FProperty with CPF_Net set,
//   emit ArrayDim FRepRecord entries into ClassReps. FirstOwnedClassRep
//   is the index of this class's first ClassReps entry; ClassRepCount
//   is the number of entries this class contributes (excluding entries
//   inherited from SuperStruct's ClassReps).
//
// At Phase 4b.5, SuperStruct's ClassReps may already be populated
// (the parent class was Linked first); FClass::Link APPENDS this
// class's entries. The FirstOwnedClassRep is the size BEFORE this
// class's contribution; ClassRepCount counts entries added.
//
// Note: the spec calls for ClassReps to be pre-emitted as constinit
// by XHT (FIX-R2-LOW-8), with Link verifying the constinit length
// matches the runtime chain. Phase 4b.5 ships the RUNTIME path
// (clear + rebuild) because XHT does not yet emit constinit
// ClassReps; the verification step lands at Phase 4b.6 alongside
// XReflectionRuntime + XHT .gen.cpp emission.
// =====================================================================
void FClass::Link() noexcept
{
    // Phase 1: FStruct::Link.
    FStruct::Link();

    // Phase 2: ClassReps. Start fresh for this class's contribution.
    // FirstOwnedClassRep points at the first slot we'll populate;
    // if SuperStruct is an FClass with existing ClassReps, we'd start
    // after those, but at Phase 4b.5 each FClass's ClassReps is
    // self-contained (no inheritance of ClassReps array; the
    // hierarchical replication index lookup happens via RepIndex
    // arithmetic across the chain).
    ClassReps.Reset();
    FirstOwnedClassRep = 0;
    ClassRepCount      = 0;

    // Walk the PropertyLink chain. For each property with CPF_Net,
    // emit ArrayDim entries (one per array element per FIX-7).
    for (FProperty* Walker = PropertyLink; Walker != nullptr; Walker = Walker->PropertyLinkNext)
    {
        // Skip non-replicated properties.
        if (!Walker->HasAnyPropertyFlags(EPropertyFlags::CPF_Net))
        {
            continue;
        }

        // ArrayDim controls how many ClassReps entries we emit. For
        // non-array properties ArrayDim == 1 (one entry); for
        // XPROPERTY(Replicated) float Stamina[4], ArrayDim == 4
        // (four entries, one per element per FIX-7).
        const ::int32 ArrayDim = Walker->GetArrayDim();
        for (::int32 Idx = 0; Idx < ArrayDim; ++Idx)
        {
            ClassReps.Add(FRepRecord(Walker, Idx));
            ++ClassRepCount;
        }
    }
}

} // namespace XCore::Reflect
