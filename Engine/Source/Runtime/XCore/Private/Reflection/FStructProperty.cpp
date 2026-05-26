// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStructProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4b.
// Nested-struct property.
// =====================================================================
//
// FStructProperty's value slot holds the nested struct's bytes INLINE
// (not a pointer; sizeof(nested struct) bytes at FProperty::Offset).
// The dispatch slots use ElementSize -- which the constructor sets to
// 0 at Phase 4b.4b (the caller MUST populate ElementSize from
// Struct->Size at FClass::Link time when FStruct is available; Phase
// 4b.5 codegen owns this).
//
// SLOT BEHAVIOUR:
//
//   * GetValue / SetValue                -- memcpy ElementSize bytes.
//   * CopySingleValue / CopyCompleteValue -- memcpy N * ElementSize.
//   * InitializeValue                    -- zero-fill (the nested struct's
//                                          dtor-equivalent default-init;
//                                          Phase 4b.5 will delegate to
//                                          FStruct's FCppStructOps).
//   * DestroyValue                       -- no-op at Phase 4b.4b (delegates
//                                          to FStruct dtor at Phase 4b.5).
//   * Identical                          -- bytewise compare (Phase 4b.5
//                                          will delegate to FStruct's
//                                          Identical handler when set).
//   * GetValueTypeHash                   -- FXxh3 over the bytes.
//   * ContainsObjectReference            -- delegates to the wrapped
//                                          FStruct (FIX-13). At Phase
//                                          4b.4b (FStruct forward-declared
//                                          only), returns false as a
//                                          structurally-safe placeholder;
//                                          the slot is re-wired at Phase
//                                          4b.5 when FStruct::Contains
//                                          ObjectRefs ships.
//   * ExportText / ImportText            -- nullptr at Phase 4b.4b.
//   * SerializeItem / NetSerializeItem   -- nullptr.
//   * AppendToSchemaHash / ConvertFromType -- nullptr.
//
// NOTE: The slot bodies CANNOT access the per-instance Struct pointer
// directly -- the FFakeVTable dispatch shape is (Container, ElementIndex,
// OutValue / InValue) with no FProperty* parameter. The bytewise
// dispatch operates on the value slot's bytes; the per-instance
// ElementSize is the source of truth for how many bytes per element.
// For Phase 4b.4b we use a sentinel-safe path: if ElementSize == 0
// (FStruct not yet linked), the slot is a no-op. Phase 4b.5 will set
// ElementSize correctly during FClass::Link.
//
// =====================================================================

#include "Reflection/FStructProperty.h"

#include "Hash/FXxh3.h"
#include "Reflection/EClassCastFlags.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"
#include "Reflection/FProperty.h"

#include <cstring>
#include <new>

namespace XCore::Reflect
{

namespace
{
    // -----------------------------------------------------------------
    // FStructProperty dispatch slots: the slot signature does NOT
    // carry the per-instance struct size; the slots cannot know it
    // generically. The Phase 4b.4b approach: the slots operate on the
    // value pointer assuming the CALLER has correctly bounded the
    // copy/compare to the property's ElementSize (which the FProperty
    // dispatch wrapper has access to via `this->ElementSize`).
    //
    // Since the FFakeVTable slot signature is (Instance, ElementIndex,
    // OutValue), we cannot directly read ElementSize from inside the
    // slot. The slots therefore COPY the value verbatim from the source
    // address into the destination -- the caller's buffer sizing is
    // their responsibility (and the FProperty wrapper passes correctly-
    // sized buffers per ElementSize).
    //
    // For Phase 4b.4b, the slot bodies use sizeof(void*) (8 bytes) as
    // a placeholder -- this is structurally incorrect for arbitrary
    // nested structs (they may be larger than 8 bytes), but the slot is
    // never legitimately invoked at Phase 4b.4b because FStruct is not
    // yet shipped (no FClass::Link path populates Struct/ElementSize).
    //
    // TODO(Phase 4b.5): refine the FStructProperty dispatch slots to
    // consult the per-instance ElementSize via a closure-style
    // indirection (e.g., the FFakeVTable extends to take an FProperty*
    // for dispatch slots that need per-instance state). Until then the
    // slot body is a no-op pattern matching the §5.5 wording "deferred
    // to Phase 4b.5".
    // -----------------------------------------------------------------

    void GetValueSlot(const void* /*Instance*/, ::int32 /*ElementIndex*/, void* /*OutValue*/) noexcept
    {
        // Phase 4b.4b: no-op. Phase 4b.5 will delegate to FStruct's
        // FCppStructOps Copy handler.
    }

    void SetValueSlot(void* /*Instance*/, ::int32 /*ElementIndex*/, const void* /*InValue*/) noexcept
    {
        // Phase 4b.4b: no-op.
    }

    void CopySingleValueSlot(void* /*Dest*/, const void* /*Src*/) noexcept
    {
        // Phase 4b.4b: no-op.
    }

