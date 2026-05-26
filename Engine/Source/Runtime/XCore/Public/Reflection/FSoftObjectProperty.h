// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FSoftObjectProperty.h -- reflected TSoftObjectPtr<T> / FSoftObjectPath
// property (XCore-4b §5.5 + §11.2; Rev 2 MVP add per FIX-3).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.5 + Section 11.2 row `FSoftObjectProperty:
// 112 bytes (extends FProperty + PropertyClass @ offset 104)`.
//
// FSoftObjectProperty is the FProperty subclass for soft asset
// references (`TSoftObjectPtr<T>` / `FSoftObjectPath`). Soft references
// do NOT create GC roots; they resolve on demand via the asset system
// (Master Plan §2 Asset reference model).
//
// SEMANTICS:
//
//   * NOT a GC root: ContainsObjectReference returns false. The
//     pointed-at asset (if loaded) lives in the asset cache; the
//     FSoftObjectProperty's slot stores an FSoftObjectPath value
//     (resolves on demand, not via GC walks).
//   * The payload at offset 104 carries the FClass* descriptor for
//     the wrapped type (same shape as FObjectProperty / FWeakObject
//     Property -- consistent across the object-reference family for
//     GC + serializer walking).
//   * The value slot holds an FSoftObjectPath value (8 bytes;
//     placeholder type from FSoftObjectPath.h; System 5 full impl).
//
// FClass FORWARD DECLARED -- the full FClass type lands at Phase 4b.5.
// FSoftObjectPath placeholder declared in FSoftObjectPath.h
// (8-byte value type; System 5 full impl).
//
// SPEC REFERENCE (per §5.6 FIX-R2-LOW-7): XPact's FSoftObjectProperty
// at 112 bytes (104 FProperty + 8 PropertyClass) is SMALLER than UE's
// equivalent (~120-128 bytes). UE uses a deep template hierarchy
// (FProperty -> FObjectPropertyBase -> TFObjectPropertyBase<FSoftObjectPtr>
// -> FSoftObjectProperty) that XPact deliberately diverges from per
// Prime Directive -- XPact's flat single-subclass-under-FProperty
// design is the spec-locked structure.
//
// CastFlag bits: kFSoftObjectProperty (0x2000000) | kFObjectPropertyBase
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
    // FSoftObjectProperty -- 112-byte FProperty subclass for soft
    // asset references.
    // -----------------------------------------------------------------
    struct alignas(8) FSoftObjectProperty : public FProperty
    {
        // ---- Per-subclass payload (offset 104; 8 bytes) ----

        // The runtime descriptor for the wrapped object type
        // (e.g., XAsset's FClass).
        FClass* PropertyClass;       // 104 +8

        // ---- Construction ----

        constexpr FSoftObjectProperty() noexcept
            : FProperty()
            , PropertyClass(nullptr)
        {
        }

        FSoftObjectProperty(FFieldVariant InOwner, FName InName,
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
    static_assert(sizeof(FSoftObjectProperty)  == 112,
                  "FSoftObjectProperty ABI lock: 112 bytes "
                  "(104 FProperty + 8 PropertyClass)");
    static_assert(alignof(FSoftObjectProperty) == 8,
                  "FSoftObjectProperty ABI lock: 8-byte alignment");
    static_assert(offsetof(FSoftObjectProperty, PropertyClass) == 104,
                  "FSoftObjectProperty ABI lock: PropertyClass @ offset 104");
    static_assert(::std::is_trivially_copyable_v<FSoftObjectProperty>,
                  "FSoftObjectProperty must be trivially copyable");
    static_assert(::std::is_trivially_destructible_v<FSoftObjectProperty>,
                  "FSoftObjectProperty must be trivially destructible");

    extern const FFakeVTable kFSoftObjectPropertyFakeVTable;
    extern FFieldClass       kFSoftObjectPropertyStaticClass;
    const FFieldClass& GetFSoftObjectPropertyStaticClass() noexcept;

} // namespace XCore::Reflect
