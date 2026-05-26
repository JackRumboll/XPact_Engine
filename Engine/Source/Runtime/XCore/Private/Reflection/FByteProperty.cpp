// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FByteProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4a. uint8 +
// typed-byte-as-enum type.
// =====================================================================
//
// The dispatch slots reuse the Detail::*Impl<::uint8> shared numeric
// implementations. The UnderlyingEnum payload is a separate field;
// the dispatch slots themselves do NOT consult it (the runtime
// engine reads the byte value as-is; the FEnum descriptor is used
// at higher tiers to interpret the bits as a named enumerator).
//
// =====================================================================

#include "Reflection/FByteProperty.h"

#include "Reflection/EClassCastFlags.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"
#include "Reflection/FNumericPropertyCommon.h"
#include "Reflection/FProperty.h"

#include <new>

namespace XCore::Reflect
{

constinit const FFakeVTable kFBytePropertyFakeVTable{
    /* Capabilities    */ Detail::kNumericCapabilities,
    /* _reservedHeader */ 0U,
    /* Slots           */ {
        (void(*)(void))&Detail::GetValueImpl<::uint8>,
        (void(*)(void))&Detail::SetValueImpl<::uint8>,
        (void(*)(void))&Detail::CopySingleValueImpl<::uint8>,
        (void(*)(void))&Detail::CopyCompleteValueImpl<::uint8>,
        (void(*)(void))&Detail::InitializeValueImpl<::uint8>,
        (void(*)(void))&Detail::DestroyValueImpl<::uint8>,
        (void(*)(void))&Detail::IdenticalImpl<::uint8>,
        nullptr, nullptr, nullptr,
        (void(*)(void))&Detail::ExportTextImpl<::uint8>,
        (void(*)(void))&Detail::ImportTextImpl<::uint8>,
        (void(*)(void))&Detail::GetValueTypeHashImpl<::uint8>,
        nullptr, nullptr,
    },
};

constinit FFieldClass kFBytePropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFByteProperty | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FByteProperty::ConstructFn,
    /* FakeVTable */ &kFBytePropertyFakeVTable,
};

FByteProperty::FByteProperty(FFieldVariant InOwner, FName InName, FEnum* InUnderlyingEnum) noexcept
    : FProperty(&kFBytePropertyStaticClass, InOwner, InName)
    , UnderlyingEnum(InUnderlyingEnum)
{
    ElementSize = static_cast<::int32>(sizeof(::uint8));
}

void FByteProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    // The protocol-level ConstructFn cannot pass UnderlyingEnum;
    // callers must populate it via SetUnderlyingEnum() after
    // construction. Default to nullptr (plain uint8 case).
    new (OutStorage) FByteProperty(Owner, Name, nullptr);
}

const FFieldClass* FByteProperty::StaticClass() noexcept
{
    return &GetFBytePropertyStaticClass();
}

const FFieldClass& GetFBytePropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFBytePropertyStaticClass.Name = FName("FByteProperty");
        return true;
    }();
    (void)Init;
    return kFBytePropertyStaticClass;
}

} // namespace XCore::Reflect
