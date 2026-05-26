// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FTextProperty.h -- reflected FText property (XCore-4b §5.5 + §11.2).
// CastFlag bit: kFTextProperty (0x1000000).
//
// Rev 2 MVP addition per FIX-3 -- FText is XCore-4a's localised-text
// type (XCore-4a §11.2). FTextProperty has NO subclass payload; the
// FText value is stored directly in the owner struct's slot.
//
// FText is NON-TRIVIALLY-COPYABLE (the lazy-resolved m_resolved
// FString carries dynamic memory). The dispatch slots use FText's
// typed operations.
//
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    struct alignas(8) FTextProperty : public FProperty
    {
        constexpr FTextProperty() noexcept = default;
        FTextProperty(FFieldVariant InOwner, FName InName) noexcept;

        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;
        static const FFieldClass* StaticClass() noexcept;
    };

    static_assert(sizeof(FTextProperty)  == 104, "FTextProperty ABI lock");
    static_assert(alignof(FTextProperty) == 8,   "FTextProperty alignment");
    static_assert(::std::is_trivially_copyable_v<FTextProperty>);
    static_assert(::std::is_trivially_destructible_v<FTextProperty>);

    extern const FFakeVTable kFTextPropertyFakeVTable;
    extern FFieldClass       kFTextPropertyStaticClass;
    const FFieldClass& GetFTextPropertyStaticClass() noexcept;

} // namespace XCore::Reflect
