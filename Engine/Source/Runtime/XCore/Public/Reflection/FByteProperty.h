// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FByteProperty.h -- reflected uint8 / typed-byte-as-enum property
// (XCore-4b §5.5 + §11.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.5 + Section 11.2 row `FByteProperty: 112
// bytes (extends FProperty + UnderlyingEnum @ offset 104)`.
//
// FByteProperty is the FProperty subclass for `uint8`-typed reflected
// fields. The per-subclass payload is a single 8-byte pointer at
// offset 104 to an optional `FEnum` descriptor (the "typed-byte-as-
// enum" case where the byte holds a constrained enum-like value).
// `UnderlyingEnum == nullptr` is the plain-uint8 case.
//
// FEnum is forward-declared; the full type lands at Phase 4b.5. Phase
// 4b.4a callers must not dereference the pointer (only store + compare
// + null-check), which is what the FProperty subsystem itself needs.
//
// CastFlag bit: kFByteProperty (0x02).
//
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    // Forward declaration. FEnum (the runtime enum-descriptor type)
    // lands at Phase 4b.5; the FByteProperty stores a pointer to it
    // for the typed-byte-as-enum case.
    struct FEnum;

    // -----------------------------------------------------------------
    // FByteProperty -- 112-byte FProperty subclass for `uint8` (and
    // typed-byte-as-enum).
    // -----------------------------------------------------------------
    struct alignas(8) FByteProperty : public FProperty
    {
        // ---- Per-subclass payload (offset 104; 8 bytes) ----

        // The underlying FEnum descriptor for typed-byte-as-enum
        // properties, or nullptr for plain uint8. Forward-declared
        // (FEnum lands at Phase 4b.5); pointer storage + compare /
        // null-check only at Phase 4b.4a.
        FEnum* UnderlyingEnum;   // 104 +8

        // ---- Construction ----

        constexpr FByteProperty() noexcept
            : FProperty()
            , UnderlyingEnum(nullptr)
        {
        }

        FByteProperty(FFieldVariant InOwner, FName InName, FEnum* InUnderlyingEnum = nullptr) noexcept;

        // ---- FConstructFn target ----

        // Note: the FConstructFn signature takes (Owner, Name,
        // OutStorage). The UnderlyingEnum pointer is set separately
        // by the caller after construction (via direct field
        // assignment) -- the FConstructFn protocol does not accept
        // subclass-specific payload at construction time.
        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;

        // ---- StaticClass hook for Cast<T> ----

        static const FFieldClass* StaticClass() noexcept;

        // ---- Per-subclass accessors ----

        [[nodiscard]] XPACT_FORCEINLINE FEnum* GetUnderlyingEnum() const noexcept
        {
            return UnderlyingEnum;
        }

        XPACT_FORCEINLINE void SetUnderlyingEnum(FEnum* InEnum) noexcept
        {
            UnderlyingEnum = InEnum;
        }
    };

    // ABI lock.
    static_assert(sizeof(FByteProperty)  == 112,
                  "FByteProperty ABI lock: 112 bytes (104 FProperty + 8 UnderlyingEnum)");
    static_assert(alignof(FByteProperty) == 8,
                  "FByteProperty ABI lock: 8-byte alignment");
    static_assert(offsetof(FByteProperty, UnderlyingEnum) == 104,
                  "FByteProperty ABI lock: UnderlyingEnum @ offset 104");
    static_assert(::std::is_trivially_copyable_v<FByteProperty>,
                  "FByteProperty must be trivially copyable");
    static_assert(::std::is_trivially_destructible_v<FByteProperty>,
                  "FByteProperty must be trivially destructible");

    extern const FFakeVTable kFBytePropertyFakeVTable;
    extern FFieldClass       kFBytePropertyStaticClass;
    const FFieldClass& GetFBytePropertyStaticClass() noexcept;

} // namespace XCore::Reflect
