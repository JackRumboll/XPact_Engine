// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FSoftClassProperty.h -- reflected TSoftClassPtr<T> property
// (XCore-4b §5.5 + §11.2; Rev 2 MVP add per FIX-3).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.5 + Section 11.2 row `FSoftClassProperty:
// 112 bytes (extends FProperty + MetaClass @ offset 104)`.
//
// FSoftClassProperty is the FProperty subclass for soft class
// references (`TSoftClassPtr<T>`) -- the property's value is a soft
// reference (FSoftObjectPath-shaped) that resolves on demand to an
// FClass*. Use case: XScenario presets, designer-authored data assets
// that hold class references that load on demand.
//
// SEMANTICS:
//
//   * NOT a GC root: ContainsObjectReference returns false. The
//     referenced class loads on demand via the asset system.
//   * The MetaClass field bounds the wrapped value: the slot's
//     soft-resolved FClass* MUST be IsChildOf(MetaClass).
//   * The value slot holds an FSoftObjectPath value (8 bytes; same
//     as FSoftObjectProperty).
//
// FClass FORWARD DECLARED -- the full FClass type lands at Phase 4b.5.
// FSoftObjectPath placeholder declared in FSoftObjectPath.h.
//
// CastFlag bits: kFSoftClassProperty (0x4000000) | kFObjectPropertyBase
// (0x800000000) | kFProperty (parent gate).
//
// =====================================================================

#include "Reflection/FProperty.h"
#include "Reflection/FSoftObjectPath.h"

namespace XCore::Reflect
{
    // Forward declaration. FClass lands at Phase 4b.5.
    struct FClass;

    // -----------------------------------------------------------------
    // FSoftClassProperty -- 112-byte FProperty subclass for soft class
    // references.
    // -----------------------------------------------------------------
    struct alignas(8) FSoftClassProperty : public FProperty
    {
        // ---- Per-subclass payload (offset 104; 8 bytes) ----

        // The upper-bound class descriptor. The slot's soft-resolved
        // FClass* MUST be IsChildOf(MetaClass).
        FClass* MetaClass;           // 104 +8

        // ---- Construction ----

        constexpr FSoftClassProperty() noexcept
            : FProperty()
            , MetaClass(nullptr)
        {
        }

        FSoftClassProperty(FFieldVariant InOwner, FName InName,
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
    static_assert(sizeof(FSoftClassProperty)  == 112,
                  "FSoftClassProperty ABI lock: 112 bytes "
                  "(104 FProperty + 8 MetaClass)");
    static_assert(alignof(FSoftClassProperty) == 8,
                  "FSoftClassProperty ABI lock: 8-byte alignment");
    static_assert(offsetof(FSoftClassProperty, MetaClass) == 104,
                  "FSoftClassProperty ABI lock: MetaClass @ offset 104");
    static_assert(::std::is_trivially_copyable_v<FSoftClassProperty>,
                  "FSoftClassProperty must be trivially copyable");
    static_assert(::std::is_trivially_destructible_v<FSoftClassProperty>,
                  "FSoftClassProperty must be trivially destructible");

    extern const FFakeVTable kFSoftClassPropertyFakeVTable;
    extern FFieldClass       kFSoftClassPropertyStaticClass;
    const FFieldClass& GetFSoftClassPropertyStaticClass() noexcept;

} // namespace XCore::Reflect
