// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FUInt16Property.h -- reflected uint16 property (XCore-4b §5.5 + §11.2).
// CastFlag bit: kFUInt16Property (0x40).
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    struct alignas(8) FUInt16Property : public FProperty
    {
        constexpr FUInt16Property() noexcept = default;
        FUInt16Property(FFieldVariant InOwner, FName InName) noexcept;

        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;
        static const FFieldClass* StaticClass() noexcept;
    };

    static_assert(sizeof(FUInt16Property)  == 104, "FUInt16Property ABI lock");
    static_assert(alignof(FUInt16Property) == 8,   "FUInt16Property alignment");
    static_assert(::std::is_trivially_copyable_v<FUInt16Property>);
    static_assert(::std::is_trivially_destructible_v<FUInt16Property>);

    extern const FFakeVTable kFUInt16PropertyFakeVTable;
    extern FFieldClass       kFUInt16PropertyStaticClass;
    const FFieldClass& GetFUInt16PropertyStaticClass() noexcept;

} // namespace XCore::Reflect
