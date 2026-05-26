// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FUInt64Property.h -- reflected uint64 property (XCore-4b §5.5 + §11.2).
// CastFlag bit: kFUInt64Property (0x100).
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    struct alignas(8) FUInt64Property : public FProperty
    {
        constexpr FUInt64Property() noexcept = default;
        FUInt64Property(FFieldVariant InOwner, FName InName) noexcept;

        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;
        static const FFieldClass* StaticClass() noexcept;
    };

    static_assert(sizeof(FUInt64Property)  == 104, "FUInt64Property ABI lock");
    static_assert(alignof(FUInt64Property) == 8,   "FUInt64Property alignment");
    static_assert(::std::is_trivially_copyable_v<FUInt64Property>);
    static_assert(::std::is_trivially_destructible_v<FUInt64Property>);

    extern const FFakeVTable kFUInt64PropertyFakeVTable;
    extern FFieldClass       kFUInt64PropertyStaticClass;
    const FFieldClass& GetFUInt64PropertyStaticClass() noexcept;

} // namespace XCore::Reflect
