// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FInt64Property.h -- reflected int64 property (XCore-4b §5.5 + §11.2).
// =====================================================================
//
// CastFlag bit: kFInt64Property (0x20).
//
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    struct alignas(8) FInt64Property : public FProperty
    {
        constexpr FInt64Property() noexcept = default;
        FInt64Property(FFieldVariant InOwner, FName InName) noexcept;

        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;
        static const FFieldClass* StaticClass() noexcept;
    };

    static_assert(sizeof(FInt64Property)  == 104, "FInt64Property ABI lock");
    static_assert(alignof(FInt64Property) == 8,   "FInt64Property alignment");
    static_assert(::std::is_trivially_copyable_v<FInt64Property>);
    static_assert(::std::is_trivially_destructible_v<FInt64Property>);

    extern const FFakeVTable kFInt64PropertyFakeVTable;
    extern FFieldClass       kFInt64PropertyStaticClass;
    const FFieldClass& GetFInt64PropertyStaticClass() noexcept;

} // namespace XCore::Reflect
