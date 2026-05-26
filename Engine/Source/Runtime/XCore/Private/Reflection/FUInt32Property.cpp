// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FUInt32Property.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4a. uint32 type.
// =====================================================================

#include "Reflection/FUInt32Property.h"

#include "Reflection/EClassCastFlags.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"
#include "Reflection/FNumericPropertyCommon.h"
#include "Reflection/FProperty.h"

#include <new>

namespace XCore::Reflect
{

constinit const FFakeVTable kFUInt32PropertyFakeVTable{
    /* Capabilities    */ Detail::kNumericCapabilities,
    /* _reservedHeader */ 0U,
    /* Slots           */ {
        (void(*)(void))&Detail::GetValueImpl<::uint32>,
        (void(*)(void))&Detail::SetValueImpl<::uint32>,
        (void(*)(void))&Detail::CopySingleValueImpl<::uint32>,
        (void(*)(void))&Detail::CopyCompleteValueImpl<::uint32>,
        (void(*)(void))&Detail::InitializeValueImpl<::uint32>,
        (void(*)(void))&Detail::DestroyValueImpl<::uint32>,
        (void(*)(void))&Detail::IdenticalImpl<::uint32>,
        nullptr, nullptr, nullptr,
        (void(*)(void))&Detail::ExportTextImpl<::uint32>,
        (void(*)(void))&Detail::ImportTextImpl<::uint32>,
        (void(*)(void))&Detail::GetValueTypeHashImpl<::uint32>,
        nullptr, nullptr,
    },
};

constinit FFieldClass kFUInt32PropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFUInt32Property | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FUInt32Property::ConstructFn,
    /* FakeVTable */ &kFUInt32PropertyFakeVTable,
};

FUInt32Property::FUInt32Property(FFieldVariant InOwner, FName InName) noexcept
    : FProperty(&kFUInt32PropertyStaticClass, InOwner, InName)
{
    ElementSize = static_cast<::int32>(sizeof(::uint32));
}

void FUInt32Property::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FUInt32Property(Owner, Name);
}

const FFieldClass* FUInt32Property::StaticClass() noexcept
{
    return &GetFUInt32PropertyStaticClass();
}

const FFieldClass& GetFUInt32PropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFUInt32PropertyStaticClass.Name = FName("FUInt32Property");
        return true;
    }();
    (void)Init;
    return kFUInt32PropertyStaticClass;
}

} // namespace XCore::Reflect
