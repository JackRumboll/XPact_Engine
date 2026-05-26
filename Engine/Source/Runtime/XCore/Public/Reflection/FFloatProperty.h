// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FFloatProperty.h -- reflected float property (XCore-4b §5.5 + §11.2).
// CastFlag bit: kFFloatProperty (0x200).
//
// Note: Identical uses bytewise comparison (NOT operator==) so NaN
// values compare consistently. UE FFloatProperty does the same.
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    struct alignas(8) FFloatProperty : public FProperty
    {
        constexpr FFloatProperty() noexcept = default;
        FFloatProperty(FFieldVariant InOwner, FName InName) noexcept;

        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;
        static const FFieldClass* StaticClass() noexcept;
    };

    static_assert(sizeof(FFloatProperty)  == 104, "FFloatProperty ABI lock");
    static_assert(alignof(FFloatProperty) == 8,   "FFloatProperty alignment");
    static_assert(::std::is_trivially_copyable_v<FFloatProperty>);
    static_assert(::std::is_trivially_destructible_v<FFloatProperty>);

    extern const FFakeVTable kFFloatPropertyFakeVTable;
    extern FFieldClass       kFFloatPropertyStaticClass;
    const FFieldClass& GetFFloatPropertyStaticClass() noexcept;

} // namespace XCore::Reflect
