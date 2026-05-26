// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FIntProperty.h -- reflected int32 property (XCore-4b §5.5 + §11.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.5 + Section 11.2 row `FIntProperty: 104
// bytes (no subclass payload)`. CastFlag bit: kFIntProperty (0x10).
//
// Naming note: per spec §5.5 and UE convention, the int32 property
// subclass is named `FIntProperty` (NOT `FInt32Property`). The
// FInt-prefixed siblings carry explicit bit-widths.
//
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    struct alignas(8) FIntProperty : public FProperty
    {
        constexpr FIntProperty() noexcept = default;
        FIntProperty(FFieldVariant InOwner, FName InName) noexcept;

        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;
        static const FFieldClass* StaticClass() noexcept;
    };

    static_assert(sizeof(FIntProperty)  == 104, "FIntProperty ABI lock");
    static_assert(alignof(FIntProperty) == 8,   "FIntProperty alignment");
    static_assert(::std::is_trivially_copyable_v<FIntProperty>);
    static_assert(::std::is_trivially_destructible_v<FIntProperty>);

    extern const FFakeVTable kFIntPropertyFakeVTable;
    extern FFieldClass       kFIntPropertyStaticClass;
    const FFieldClass& GetFIntPropertyStaticClass() noexcept;

} // namespace XCore::Reflect
