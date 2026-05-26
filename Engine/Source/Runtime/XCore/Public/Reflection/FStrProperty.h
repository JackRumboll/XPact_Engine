// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FStrProperty.h -- reflected FString property (XCore-4b §5.5 + §11.2).
// CastFlag bit: kFStrProperty (0x800).
//
// FStrProperty has NO subclass payload -- the FString value is stored
// directly in the owner struct's 64-byte slot at FProperty::Offset.
//
// FString is NON-TRIVIALLY-COPYABLE (the heap-allocated path owns
// dynamic memory). The dispatch slots use FString's typed copy
// constructor / assignment / destructor rather than memcpy / memset
// to preserve the SSO + heap-promotion discipline.
//
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    struct alignas(8) FStrProperty : public FProperty
    {
        constexpr FStrProperty() noexcept = default;
        FStrProperty(FFieldVariant InOwner, FName InName) noexcept;

        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;
        static const FFieldClass* StaticClass() noexcept;
    };

    static_assert(sizeof(FStrProperty)  == 104, "FStrProperty ABI lock");
    static_assert(alignof(FStrProperty) == 8,   "FStrProperty alignment");
    static_assert(::std::is_trivially_copyable_v<FStrProperty>);
    static_assert(::std::is_trivially_destructible_v<FStrProperty>);

    extern const FFakeVTable kFStrPropertyFakeVTable;
    extern FFieldClass       kFStrPropertyStaticClass;
    const FFieldClass& GetFStrPropertyStaticClass() noexcept;

} // namespace XCore::Reflect
