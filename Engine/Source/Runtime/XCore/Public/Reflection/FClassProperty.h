// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FClassProperty.h -- reflected FClass* (metaclass) property
// (XCore-4b §5.5 + §11.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.5 + Section 11.2 row `FClassProperty: 112
// bytes (extends FProperty + MetaClass @ offset 104)`.
//
// FClassProperty is the FProperty subclass for "class reference"
// fields -- the property's value is an `FClass*` (a metaclass; the
// runtime descriptor for some XObject subclass). UE's analogous type
// is `TSubclassOf<T>` which is a typed pointer to a UClass.
//
// SEMANTICS:
//
//   * Strong reference (the FClass is a singleton owned by the
//     reflection system; pointing at one keeps the descriptor live for
//     the engine's lifetime).
//   * ContainsObjectReference returns true (FClass is an XObject-
//     derived type at System 5; the slot IS an object reference).
//   * The MetaClass field bounds the wrapped value: the slot's
//     FClass* MUST be a child of MetaClass (e.g., MetaClass =
//     XActor's FClass; valid slots include XPawn / XCharacter; invalid
//     slots include XComponent).
//
// FClass FORWARD DECLARED -- the full FClass type lands at Phase 4b.5.
//
// XPact'S FLAT HIERARCHY (per spec §5.6 + FIX-R2-LOW-7): FClassProperty
// is a direct FProperty subclass (NOT inheriting from FObjectProperty
// like UE does). The shared "object-reference family" semantics are
// expressed via the kFObjectPropertyBase parent gate bit in CastFlags,
// not via class inheritance.
//
// CastFlag bits: kFClassProperty (0x200000) | kFObjectPropertyBase
// (0x800000000) | kFProperty (parent gate).
//
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    // Forward declaration. FClass lands at Phase 4b.5.
    struct FClass;

    // -----------------------------------------------------------------
    // FClassProperty -- 112-byte FProperty subclass for FClass*
    // (metaclass) references.
    // -----------------------------------------------------------------
    struct alignas(8) FClassProperty : public FProperty
    {
        // ---- Per-subclass payload (offset 104; 8 bytes) ----

        // The upper-bound class descriptor. The slot's stored FClass*
        // MUST be IsChildOf(MetaClass) (enforced by SetValue in the
        // typed accessor path; the bytewise dispatch slot does not
        // verify; the XHT codegen path is responsible for upholding
        // the constraint).
        FClass* MetaClass;           // 104 +8

        // ---- Construction ----

        constexpr FClassProperty() noexcept
            : FProperty()
            , MetaClass(nullptr)
        {
        }

        FClassProperty(FFieldVariant InOwner, FName InName,
                       FClass* InMetaClass = nullptr) noexcept;

        // ---- FConstructFn target ----

        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;

        // ---- StaticClass hook for Cast<T> ----

        static const FFieldClass* StaticClass() noexcept;

        // ---- Per-subclass accessors ----

        [[nodiscard]] XPACT_FORCEINLINE FClass* GetMetaClass() const noexcept
        {
            return MetaClass;
        }

        XPACT_FORCEINLINE void SetMetaClass(FClass* InClass) noexcept
        {
            MetaClass = InClass;
        }
    };

    // ABI lock.
    static_assert(sizeof(FClassProperty)  == 112,
                  "FClassProperty ABI lock: 112 bytes "
                  "(104 FProperty + 8 MetaClass)");
    static_assert(alignof(FClassProperty) == 8,
                  "FClassProperty ABI lock: 8-byte alignment");
    static_assert(offsetof(FClassProperty, MetaClass) == 104,
                  "FClassProperty ABI lock: MetaClass @ offset 104");
    static_assert(::std::is_trivially_copyable_v<FClassProperty>,
                  "FClassProperty must be trivially copyable");
    static_assert(::std::is_trivially_destructible_v<FClassProperty>,
                  "FClassProperty must be trivially destructible");

    extern const FFakeVTable kFClassPropertyFakeVTable;
    extern FFieldClass       kFClassPropertyStaticClass;
    const FFieldClass& GetFClassPropertyStaticClass() noexcept;

} // namespace XCore::Reflect
