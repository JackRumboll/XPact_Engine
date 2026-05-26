// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FDelegateProperty.h -- reflected FScriptDelegate (single-cast)
// property (XCore-4b §5.5 + §11.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.5 + Section 11.2 row `FDelegateProperty:
// 112 bytes (extends FProperty + SignatureFunction @ offset 104)`.
//
// FDelegateProperty is the FProperty subclass for single-cast delegate
// fields (`FScriptDelegate`). The per-subclass payload is a single
// 8-byte pointer to the FFunctionDescriptor describing the delegate's
// signature (parameter types + return type).
//
// SEMANTICS:
//
//   * The value slot in the owner struct holds an FScriptDelegate
//     by-value (a {FObject* Target; FName FunctionName} pair = 16
//     bytes -- the same layout UE uses for FScriptDelegate).
//   * ContainsObjectReference returns true (the FScriptDelegate's
//     Target field IS an XObject reference; GC must scan).
//
// FFunctionDescriptor FORWARD DECLARED -- the full type lands at Phase
// 4b.5 (FInterface ships FFunctionDescriptor alongside its function-
// list TArray). Phase 4b.4b stores + null-checks the pointer.
//
// CastFlag bit: kFDelegateProperty (0x400000) | kFProperty.
//
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    // Forward declaration. FFunctionDescriptor lands at Phase 4b.5
    // (alongside FInterface's function-list).
    struct FFunctionDescriptor;

    // -----------------------------------------------------------------
    // FDelegateProperty -- 112-byte FProperty subclass for single-cast
    // delegates.
    // -----------------------------------------------------------------
    struct alignas(8) FDelegateProperty : public FProperty
    {
        // ---- Per-subclass payload (offset 104; 8 bytes) ----

        // The runtime descriptor for the delegate's signature.
        FFunctionDescriptor* SignatureFunction;       // 104 +8

        // ---- Construction ----

        constexpr FDelegateProperty() noexcept
            : FProperty()
            , SignatureFunction(nullptr)
        {
        }

        FDelegateProperty(FFieldVariant InOwner, FName InName,
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
    static_assert(sizeof(FDelegateProperty)  == 112,
                  "FDelegateProperty ABI lock: 112 bytes "
                  "(104 FProperty + 8 SignatureFunction)");
    static_assert(alignof(FDelegateProperty) == 8,
                  "FDelegateProperty ABI lock: 8-byte alignment");
    static_assert(offsetof(FDelegateProperty, SignatureFunction) == 104,
                  "FDelegateProperty ABI lock: SignatureFunction @ offset 104");
    static_assert(::std::is_trivially_copyable_v<FDelegateProperty>,
                  "FDelegateProperty must be trivially copyable");
    static_assert(::std::is_trivially_destructible_v<FDelegateProperty>,
                  "FDelegateProperty must be trivially destructible");

    extern const FFakeVTable kFDelegatePropertyFakeVTable;
    extern FFieldClass       kFDelegatePropertyStaticClass;
    const FFieldClass& GetFDelegatePropertyStaticClass() noexcept;

} // namespace XCore::Reflect
