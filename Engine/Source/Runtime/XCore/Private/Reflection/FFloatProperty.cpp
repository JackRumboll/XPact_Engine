// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FFloatProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4a. float type.
// =====================================================================

#include "Reflection/FFloatProperty.h"

#include "Reflection/EClassCastFlags.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"
#include "Reflection/FNumericPropertyCommon.h"
#include "Reflection/FProperty.h"

#include <new>

namespace XCore::Reflect
{

constinit const FFakeVTable kFFloatPropertyFakeVTable{
    /* Capabilities    */ Detail::kNumericCapabilities,
    /* _reservedHeader */ 0U,
    /* Slots           */ {
        (void(*)(void))&Detail::GetValueImpl<float>,
        (void(*)(void))&Detail::SetValueImpl<float>,
        (void(*)(void))&Detail::CopySingleValueImpl<float>,
        (void(*)(void))&Detail::CopyCompleteValueImpl<float>,
        (void(*)(void))&Detail::InitializeValueImpl<float>,
        (void(*)(void))&Detail::DestroyValueImpl<float>,
        (void(*)(void))&Detail::IdenticalImpl<float>,
        nullptr, nullptr, nullptr,
        (void(*)(void))&Detail::ExportTextImpl<float>,
        (void(*)(void))&Detail::ImportTextImpl<float>,
        (void(*)(void))&Detail::GetValueTypeHashImpl<float>,
        nullptr, nullptr,
    },
};

constinit FFieldClass kFFloatPropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFFloatProperty | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FFloatProperty::ConstructFn,
    /* FakeVTable */ &kFFloatPropertyFakeVTable,
};

FFloatProperty::FFloatProperty(FFieldVariant InOwner, FName InName) noexcept
    : FProperty(&kFFloatPropertyStaticClass, InOwner, InName)
{
    ElementSize = static_cast<::int32>(sizeof(float));
}

void FFloatProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FFloatProperty(Owner, Name);
}

const FFieldClass* FFloatProperty::StaticClass() noexcept
{
    return &GetFFloatPropertyStaticClass();
}

const FFieldClass& GetFFloatPropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFFloatPropertyStaticClass.Name = FName("FFloatProperty");
        return true;
    }();
    (void)Init;
    return kFFloatPropertyStaticClass;
}

} // namespace XCore::Reflect
