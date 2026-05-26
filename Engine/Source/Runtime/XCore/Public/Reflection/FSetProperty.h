// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FSetProperty.h -- reflected TSet<T> property
// (XCore-4b §5.5 + §11.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.5 + Section 11.2 row `FSetProperty: 112
// bytes (extends FProperty + ElementProp @ offset 104)`.
//
// FSetProperty is the FProperty subclass for `TSet<T>` fields. The
// per-subclass payload is a single 8-byte pointer to the per-element
// FProperty subclass describing T (e.g., FInt32Property for
// TSet<int32>).
//
// SEMANTICS:
//
//   * The value slot in the owner struct holds a TSet<T> by-value.
//   * ContainsObjectReference delegates to ElementProp.
//   * Identical / GetValueTypeHash walk the entries via ElementProp.
//
// CastFlag bit: kFSetProperty (0x40000) | kFProperty.
//
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // FSetProperty -- 112-byte FProperty subclass for TSet<T>.
    // -----------------------------------------------------------------
    struct alignas(8) FSetProperty : public FProperty
    {
        // ---- Per-subclass payload (offset 104; 8 bytes) ----

        // The per-element FProperty subclass describing T. nullptr is
        // structurally invalid; XHT codegen populates this at
        // FClass::Link time.
        FProperty* ElementProp;      // 104 +8

        // ---- Construction ----

        constexpr FSetProperty() noexcept
            : FProperty()
            , ElementProp(nullptr)
        {
        }

        FSetProperty(FFieldVariant InOwner, FName InName,
                     FProperty* InElementProp = nullptr) noexcept;

        // ---- FConstructFn target ----

        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;

        // ---- StaticClass hook for Cast<T> ----

        static const FFieldClass* StaticClass() noexcept;

        // ---- Per-subclass accessors ----

        [[nodiscard]] XPACT_FORCEINLINE FProperty* GetElementProp() const noexcept
        {
            return ElementProp;
        }

        XPACT_FORCEINLINE void SetElementProp(FProperty* InProp) noexcept
        {
            ElementProp = InProp;
        }
    };

    // ABI lock.
    static_assert(sizeof(FSetProperty)  == 112,
                  "FSetProperty ABI lock: 112 bytes "
                  "(104 FProperty + 8 ElementProp)");
    static_assert(alignof(FSetProperty) == 8,
                  "FSetProperty ABI lock: 8-byte alignment");
    static_assert(offsetof(FSetProperty, ElementProp) == 104,
                  "FSetProperty ABI lock: ElementProp @ offset 104");
    static_assert(::std::is_trivially_copyable_v<FSetProperty>,
                  "FSetProperty must be trivially copyable");
    static_assert(::std::is_trivially_destructible_v<FSetProperty>,
                  "FSetProperty must be trivially destructible");

    extern const FFakeVTable kFSetPropertyFakeVTable;
    extern FFieldClass       kFSetPropertyStaticClass;
    const FFieldClass& GetFSetPropertyStaticClass() noexcept;

} // namespace XCore::Reflect
