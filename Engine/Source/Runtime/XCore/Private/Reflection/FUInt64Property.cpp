// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FUInt64Property.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4a. uint64 type.
// =====================================================================

#include "Reflection/FUInt64Property.h"

#include "Reflection/EClassCastFlags.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"
#include "Reflection/FNumericPropertyCommon.h"
#include "Reflection/FProperty.h"

#include <new>

namespace XCore::Reflect
{

constinit const FFakeVTable kFUInt64PropertyFakeVTable{
    /* Capabilities    */ Detail::kNumericCapabilities,
    /* _reservedHeader */ 0U,
    /* Slots           */ {
        (void(*)(void))&Detail::GetValueImpl<::uint64>,
        (void(*)(void))&Detail::SetValueImpl<::uint64>,
        (void(*)(void))&Detail::CopySingleValueImpl<::uint64>,
        (void(*)(void))&Detail::CopyCompleteValueImpl<::uint64>,
        (void(*)(void))&Detail::InitializeValueImpl<::uint64>,
        (void(*)(void))&Detail::DestroyValueImpl<::uint64>,
        (void(*)(void))&Detail::IdenticalImpl<::uint64>,
        nullptr, nullptr, nullptr,
        (void(*)(void))&Detail::ExportTextImpl<::uint64>,
        (void(*)(void))&Detail::ImportTextImpl<::uint64>,
        (void(*)(void))&Detail::GetValueTypeHashImpl<::uint64>,
        nullptr, nullptr,
    },
};

constinit FFieldClass kFUInt64PropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFUInt64Property | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FUInt64Property::ConstructFn,
    /* FakeVTable */ &kFUInt64PropertyFakeVTable,
};

FUInt64Property::FUInt64Property(FFieldVariant InOwner, FName InName) noexcept
    : FProperty(&kFUInt64PropertyStaticClass, InOwner, InName)
{
    ElementSize = static_cast<::int32>(sizeof(::uint64));
}

void FUInt64Property::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FUInt64Property(Owner, Name);
}

const FFieldClass* FUInt64Property::StaticClass() noexcept
{
    return &GetFUInt64PropertyStaticClass();
}

const FFieldClass& GetFUInt64PropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFUInt64PropertyStaticClass.Name = FName("FUInt64Property");
        return true;
    }();
    (void)Init;
    return kFUInt64PropertyStaticClass;
}

} // namespace XCore::Reflect
