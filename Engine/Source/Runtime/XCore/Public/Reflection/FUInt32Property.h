// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FUInt32Property.h -- reflected uint32 property (XCore-4b §5.5 + §11.2).
// CastFlag bit: kFUInt32Property (0x80).
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    struct alignas(8) FUInt32Property : public FProperty
    {
        constexpr FUInt32Property() noexcept = default;
        FUInt32Property(FFieldVariant InOwner, FName InName) noexcept;

        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;
        static const FFieldClass* StaticClass() noexcept;
    };

    static_assert(sizeof(FUInt32Property)  == 104, "FUInt32Property ABI lock");
    static_assert(alignof(FUInt32Property) == 8,   "FUInt32Property alignment");
    static_assert(::std::is_trivially_copyable_v<FUInt32Property>);
    static_assert(::std::is_trivially_destructible_v<FUInt32Property>);

    extern const FFakeVTable kFUInt32PropertyFakeVTable;
    extern FFieldClass       kFUInt32PropertyStaticClass;
    const FFieldClass& GetFUInt32PropertyStaticClass() noexcept;

} // namespace XCore::Reflect
