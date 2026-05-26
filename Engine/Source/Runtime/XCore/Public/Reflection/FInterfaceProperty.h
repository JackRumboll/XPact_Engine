// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FInterfaceProperty.h -- reflected TScriptInterface<I> property
// (XCore-4b §5.5 + §11.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.5 + Section 11.2 row `FInterfaceProperty:
// 112 bytes (extends FProperty + InterfaceClass @ offset 104)`.
//
// FInterfaceProperty is the FProperty subclass for typed-interface
// references (`TScriptInterface<IFoo>`). The value slot in the owner
// struct is an FObject* pointer that fulfils some interface contract;
// the FInterfaceProperty's InterfaceClass field names the interface.
//
// SEMANTICS:
//
//   * Strong reference (the wrapped FObject* is a GC root via this
//     property): ContainsObjectReference returns true.
//   * InterfaceClass is the FInterface descriptor for the named
//     interface (e.g. IInteractable's FInterface). FInterface lands
//     at Phase 4b.5; Phase 4b.4b stores + null-checks the pointer.
//   * The value slot's FObject* MUST implement InterfaceClass; this
//     is enforced by the XHT codegen path at FClass::Link time and
//     by C++ template constraints at compile time.
//
// FInterface FORWARD DECLARED -- the full FInterface type lands at
// Phase 4b.5.
//
// CastFlag bit: kFInterfaceProperty (0x100000) | kFProperty.
//
// NOTE: FInterfaceProperty does NOT include kFObjectPropertyBase in
// its CastFlags. The "object reference" semantics live on the wrapped
// FObject* value, but the FInterfaceProperty itself is not an
// FObjectProperty subclass (UE's hierarchy puts FInterfaceProperty
// outside FObjectProperty; XPact mirrors that). The
// ContainsObjectReference dispatch slot is the correct way to query
// reachability.
//
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    // Forward declaration. FInterface lands at Phase 4b.5.
    struct FInterface;

    // Forward declaration. FObject is the GC base; the value slot
    // stores an FObject* fulfilling the named interface.
    struct FObject;

    // -----------------------------------------------------------------
    // FInterfaceProperty -- 112-byte FProperty subclass for typed-
    // interface references.
    // -----------------------------------------------------------------
    struct alignas(8) FInterfaceProperty : public FProperty
    {
        // ---- Per-subclass payload (offset 104; 8 bytes) ----

        // The runtime descriptor for the named interface.
        FInterface* InterfaceClass;       // 104 +8

        // ---- Construction ----

        constexpr FInterfaceProperty() noexcept
            : FProperty()
            , InterfaceClass(nullptr)
        {
        }

        FInterfaceProperty(FFieldVariant InOwner, FName InName,
                           FInterface* InInterfaceClass = nullptr) noexcept;

        // ---- FConstructFn target ----

        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;

        // ---- StaticClass hook for Cast<T> ----

        static const FFieldClass* StaticClass() noexcept;

        // ---- Per-subclass accessors ----

        [[nodiscard]] XPACT_FORCEINLINE FInterface* GetInterfaceClass() const noexcept
        {
            return InterfaceClass;
        }

        XPACT_FORCEINLINE void SetInterfaceClass(FInterface* InClass) noexcept
        {
            InterfaceClass = InClass;
        }
    };

    // ABI lock.
    static_assert(sizeof(FInterfaceProperty)  == 112,
                  "FInterfaceProperty ABI lock: 112 bytes "
                  "(104 FProperty + 8 InterfaceClass)");
    static_assert(alignof(FInterfaceProperty) == 8,
                  "FInterfaceProperty ABI lock: 8-byte alignment");
    static_assert(offsetof(FInterfaceProperty, InterfaceClass) == 104,
                  "FInterfaceProperty ABI lock: InterfaceClass @ offset 104");
    static_assert(::std::is_trivially_copyable_v<FInterfaceProperty>,
                  "FInterfaceProperty must be trivially copyable");
    static_assert(::std::is_trivially_destructible_v<FInterfaceProperty>,
                  "FInterfaceProperty must be trivially destructible");

    extern const FFakeVTable kFInterfacePropertyFakeVTable;
    extern FFieldClass       kFInterfacePropertyStaticClass;
    const FFieldClass& GetFInterfacePropertyStaticClass() noexcept;

} // namespace XCore::Reflect
