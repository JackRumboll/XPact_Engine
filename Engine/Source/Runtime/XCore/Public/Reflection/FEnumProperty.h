// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FEnumProperty.h -- reflected `enum class` property
// (XCore-4b §5.5 + §11.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.5 + Section 11.2 row `FEnumProperty: 120
// bytes (extends FProperty + UnderlyingProp @ 104 + Enum @ 112)`.
//
// FEnumProperty handles strongly-typed `enum class` properties. The
// per-subclass payload is:
//
//   UnderlyingProp  (FProperty* @ 104)  -- the integer-typed FProperty
//                                          subclass that does the
//                                          underlying read/write (e.g.,
//                                          a FByteProperty for
//                                          `enum class : uint8`).
//   Enum            (FEnum* @ 112)      -- the runtime descriptor for
//                                          the enum's named values.
//                                          Forward-declared; FEnum
//                                          lands at Phase 4b.5.
//
// Dispatch: GetValue / SetValue route to UnderlyingProp's typed slots.
// Identical compares underlying integer values. ExportText / ImportText
// would emit / parse the enumerator NAMES (via FEnum), but at Phase
// 4b.4a (FEnum not yet shipped) they fall back to the underlying-
// integer textual form via UnderlyingProp's dispatch slots.
//
// CastFlag bit: kFEnumProperty (0x80000).
//
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    // Forward declaration. FEnum (the runtime enum-descriptor type)
    // lands at Phase 4b.5; FEnumProperty stores a pointer to it.
    struct FEnum;

    // -----------------------------------------------------------------
    // FEnumProperty -- 120-byte FProperty subclass.
    // -----------------------------------------------------------------
    struct alignas(8) FEnumProperty : public FProperty
    {
        // ---- Per-subclass payload (offsets 104..119) ----

        // The underlying integer-typed FProperty subclass (e.g., an
        // FByteProperty for `enum class : uint8`, an FIntProperty for
        // `enum class : int32`). The FEnumProperty's dispatch slots
        // delegate to UnderlyingProp's slots for the integer read/write.
        FProperty* UnderlyingProp;   // 104 +8

        // The runtime enum descriptor. Forward-declared (FEnum lands at
        // Phase 4b.5); Phase 4b.4a callers store + null-check only.
        FEnum*     Enum;             // 112 +8

        // ---- Construction ----

        constexpr FEnumProperty() noexcept
            : FProperty()
            , UnderlyingProp(nullptr)
            , Enum(nullptr)
        {
        }

        FEnumProperty(FFieldVariant InOwner, FName InName,
                      FProperty* InUnderlyingProp = nullptr,
                      FEnum* InEnum = nullptr) noexcept;

        // ---- FConstructFn target ----

        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;

        // ---- StaticClass hook for Cast<T> ----

        static const FFieldClass* StaticClass() noexcept;

        // ---- Per-subclass accessors ----

        [[nodiscard]] XPACT_FORCEINLINE FProperty* GetUnderlyingProp() const noexcept
        {
            return UnderlyingProp;
        }

        [[nodiscard]] XPACT_FORCEINLINE FEnum* GetEnum() const noexcept
        {
            return Enum;
        }

        XPACT_FORCEINLINE void SetUnderlyingProp(FProperty* InProp) noexcept
        {
            UnderlyingProp = InProp;
        }

        XPACT_FORCEINLINE void SetEnum(FEnum* InEnum) noexcept
        {
            Enum = InEnum;
        }
    };

    // ABI lock.
    static_assert(sizeof(FEnumProperty)  == 120,
                  "FEnumProperty ABI lock: 120 bytes "
                  "(104 FProperty + 8 UnderlyingProp + 8 Enum)");
    static_assert(alignof(FEnumProperty) == 8,
                  "FEnumProperty ABI lock: 8-byte alignment");
    static_assert(offsetof(FEnumProperty, UnderlyingProp) == 104,
                  "FEnumProperty ABI lock: UnderlyingProp @ offset 104");
    static_assert(offsetof(FEnumProperty, Enum)           == 112,
                  "FEnumProperty ABI lock: Enum @ offset 112");
    static_assert(::std::is_trivially_copyable_v<FEnumProperty>);
    static_assert(::std::is_trivially_destructible_v<FEnumProperty>);

    extern const FFakeVTable kFEnumPropertyFakeVTable;
    extern FFieldClass       kFEnumPropertyStaticClass;
    const FFieldClass& GetFEnumPropertyStaticClass() noexcept;

} // namespace XCore::Reflect
