// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FInt16Property.h -- reflected int16 property (XCore-4b §5.5 + §11.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.5 + Section 11.2 row `FInt16Property: 104
// bytes (no subclass payload)`. CastFlag bit: kFInt16Property (0x08).
//
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    struct alignas(8) FInt16Property : public FProperty
    {
        constexpr FInt16Property() noexcept = default;
        FInt16Property(FFieldVariant InOwner, FName InName) noexcept;

        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;
        static const FFieldClass* StaticClass() noexcept;
    };

    static_assert(sizeof(FInt16Property)  == 104, "FInt16Property ABI lock");
    static_assert(alignof(FInt16Property) == 8,   "FInt16Property alignment");
    static_assert(::std::is_trivially_copyable_v<FInt16Property>);
    static_assert(::std::is_trivially_destructible_v<FInt16Property>);

    extern const FFakeVTable kFInt16PropertyFakeVTable;
    extern FFieldClass       kFInt16PropertyStaticClass;
    const FFieldClass& GetFInt16PropertyStaticClass() noexcept;

} // namespace XCore::Reflect
