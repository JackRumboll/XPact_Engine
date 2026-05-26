// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FNameProperty.h -- reflected FName property (XCore-4b §5.5 + §11.2).
// CastFlag bit: kFNameProperty (0x1000).
//
// FNameProperty has NO subclass payload -- the FName value is stored
// directly in the owner struct's 8-byte slot at FProperty::Offset.
//
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    struct alignas(8) FNameProperty : public FProperty
    {
        constexpr FNameProperty() noexcept = default;
        FNameProperty(FFieldVariant InOwner, FName InName) noexcept;

        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;
        static const FFieldClass* StaticClass() noexcept;
    };

    static_assert(sizeof(FNameProperty)  == 104, "FNameProperty ABI lock");
    static_assert(alignof(FNameProperty) == 8,   "FNameProperty alignment");
    static_assert(::std::is_trivially_copyable_v<FNameProperty>);
    static_assert(::std::is_trivially_destructible_v<FNameProperty>);

    extern const FFakeVTable kFNamePropertyFakeVTable;
    extern FFieldClass       kFNamePropertyStaticClass;
    const FFieldClass& GetFNamePropertyStaticClass() noexcept;

} // namespace XCore::Reflect
