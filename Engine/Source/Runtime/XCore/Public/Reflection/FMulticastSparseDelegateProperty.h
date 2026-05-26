// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FMulticastSparseDelegateProperty.h -- reflected
// FMulticastSparseDelegate property (XCore-4b §5.5 + §11.2;
// Rev 2 MVP add per FIX-3).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.5 + Section 11.2 row
// `FMulticastSparseDelegateProperty: 112 bytes (extends FProperty +
// SignatureFunction @ offset 104)`.
//
// FMulticastSparseDelegateProperty is the FProperty subclass for UE's
// sparse-multicast variant -- smaller footprint per actor when the
// typical subscriber count is 0 (e.g., XPropertyChangedEvent's "may
// have subscriber" case). Same payload shape as
// FMulticastInlineDelegateProperty; the discriminator is the
// FFieldClass identity (the sparse variant uses a different runtime
// storage strategy).
//
// SEMANTICS:
//
//   * The value slot holds an FMulticastSparseDelegate by-value (a
//     sparse subscriber map; runtime size implementation-specific).
//   * ContainsObjectReference returns true (subscriber Targets are GC
//     roots when the sparse map has entries).
//
// CastFlag bit: kFMulticastSparseDelegateProperty (0x10000000) |
// kFProperty.
//
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    // Forward declaration. FFunctionDescriptor lands at Phase 4b.5.
    struct FFunctionDescriptor;

    // -----------------------------------------------------------------
    // FMulticastSparseDelegateProperty -- 112-byte FProperty subclass.
    // -----------------------------------------------------------------
    struct alignas(8) FMulticastSparseDelegateProperty : public FProperty
    {
        // ---- Per-subclass payload (offset 104; 8 bytes) ----

        FFunctionDescriptor* SignatureFunction;       // 104 +8

        // ---- Construction ----

        constexpr FMulticastSparseDelegateProperty() noexcept
            : FProperty()
            , SignatureFunction(nullptr)
        {
        }

        FMulticastSparseDelegateProperty(FFieldVariant InOwner, FName InName,
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
    static_assert(sizeof(FMulticastSparseDelegateProperty)  == 112,
                  "FMulticastSparseDelegateProperty ABI lock: 112 bytes "
                  "(104 FProperty + 8 SignatureFunction)");
    static_assert(alignof(FMulticastSparseDelegateProperty) == 8,
                  "FMulticastSparseDelegateProperty ABI lock: 8-byte alignment");
    static_assert(offsetof(FMulticastSparseDelegateProperty, SignatureFunction) == 104,
                  "FMulticastSparseDelegateProperty ABI lock: SignatureFunction @ 104");
    static_assert(::std::is_trivially_copyable_v<FMulticastSparseDelegateProperty>,
                  "FMulticastSparseDelegateProperty must be trivially copyable");
    static_assert(::std::is_trivially_destructible_v<FMulticastSparseDelegateProperty>,
                  "FMulticastSparseDelegateProperty must be trivially destructible");

    extern const FFakeVTable kFMulticastSparseDelegatePropertyFakeVTable;
    extern FFieldClass       kFMulticastSparseDelegatePropertyStaticClass;
    const FFieldClass& GetFMulticastSparseDelegatePropertyStaticClass() noexcept;

} // namespace XCore::Reflect
