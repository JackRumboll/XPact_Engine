// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FObjectProperty.h -- reflected XObject* property
// (XCore-4b §5.5 + §11.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.5 + Section 11.2 row `FObjectProperty: 112
// bytes (extends FProperty + PropertyClass @ offset 104)`.
//
// FObjectProperty is the FProperty subclass for strong XObject pointer
// fields (`T*` where T is XObject-derived). The per-subclass payload
// is a single 8-byte pointer to an `FClass*` (the runtime descriptor
// for the wrapped object type).
//
// SEMANTICS:
//
//   * Strong reference: when populated, the pointed-at XObject is a
//     GC root via the owner struct's reflection walk.
//   * GC-aware: ContainsObjectReference returns true (the slot's
//     contained value IS an XObject reference; GC must scan it).
//   * The wrapped pointer's class descriptor (PropertyClass) is used
//     by the GC walker and serializer to discriminate between distinct
//     XObject subclasses pointed at by the same FProperty (every
//     FObjectProperty pointing at `XActor*` has the same PropertyClass).
//
// FClass FORWARD DECLARED -- the full FClass type lands at Phase 4b.5.
// Phase 4b.4b stores + compares + null-checks PropertyClass pointers
// but does not dereference them. The pointer value is opaque to the
// FProperty subsystem until Phase 4b.5.
//
// CastFlag bits: kFObjectProperty (0x2000) | kFObjectPropertyBase
// (0x800000000) | kFProperty (parent gate).
//
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    // Forward declaration. FClass lands at Phase 4b.5; Phase 4b.4b
    // stores + compares pointer values only.
    struct FClass;

    // Forward declaration. FObject is the GC-managed object base type
    // (lands at XCoreXObject / System 5). FObjectProperty's value slot
    // stores `FObject*`; the GetValue/SetValue dispatch slots operate
    // on 8-byte pointer payloads regardless of the FObject type's
    // visibility.
    struct FObject;

    // -----------------------------------------------------------------
    // FObjectProperty -- 112-byte FProperty subclass for strong
    // XObject pointers.
    // -----------------------------------------------------------------
    struct alignas(8) FObjectProperty : public FProperty
    {
        // ---- Per-subclass payload (offset 104; 8 bytes) ----

        // The runtime descriptor for the wrapped object type. nullptr
        // sentinel for unbound / abstract; populated by the XHT codegen
        // path at FClass::Link time with the concrete FClass* of the
        // declared template parameter (e.g., `XActor*` -> &XActor's
        // FClass).
        FClass* PropertyClass;       // 104 +8

        // ---- Construction ----

        constexpr FObjectProperty() noexcept
            : FProperty()
            , PropertyClass(nullptr)
        {
        }

        FObjectProperty(FFieldVariant InOwner, FName InName,
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
    static_assert(sizeof(FObjectProperty)  == 112,
                  "FObjectProperty ABI lock: 112 bytes "
                  "(104 FProperty + 8 PropertyClass)");
    static_assert(alignof(FObjectProperty) == 8,
                  "FObjectProperty ABI lock: 8-byte alignment");
    static_assert(offsetof(FObjectProperty, PropertyClass) == 104,
                  "FObjectProperty ABI lock: PropertyClass @ offset 104");
    static_assert(::std::is_trivially_copyable_v<FObjectProperty>,
                  "FObjectProperty must be trivially copyable");
    static_assert(::std::is_trivially_destructible_v<FObjectProperty>,
                  "FObjectProperty must be trivially destructible");

    extern const FFakeVTable kFObjectPropertyFakeVTable;
    extern FFieldClass       kFObjectPropertyStaticClass;
    const FFieldClass& GetFObjectPropertyStaticClass() noexcept;

} // namespace XCore::Reflect
