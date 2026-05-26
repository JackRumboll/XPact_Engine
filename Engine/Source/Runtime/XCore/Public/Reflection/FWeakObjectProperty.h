// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FWeakObjectProperty.h -- reflected TWeakObjectPtr<T> property
// (XCore-4b §5.5 + §11.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.5 + Section 11.2 row `FWeakObjectProperty:
// 112 bytes (extends FProperty + PropertyClass @ offset 104)`.
//
// FWeakObjectProperty is the FProperty subclass for weak XObject
// pointer fields (`TWeakObjectPtr<T>` where T is XObject-derived).
// The per-subclass payload is a single 8-byte pointer to an `FClass*`
// (the runtime descriptor for the wrapped object type) -- same payload
// shape as FObjectProperty.
//
// SEMANTICS:
//
//   * Weak reference: the pointed-at XObject is NOT a GC root via
//     this property. The slot's stored FWeakObjectPtr survives the
//     pointed-at object's destruction (the next dereference observes
//     a null-resolved weak ref).
//   * NOT GC-aware as a strong reference: ContainsObjectReference
//     returns false (the slot does NOT contribute a GC root). The
//     GC walker still consults FWeakObjectProperty slots during the
//     weak-ref-fixup phase (see XCoreXObject System 5 for the full
//     fixup loop), but the slot's contained reference is NOT a root.
//   * Value type FWeakObjectPtr lives in the owner struct; the
//     FProperty subsystem treats the slot as an 8-byte payload.
//
// FClass FORWARD DECLARED -- the full FClass type lands at Phase 4b.5.
// FWeakObjectPtr placeholder declared in FWeakObjectPtr.h
// (8-byte value type; System 5 full impl).
//
// CastFlag bits: kFWeakObjectProperty (0x4000) | kFObjectPropertyBase
// (0x800000000) | kFProperty (parent gate).
//
// =====================================================================

#include "Reflection/FProperty.h"
#include "Reflection/FWeakObjectPtr.h"

namespace XCore::Reflect
{
    // Forward declaration. FClass lands at Phase 4b.5.
    struct FClass;

    // -----------------------------------------------------------------
    // FWeakObjectProperty -- 112-byte FProperty subclass for weak
    // XObject pointers.
    // -----------------------------------------------------------------
    struct alignas(8) FWeakObjectProperty : public FProperty
    {
        // ---- Per-subclass payload (offset 104; 8 bytes) ----

        // The runtime descriptor for the wrapped object type. Same
        // payload shape as FObjectProperty -- the discriminator is
        // the FFieldClass identity, not the payload layout.
        FClass* PropertyClass;       // 104 +8

        // ---- Construction ----

        constexpr FWeakObjectProperty() noexcept
            : FProperty()
            , PropertyClass(nullptr)
        {
        }

        FWeakObjectProperty(FFieldVariant InOwner, FName InName,
                            FClass* InPropertyClass = nullptr) noexcept;

        // ---- FConstructFn target ----

        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;

        // ---- StaticClass hook for Cast<T> ----

        static const FFieldClass* StaticClass() noexcept;

        // ---- Per-subclass accessors ----

        [[nodiscard]] XPACT_FORCEINLINE FClass* GetPropertyClass() const noexcept
        {
            return PropertyClass;
        }

        XPACT_FORCEINLINE void SetPropertyClass(FClass* InClass) noexcept
        {
            PropertyClass = InClass;
        }
    };

    // ABI lock.
    static_assert(sizeof(FWeakObjectProperty)  == 112,
                  "FWeakObjectProperty ABI lock: 112 bytes "
                  "(104 FProperty + 8 PropertyClass)");
    static_assert(alignof(FWeakObjectProperty) == 8,
                  "FWeakObjectProperty ABI lock: 8-byte alignment");
    static_assert(offsetof(FWeakObjectProperty, PropertyClass) == 104,
                  "FWeakObjectProperty ABI lock: PropertyClass @ offset 104");
    static_assert(::std::is_trivially_copyable_v<FWeakObjectProperty>,
                  "FWeakObjectProperty must be trivially copyable");
    static_assert(::std::is_trivially_destructible_v<FWeakObjectProperty>,
                  "FWeakObjectProperty must be trivially destructible");

    extern const FFakeVTable kFWeakObjectPropertyFakeVTable;
    extern FFieldClass       kFWeakObjectPropertyStaticClass;
    const FFieldClass& GetFWeakObjectPropertyStaticClass() noexcept;

} // namespace XCore::Reflect