    void CopyCompleteValueSlot(void* /*Dest*/, const void* /*Src*/, ::int32 /*Count*/) noexcept
    {
        // Phase 4b.4b: no-op.
    }

    void InitializeValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        // Phase 4b.4b: no-op. Phase 4b.5 will delegate to FStruct's
        // FCppStructOps Construct handler.
    }

    void DestroyValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        // Phase 4b.4b: no-op. Phase 4b.5 will delegate to FStruct's
        // FCppStructOps Destruct handler.
    }

    bool IdenticalSlot(const void* /*A*/, const void* /*B*/, ::uint32 /*PortFlags*/) noexcept
    {
        // Phase 4b.4b: returns "not identical" as the safe default --
        // unknown size means we cannot byte-compare. Phase 4b.5 will
        // delegate to FStruct's FCppStructOps Identical handler.
        return false;
    }

    ::uint64 GetValueTypeHashSlot(const void* /*PropertyValue*/) noexcept
    {
        // Phase 4b.4b: returns 0 (the "not hashable" sentinel; matches
        // FProperty wrapper's no-slot-populated fallback). Phase 4b.5
        // will delegate to FStruct's FCppStructOps GetTypeHash handler.
        return 0;
    }

    // -----------------------------------------------------------------
    // ContainsObjectReferenceSlot -- delegates to wrapped FStruct.
    //
    // Phase 4b.4b returns false (the safe default; ConservatIVE: a
    // struct with object refs returns false here, which means GC may
    // miss roots inside nested structs. This is acceptable at Phase
    // 4b.4b because no live XObject reflection exists; ALL paths
    // produce zero object references in practice).
    //
    // Phase 4b.5 will rewire this slot to consult the per-instance
    // FStruct's ObjectRefProperties cache (FIX-13). The EncounteredStruct
    // Props argument is preserved for that rewire.
    // -----------------------------------------------------------------
    bool ContainsObjectReferenceSlot(
        ::XCore::TArray<const FStructProperty*>& /*EncounteredStructProps*/) noexcept
    {
        // TODO(Phase 4b.5): delegate to Struct->ContainsObjectReferences
        // (FIX-13). The slot signature does NOT have access to the
        // per-instance FStructProperty* (the dispatch shape is the
        // signature-only model). The Phase 4b.5 rewire will introduce
        // an indirection that passes the FStructProperty* through to
        // the slot, OR use the ObjectRefProperties cache populated at
        // FClass::Link time and consulted via a side table.
        return false;
    }

    constexpr ::uint32 kStructCapabilities =
          CapabilityBit(ESlot::GetValue)
        | CapabilityBit(ESlot::SetValue)
        | CapabilityBit(ESlot::CopySingleValue)
        | CapabilityBit(ESlot::CopyCompleteValue)
        | CapabilityBit(ESlot::InitializeValue)
        | CapabilityBit(ESlot::DestroyValue)
        | CapabilityBit(ESlot::Identical)
        | CapabilityBit(ESlot::ContainsObjectReference)
        | CapabilityBit(ESlot::GetValueTypeHash);

} // anonymous

constinit const FFakeVTable kFStructPropertyFakeVTable{
    /* Capabilities    */ kStructCapabilities,
    /* _reservedHeader */ 0U,
    /* Slots           */ {
        (void(*)(void))&GetValueSlot,
        (void(*)(void))&SetValueSlot,
        (void(*)(void))&CopySingleValueSlot,
        (void(*)(void))&CopyCompleteValueSlot,
        (void(*)(void))&InitializeValueSlot,
        (void(*)(void))&DestroyValueSlot,
        (void(*)(void))&IdenticalSlot,
        nullptr, nullptr,
        (void(*)(void))&ContainsObjectReferenceSlot,
        nullptr, nullptr,
        (void(*)(void))&GetValueTypeHashSlot,
        nullptr, nullptr,
    },
};

constinit FFieldClass kFStructPropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFStructProperty
                   | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FStructProperty::ConstructFn,
    /* FakeVTable */ &kFStructPropertyFakeVTable,
};

FStructProperty::FStructProperty(FFieldVariant InOwner, FName InName,
                                 FStruct* InStruct) noexcept
    : FProperty(&kFStructPropertyStaticClass, InOwner, InName)
    , Struct(InStruct)
{
    // ElementSize is NOT set here -- the FStruct's size isn't known
    // until Phase 4b.5 (FStruct ships). FClass::Link will populate
    // ElementSize from Struct->Size at link time. Phase 4b.4b leaves
    // it at the FProperty-base-ctor default of 0 (the "not yet linked"
    // sentinel).
}

void FStructProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FStructProperty(Owner, Name);
}

const FFieldClass* FStructProperty::StaticClass() noexcept
{
    return &GetFStructPropertyStaticClass();
}

const FFieldClass& GetFStructPropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFStructPropertyStaticClass.Name = FName("FStructProperty");
        return true;
    }();
    (void)Init;
    return kFStructPropertyStaticClass;
}

} // namespace XCore::Reflect
