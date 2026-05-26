// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FInt64Property.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4a. int64 type.
// =====================================================================

#include "Reflection/FInt64Property.h"

#include "Reflection/EClassCastFlags.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"
#include "Reflection/FNumericPropertyCommon.h"
#include "Reflection/FProperty.h"

#include <new>

namespace XCore::Reflect
{

constinit const FFakeVTable kFInt64PropertyFakeVTable{
    /* Capabilities    */ Detail::kNumericCapabilities,
    /* _reservedHeader */ 0U,
    /* Slots           */ {
        (void(*)(void))&Detail::GetValueImpl<::int64>,
        (void(*)(void))&Detail::SetValueImpl<::int64>,
        (void(*)(void))&Detail::CopySingleValueImpl<::int64>,
        (void(*)(void))&Detail::CopyCompleteValueImpl<::int64>,
        (void(*)(void))&Detail::InitializeValueImpl<::int64>,
        (void(*)(void))&Detail::DestroyValueImpl<::int64>,
        (void(*)(void))&Detail::IdenticalImpl<::int64>,
        nullptr, nullptr, nullptr,
        (void(*)(void))&Detail::ExportTextImpl<::int64>,
        (void(*)(void))&Detail::ImportTextImpl<::int64>,
        (void(*)(void))&Detail::GetValueTypeHashImpl<::int64>,
        nullptr, nullptr,
    },
};

constinit FFieldClass kFInt64PropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFInt64Property | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FInt64Property::ConstructFn,
    /* FakeVTable */ &kFInt64PropertyFakeVTable,
};

FInt64Property::FInt64Property(FFieldVariant InOwner, FName InName) noexcept
    : FProperty(&kFInt64PropertyStaticClass, InOwner, InName)
{
    ElementSize = static_cast<::int32>(sizeof(::int64));
}

void FInt64Property::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FInt64Property(Owner, Name);
}

const FFieldClass* FInt64Property::StaticClass() noexcept
{
    return &GetFInt64PropertyStaticClass();
}

const FFieldClass& GetFInt64PropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFInt64PropertyStaticClass.Name = FName("FInt64Property");
        return true;
    }();
    (void)Init;
    return kFInt64PropertyStaticClass;
}

} // namespace XCore::Reflect
