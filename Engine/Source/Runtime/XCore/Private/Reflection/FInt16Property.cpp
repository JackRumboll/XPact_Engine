// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FInt16Property.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4a.
// =====================================================================

#include "Reflection/FInt16Property.h"

#include "Reflection/EClassCastFlags.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"
#include "Reflection/FNumericPropertyCommon.h"
#include "Reflection/FProperty.h"

#include <new>

namespace XCore::Reflect
{

constinit const FFakeVTable kFInt16PropertyFakeVTable{
    /* Capabilities    */ Detail::kNumericCapabilities,
    /* _reservedHeader */ 0U,
    /* Slots           */ {
        (void(*)(void))&Detail::GetValueImpl<::int16>,
        (void(*)(void))&Detail::SetValueImpl<::int16>,
        (void(*)(void))&Detail::CopySingleValueImpl<::int16>,
        (void(*)(void))&Detail::CopyCompleteValueImpl<::int16>,
        (void(*)(void))&Detail::InitializeValueImpl<::int16>,
        (void(*)(void))&Detail::DestroyValueImpl<::int16>,
        (void(*)(void))&Detail::IdenticalImpl<::int16>,
        nullptr, nullptr, nullptr,
        (void(*)(void))&Detail::ExportTextImpl<::int16>,
        (void(*)(void))&Detail::ImportTextImpl<::int16>,
        (void(*)(void))&Detail::GetValueTypeHashImpl<::int16>,
        nullptr, nullptr,
    },
};

constinit FFieldClass kFInt16PropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFInt16Property | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FInt16Property::ConstructFn,
    /* FakeVTable */ &kFInt16PropertyFakeVTable,
};

FInt16Property::FInt16Property(FFieldVariant InOwner, FName InName) noexcept
    : FProperty(&kFInt16PropertyStaticClass, InOwner, InName)
{
    ElementSize = static_cast<::int32>(sizeof(::int16));
}

void FInt16Property::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FInt16Property(Owner, Name);
}

const FFieldClass* FInt16Property::StaticClass() noexcept
{
    return &GetFInt16PropertyStaticClass();
}

const FFieldClass& GetFInt16PropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFInt16PropertyStaticClass.Name = FName("FInt16Property");
        return true;
    }();
    (void)Init;
    return kFInt16PropertyStaticClass;
}

} // namespace XCore::Reflect
