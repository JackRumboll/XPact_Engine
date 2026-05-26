// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FIntProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4a. int32 type.
// =====================================================================

#include "Reflection/FIntProperty.h"

#include "Reflection/EClassCastFlags.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"
#include "Reflection/FNumericPropertyCommon.h"
#include "Reflection/FProperty.h"

#include <new>

namespace XCore::Reflect
{

constinit const FFakeVTable kFIntPropertyFakeVTable{
    /* Capabilities    */ Detail::kNumericCapabilities,
    /* _reservedHeader */ 0U,
    /* Slots           */ {
        (void(*)(void))&Detail::GetValueImpl<::int32>,
        (void(*)(void))&Detail::SetValueImpl<::int32>,
        (void(*)(void))&Detail::CopySingleValueImpl<::int32>,
        (void(*)(void))&Detail::CopyCompleteValueImpl<::int32>,
        (void(*)(void))&Detail::InitializeValueImpl<::int32>,
        (void(*)(void))&Detail::DestroyValueImpl<::int32>,
        (void(*)(void))&Detail::IdenticalImpl<::int32>,
        nullptr, nullptr, nullptr,
        (void(*)(void))&Detail::ExportTextImpl<::int32>,
        (void(*)(void))&Detail::ImportTextImpl<::int32>,
        (void(*)(void))&Detail::GetValueTypeHashImpl<::int32>,
        nullptr, nullptr,
    },
};

constinit FFieldClass kFIntPropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFIntProperty | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FIntProperty::ConstructFn,
    /* FakeVTable */ &kFIntPropertyFakeVTable,
};

FIntProperty::FIntProperty(FFieldVariant InOwner, FName InName) noexcept
    : FProperty(&kFIntPropertyStaticClass, InOwner, InName)
{
    ElementSize = static_cast<::int32>(sizeof(::int32));
}

void FIntProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FIntProperty(Owner, Name);
}

const FFieldClass* FIntProperty::StaticClass() noexcept
{
    return &GetFIntPropertyStaticClass();
}

const FFieldClass& GetFIntPropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFIntPropertyStaticClass.Name = FName("FIntProperty");
        return true;
    }();
    (void)Init;
    return kFIntPropertyStaticClass;
}

} // namespace XCore::Reflect
