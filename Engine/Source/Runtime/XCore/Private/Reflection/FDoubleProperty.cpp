// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FDoubleProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4a. double type.
// =====================================================================

#include "Reflection/FDoubleProperty.h"

#include "Reflection/EClassCastFlags.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"
#include "Reflection/FNumericPropertyCommon.h"
#include "Reflection/FProperty.h"

#include <new>

namespace XCore::Reflect
{

constinit const FFakeVTable kFDoublePropertyFakeVTable{
    /* Capabilities    */ Detail::kNumericCapabilities,
    /* _reservedHeader */ 0U,
    /* Slots           */ {
        (void(*)(void))&Detail::GetValueImpl<double>,
        (void(*)(void))&Detail::SetValueImpl<double>,
        (void(*)(void))&Detail::CopySingleValueImpl<double>,
        (void(*)(void))&Detail::CopyCompleteValueImpl<double>,
        (void(*)(void))&Detail::InitializeValueImpl<double>,
        (void(*)(void))&Detail::DestroyValueImpl<double>,
        (void(*)(void))&Detail::IdenticalImpl<double>,
        nullptr, nullptr, nullptr,
        (void(*)(void))&Detail::ExportTextImpl<double>,
        (void(*)(void))&Detail::ImportTextImpl<double>,
        (void(*)(void))&Detail::GetValueTypeHashImpl<double>,
        nullptr, nullptr,
    },
};

constinit FFieldClass kFDoublePropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFDoubleProperty | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FDoubleProperty::ConstructFn,
    /* FakeVTable */ &kFDoublePropertyFakeVTable,
};

FDoubleProperty::FDoubleProperty(FFieldVariant InOwner, FName InName) noexcept
    : FProperty(&kFDoublePropertyStaticClass, InOwner, InName)
{
    ElementSize = static_cast<::int32>(sizeof(double));
}

void FDoubleProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FDoubleProperty(Owner, Name);
}

const FFieldClass* FDoubleProperty::StaticClass() noexcept
{
    return &GetFDoublePropertyStaticClass();
}

const FFieldClass& GetFDoublePropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFDoublePropertyStaticClass.Name = FName("FDoubleProperty");
        return true;
    }();
    (void)Init;
    return kFDoublePropertyStaticClass;
}

} // namespace XCore::Reflect
