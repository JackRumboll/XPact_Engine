// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FArrayProperty.h -- reflected TArray<T> property
// (XCore-4b §5.5 + §11.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.5 + Section 11.2 row `FArrayProperty: 120
// bytes (extends FProperty + Inner @ 104 + ArrayFlags @ 112 + _pad @
// 116)`.
//
// FArrayProperty is the FProperty subclass for `TArray<T>` fields.
// The per-subclass payload is:
//
//   Inner       (FProperty* @ 104)  -- the per-element FProperty
//                                      subclass describing T (e.g., an
//                                      FInt32Property if T is int32).
//   ArrayFlags  (uint32 @ 112)      -- 32-bit bitmask carrying
//                                      array-specific flags. Reserved
//                                      MVP bits; populated by XHT
//                                      codegen.
//   _pad        (4 bytes @ 116)     -- alignment pad to 8-byte
//                                      boundary so sizeof rounds to
//                                      120 cleanly under alignas(8).
//
// SEMANTICS:
//
//   * The value slot in the owner struct holds a TArray<T> by-value
//     (16 bytes per XCore-4a Rev 3 EBO -- the TArray header carries
//     Data + Num + Max).
//   * ContainsObjectReference DELEGATES to Inner->ContainsObjectReference:
//     a TArray<XObject*> contains object refs (via Inner being
//     FObjectProperty), a TArray<int32> does not.
//   * Identical / GetValueTypeHash walk the elements via Inner's
//     dispatch slots.
//
// CastFlag bit: kFArrayProperty (0x10000) | kFProperty.
//
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // FArrayProperty -- 120-byte FProperty subclass for TArray<T>.
    // -----------------------------------------------------------------
    struct alignas(8) FArrayProperty : public FProperty
    {
        // ---- Per-subclass payload (offsets 104..119) ----

        // The per-element FProperty subclass. Describes T in
        // TArray<T>. nullptr is structurally invalid; XHT codegen
        // populates this at FClass::Link time.
        FProperty* Inner;            // 104 +8

        // Array-specific flags. Phase 4b.4b reserves the field; flag
        // bits populated by later phases.
        ::uint32   ArrayFlags;       // 112 +4

        // Alignment pad to 8-byte boundary.
        ::uint32   _padArrayPayload; // 116 +4

        // ---- Construction ----

        constexpr FArrayProperty() noexcept
            : FProperty()
            , Inner(nullptr)
            , ArrayFlags(0)
            , _padArrayPayload(0)
        {
        }

        FArrayProperty(FFieldVariant InOwner, FName InName,
                       FProperty* InInner = nullptr) noexcept;

        // ---- FConstructFn target ----

        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;

        // ---- StaticClass hook for Cast<T> ----

        static const FFieldClass* StaticClass() noexcept;

        // ---- Per-subclass accessors ----

        [[nodiscard]] XPACT_FORCEINLINE FProperty* GetInner() const noexcept
        {
            return Inner;
        }

        XPACT_FORCEINLINE void SetInner(FProperty* InInner) noexcept
        {
            Inner = InInner;
        }

        [[nodiscard]] XPACT_FORCEINLINE ::uint32 GetArrayFlags() const noexcept
        {
            return ArrayFlags;
        }

        XPACT_FORCEINLINE void SetArrayFlags(::uint32 InFlags) noexcept
        {
            ArrayFlags = InFlags;
        }
    };

    // ABI lock.
    static_assert(sizeof(FArrayProperty)  == 120,
                  "FArrayProperty ABI lock: 120 bytes "
                  "(104 FProperty + 8 Inner + 4 ArrayFlags + 4 pad)");
    static_assert(alignof(FArrayProperty) == 8,
                  "FArrayProperty ABI lock: 8-byte alignment");
    static_assert(offsetof(FArrayProperty, Inner)      == 104,
                  "FArrayProperty ABI lock: Inner @ offset 104");
    static_assert(offsetof(FArrayProperty, ArrayFlags) == 112,
                  "FArrayProperty ABI lock: ArrayFlags @ offset 112");
    static_assert(::std::is_trivially_copyable_v<FArrayProperty>,
                  "FArrayProperty must be trivially copyable");
    static_assert(::std::is_trivially_destructible_v<FArrayProperty>,
                  "FArrayProperty must be trivially destructible");

    extern const FFakeVTable kFArrayPropertyFakeVTable;
    extern FFieldClass       kFArrayPropertyStaticClass;
    const FFieldClass& GetFArrayPropertyStaticClass() noexcept;

} // namespace XCore::Reflect
