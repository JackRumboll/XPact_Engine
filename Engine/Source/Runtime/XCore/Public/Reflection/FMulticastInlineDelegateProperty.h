// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FMulticastInlineDelegateProperty.h -- reflected
// FMulticastInlineDelegate property (XCore-4b §5.5 + §11.2;
// Rev 2 MVP add per FIX-3).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.5 + Section 11.2 row
// `FMulticastInlineDelegateProperty: 112 bytes (extends FProperty +
// SignatureFunction @ offset 104)`.
//
// FMulticastInlineDelegateProperty is the FProperty subclass for UE's
// primary multicast pattern (e.g., `OnDestroyed`,
// `OnComponentBeginOverlap`). The per-subclass payload is identical
// to FDelegateProperty -- a single 8-byte pointer to the
// FFunctionDescriptor describing the delegate's signature -- the
// discriminator is the FFieldClass identity (the slot's contained
// FMulticastInlineDelegate value type has different semantics from a
// single-cast FScriptDelegate).
//
// SEMANTICS:
//
//   * The value slot in the owner struct holds an
//     FMulticastInlineDelegate by-value (an inline list of subscriber
//     {FObject*, FName} pairs; the runtime size is implementation-
//     specific, populated at FClass::Link time at Phase 4b.5).
//   * ContainsObjectReference returns true (every subscriber's Target
//     is a GC root).
//
// CastFlag bit: kFMulticastInlineDelegateProperty (0x8000000) |
// kFProperty.
//
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    // Forward declaration. FFunctionDescriptor lands at Phase 4b.5.
    struct FFunctionDescriptor;

    // -----------------------------------------------------------------
    // FMulticastInlineDelegateProperty -- 112-byte FProperty subclass.
    // -----------------------------------------------------------------
    struct alignas(8) FMulticastInlineDelegateProperty : public FProperty
    {
        // ---- Per-subclass payload (offset 104; 8 bytes) ----

        FFunctionDescriptor* SignatureFunction;       // 104 +8

        // ---- Construction ----

        constexpr FMulticastInlineDelegateProperty() noexcept
            : FProperty()
            , SignatureFunction(nullptr)
        {
        }

        FMulticastInlineDelegateProperty(FFieldVariant InOwner, FName InName,
                                          FFunctionDescriptor* InSignatureFunction = nullptr) noexcept;

        // ---- FConstructFn target ----

        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;

        // ---- StaticClass hook for Cast<T> ----

        static const FFieldClass* StaticClass() noexcept;

        // ---- Per-subclass accessors ----

        [[nodiscard]] XPACT_FORCEINLINE FFunctionDescriptor* GetSignatureFunction() const noexcept
        {
            return SignatureFunction;
        }

        XPACT_FORCEINLINE void SetSignatureFunction(FFunctionDescriptor* InFn) noexcept
        {
            SignatureFunction = InFn;
        }
    };

    // ABI lock.
    static_assert(sizeof(FMulticastInlineDelegateProperty)  == 112,
                  "FMulticastInlineDelegateProperty ABI lock: 112 bytes "
                  "(104 FProperty + 8 SignatureFunction)");
    static_assert(alignof(FMulticastInlineDelegateProperty) == 8,
                  "FMulticastInlineDelegateProperty ABI lock: 8-byte alignment");
    static_assert(offsetof(FMulticastInlineDelegateProperty, SignatureFunction) == 104,
                  "FMulticastInlineDelegateProperty ABI lock: SignatureFunction @ 104");
    static_assert(::std::is_trivially_copyable_v<FMulticastInlineDelegateProperty>,
                  "FMulticastInlineDelegateProperty must be trivially copyable");
    static_assert(::std::is_trivially_destructible_v<FMulticastInlineDelegateProperty>,
                  "FMulticastInlineDelegateProperty must be trivially destructible");

    extern const FFakeVTable kFMulticastInlineDelegatePropertyFakeVTable;
    extern FFieldClass       kFMulticastInlineDelegatePropertyStaticClass;
    const FFieldClass& GetFMulticastInlineDelegatePropertyStaticClass() noexcept;

} // namespace XCore::Reflect
