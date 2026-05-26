// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FMapProperty.h -- reflected TMap<K, V> property
// (XCore-4b §5.5 + §11.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.5 + Section 11.2 row `FMapProperty: 128
// bytes (extends FProperty + KeyProp @ 104 + ValueProp @ 112 +
// MapFlags @ 120 + _pad @ 124)`.
//
// FMapProperty is the FProperty subclass for `TMap<K, V>` fields. The
// per-subclass payload is:
//
//   KeyProp    (FProperty* @ 104) -- per-key FProperty subclass describing K.
//   ValueProp  (FProperty* @ 112) -- per-value FProperty subclass describing V.
//   MapFlags   (uint32 @ 120)     -- 32-bit map-specific flag mask.
//   _pad       (4 bytes @ 124)    -- alignment pad to 8-byte boundary.
//
// SEMANTICS:
//
//   * The value slot in the owner struct holds a TMap<K, V> by-value.
//     TMap size is implementation-specific (16+ bytes per XCore-4a §5.1
//     SwissTable backing); ElementSize is populated at FClass::Link time
//     from the TMap header's compile-time size.
//   * ContainsObjectReference returns TRUE if EITHER KeyProp OR
//     ValueProp contains object refs.
//   * Identical / GetValueTypeHash walk the entries via KeyProp + ValueProp.
//
// CastFlag bit: kFMapProperty (0x20000) | kFProperty.
//
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // FMapProperty -- 128-byte FProperty subclass for TMap<K, V>.
    // -----------------------------------------------------------------
    struct alignas(8) FMapProperty : public FProperty
    {
        // ---- Per-subclass payload (offsets 104..127) ----

        FProperty* KeyProp;          // 104 +8   per-key FProperty
        FProperty* ValueProp;        // 112 +8   per-value FProperty
        ::uint32   MapFlags;         // 120 +4   map-specific flags
        ::uint32   _padMapPayload;   // 124 +4   alignment pad

        // ---- Construction ----

        constexpr FMapProperty() noexcept
            : FProperty()
            , KeyProp(nullptr)
            , ValueProp(nullptr)
            , MapFlags(0)
            , _padMapPayload(0)
        {
        }

        FMapProperty(FFieldVariant InOwner, FName InName,
                     FProperty* InKeyProp   = nullptr,
                     FProperty* InValueProp = nullptr) noexcept;

        // ---- FConstructFn target ----

        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;

        // ---- StaticClass hook for Cast<T> ----

        static const FFieldClass* StaticClass() noexcept;

        // ---- Per-subclass accessors ----

        [[nodiscard]] XPACT_FORCEINLINE FProperty* GetKeyProp() const noexcept
        {
            return KeyProp;
        }

        [[nodiscard]] XPACT_FORCEINLINE FProperty* GetValueProp() const noexcept
        {
            return ValueProp;
        }

        XPACT_FORCEINLINE void SetKeyProp(FProperty* InKey) noexcept
        {
            KeyProp = InKey;
        }

        XPACT_FORCEINLINE void SetValueProp(FProperty* InValue) noexcept
        {
            ValueProp = InValue;
        }

        [[nodiscard]] XPACT_FORCEINLINE ::uint32 GetMapFlags() const noexcept
        {
            return MapFlags;
        }

        XPACT_FORCEINLINE void SetMapFlags(::uint32 InFlags) noexcept
        {
            MapFlags = InFlags;
        }
    };

    // ABI lock.
    static_assert(sizeof(FMapProperty)  == 128,
                  "FMapProperty ABI lock: 128 bytes "
                  "(104 FProperty + 8 KeyProp + 8 ValueProp + 4 MapFlags + 4 pad)");
    static_assert(alignof(FMapProperty) == 8,
                  "FMapProperty ABI lock: 8-byte alignment");
    static_assert(offsetof(FMapProperty, KeyProp)   == 104,
                  "FMapProperty ABI lock: KeyProp @ offset 104");
    static_assert(offsetof(FMapProperty, ValueProp) == 112,
                  "FMapProperty ABI lock: ValueProp @ offset 112");
    static_assert(offsetof(FMapProperty, MapFlags)  == 120,
                  "FMapProperty ABI lock: MapFlags @ offset 120");
    static_assert(::std::is_trivially_copyable_v<FMapProperty>,
                  "FMapProperty must be trivially copyable");
    static_assert(::std::is_trivially_destructible_v<FMapProperty>,
                  "FMapProperty must be trivially destructible");

    extern const FFakeVTable kFMapPropertyFakeVTable;
    extern FFieldClass       kFMapPropertyStaticClass;
    const FFieldClass& GetFMapPropertyStaticClass() noexcept;

} // namespace XCore::Reflect
