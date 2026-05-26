// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FDoubleProperty.h -- reflected double property (XCore-4b §5.5 + §11.2).
// CastFlag bit: kFDoubleProperty (0x400).
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    struct alignas(8) FDoubleProperty : public FProperty
    {
        constexpr FDoubleProperty() noexcept = default;
        FDoubleProperty(FFieldVariant InOwner, FName InName) noexcept;

        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;
        static const FFieldClass* StaticClass() noexcept;
    };

    static_assert(sizeof(FDoubleProperty)  == 104, "FDoubleProperty ABI lock");
    static_assert(alignof(FDoubleProperty) == 8,   "FDoubleProperty alignment");
    static_assert(::std::is_trivially_copyable_v<FDoubleProperty>);
    static_assert(::std::is_trivially_destructible_v<FDoubleProperty>);

    extern const FFakeVTable kFDoublePropertyFakeVTable;
    extern FFieldClass       kFDoublePropertyStaticClass;
    const FFieldClass& GetFDoublePropertyStaticClass() noexcept;

} // namespace XCore::Reflect
