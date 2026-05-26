// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FInt8Property.cpp -- FInt8Property definitions + per-subclass
// FFakeVTable + FFieldClass (XCore-4b §5.5 + §11.2; Phase 4b.4a).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.5 ("FProperty subclass family") +
// Section 11.2 layout row `FInt8Property: 104 bytes`.
//
// This translation unit anchors:
//
//   1. The per-subclass FFakeVTable in `.rodata` with the shared
//      numeric dispatch slots from Detail::*Impl<int8>.
//   2. The per-subclass FFieldClass with SuperClass pointing at
//      &kFPropertyStaticClass and CastFlags including kFInt8Property
//      | kFProperty.
//   3. The FInt8Property::ConstructFn placement-new target.
//   4. The StaticClass + GetFInt8PropertyStaticClass accessor pair.
//
// =====================================================================

#include "Reflection/FInt8Property.h"

#include "Reflection/EClassCastFlags.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"
#include "Reflection/FNumericPropertyCommon.h"
#include "Reflection/FProperty.h"

#include <new>

namespace XCore::Reflect
{

// =====================================================================
// kFInt8PropertyFakeVTable -- the shared dispatch table for int8.
//
// Lives in `.rodata` (constinit const). The 13 populated slots use
// the Detail::*Impl<::int8> template instantiations. Empty slots:
// SerializeItem (7), NetSerializeItem (8), ContainsObjectReference (9),
// AppendToSchemaHash (13), ConvertFromType (14) -- per Phase 4b.4a
// scope.
//
// The function-pointer slot population uses C-style casts to
// `void(*)(void)`. The casts are reinterpret_cast in disguise and
// are NOT strictly constant expressions per [expr.const]/5.10, but
// every major compiler accepts them in constinit scope because the
// storage bytes are the function's address verbatim. The spec §5.4
// example uses the same pattern.
// =====================================================================
constinit const FFakeVTable kFInt8PropertyFakeVTable{
    /* Capabilities    */ Detail::kNumericCapabilities,
    /* _reservedHeader */ 0U,
    /* Slots           */ {
        (void(*)(void))&Detail::GetValueImpl<::int8>,
        (void(*)(void))&Detail::SetValueImpl<::int8>,
        (void(*)(void))&Detail::CopySingleValueImpl<::int8>,
        (void(*)(void))&Detail::CopyCompleteValueImpl<::int8>,
        (void(*)(void))&Detail::InitializeValueImpl<::int8>,
        (void(*)(void))&Detail::DestroyValueImpl<::int8>,
        (void(*)(void))&Detail::IdenticalImpl<::int8>,
        nullptr,                                                // 7  SerializeItem
        nullptr,                                                // 8  NetSerializeItem
        nullptr,                                                // 9  ContainsObjectReference
        (void(*)(void))&Detail::ExportTextImpl<::int8>,
        (void(*)(void))&Detail::ImportTextImpl<::int8>,
        (void(*)(void))&Detail::GetValueTypeHashImpl<::int8>,
        nullptr,                                                // 13 AppendToSchemaHash
        nullptr,                                                // 14 ConvertFromType
    },
};

// =====================================================================
// kFInt8PropertyStaticClass -- the FFieldClass descriptor.
//
// constinit; Name slot lazy-initialised by GetFInt8PropertyStaticClass()
// (same pattern as kFieldStaticClass in Phase 4b.3 and
// kFPropertyStaticClass earlier in Phase 4b.4a).
//
// CastFlags: kFInt8Property | kFProperty. The OR-include of the
// parent gate bit lets the IsA fast path resolve "is this any
// FProperty?" in a single AND.
// =====================================================================
constinit FFieldClass kFInt8PropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFInt8Property | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FInt8Property::ConstructFn,
    /* FakeVTable */ &kFInt8PropertyFakeVTable,
};

// =====================================================================
// FInt8Property ctor.
// =====================================================================
FInt8Property::FInt8Property(FFieldVariant InOwner, FName InName) noexcept
    : FProperty(&kFInt8PropertyStaticClass, InOwner, InName)
{
    // ElementSize defaults to 0 in the FProperty base ctor; populate
    // it here to sizeof(::int8) so the wrapper's ContainerPtrToValuePtr
    // arithmetic works for ArrayDim > 1.
    ElementSize = static_cast<::int32>(sizeof(::int8));
}

// =====================================================================
// FInt8Property::ConstructFn -- placement-new target.
// =====================================================================
void FInt8Property::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FInt8Property(Owner, Name);
}

// =====================================================================
// FInt8Property::StaticClass + GetFInt8PropertyStaticClass.
// =====================================================================
const FFieldClass* FInt8Property::StaticClass() noexcept
{
    return &GetFInt8PropertyStaticClass();
}

const FFieldClass& GetFInt8PropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFInt8PropertyStaticClass.Name = FName("FInt8Property");
        return true;
    }();
    (void)Init;
    return kFInt8PropertyStaticClass;
}

} // namespace XCore::Reflect
