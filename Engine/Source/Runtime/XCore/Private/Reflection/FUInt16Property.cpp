// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FUInt16Property.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4a. uint16 type.
// =====================================================================

#include "Reflection/FUInt16Property.h"

#include "Reflection/EClassCastFlags.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"
#include "Reflection/FNumericPropertyCommon.h"
#include "Reflection/FProperty.h"

#include <new>

namespace XCore::Reflect
{

constinit const FFakeVTable kFUInt16PropertyFakeVTable{
    /* Capabilities    */ Detail::kNumericCapabilities,
    /* _reservedHeader */ 0U,
    /* Slots           */ {
        (void(*)(void))&Detail::GetValueImpl<::uint16>,
        (void(*)(void))&Detail::SetValueImpl<::uint16>,
        (void(*)(void))&Detail::CopySingleValueImpl<::uint16>,
        (void(*)(void))&Detail::CopyCompleteValueImpl<::uint16>,
        (void(*)(void))&Detail::InitializeValueImpl<::uint16>,
        (void(*)(void))&Detail::DestroyValueImpl<::uint16>,
        (void(*)(void))&Detail::IdenticalImpl<::uint16>,
        nullptr, nullptr, nullptr,
        (void(*)(void))&Detail::ExportTextImpl<::uint16>,
        (void(*)(void))&Detail::ImportTextImpl<::uint16>,
        (void(*)(void))&Detail::GetValueTypeHashImpl<::uint16>,
        nullptr, nullptr,
    },
};

constinit FFieldClass kFUInt16PropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFUInt16Property | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FUInt16Property::ConstructFn,
    /* FakeVTable */ &kFUInt16PropertyFakeVTable,
};

FUInt16Property::FUInt16Property(FFieldVariant InOwner, FName InName) noexcept
    : FProperty(&kFUInt16PropertyStaticClass, InOwner, InName)
{
    ElementSize = static_cast<::int32>(sizeof(::uint16));
}

void FUInt16Property::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FUInt16Property(Owner, Name);
}

const FFieldClass* FUInt16Property::StaticClass() noexcept
{
    return &GetFUInt16PropertyStaticClass();
}

const FFieldClass& GetFUInt16PropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFUInt16PropertyStaticClass.Name = FName("FUInt16Property");
        return true;
    }();
    (void)Init;
    return kFUInt16PropertyStaticClass;
}

} // namespace XCore::Reflect
