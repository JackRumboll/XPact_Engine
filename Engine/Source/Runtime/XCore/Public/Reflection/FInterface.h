// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FInterface.h -- the 56-byte interface descriptor (XCore-4b §7.5 +
// §7.5.1 + §11.3; FIX-R2-CRIT-1 + FIX-8 Rev 2).
// =====================================================================
//
// XCore-4b Rev 3, Section 7.5 ("FInterface") + Section 7.5.1
// ("Interface dispatch") + Section 11.3 layout row `FInterface: 56
// bytes (32 FField + 24 FInterface-specific)`.
//
// FInterface is the runtime descriptor for reflected interfaces
// (XPact's XInterface, the C++ side; the C# side uses the `interface`
// attribute keyword). UE has both `UInterface` and `IInterface` with
// a complex dispatch story; XPact collapses to a single FInterface
// descriptor + the function-pointer dispatch lives directly on the
// descriptor's InterfaceFunctions table.
//
// LAYOUT (Phase 4b.5 audit-corrected; spec said 56 with TArray=16,
// actually 64 with TArray=24; see FStruct.h SPEC DRIFT NOTICE):
//
//   struct alignas(8) FInterface : FField {
//       // FField base @ 0-31 (32 bytes)
//       TArray<FFunctionDescriptor>  InterfaceFunctions;  // 32 +24 (TArray=24)
//       EInterfaceFlags              InterfaceFlags;      // 56  +4
//       uint32                       _pad;                // 60  +4
//   };
//
// sizeof(FInterface) == 64.
//
// XPACT_FINTERFACE_LAYOUT_TAG (Rev 3 §11.6):
//   "FInterface-v3: 56 bytes; InterfaceFunctions TArray @ offset 32 =
//    16 bytes; InterfaceFlags @ 48"
//
// FFunctionDescriptor:
//
//   16 bytes; { FName Name; uint64 SignatureHash }. The SignatureHash
//   is a BLAKE3-truncated 64-bit hash over the function signature
//   (return type + parameter types + qualifiers) used by Blueprint VM
//   / scripting dispatch (§7.5.1) to verify the implementing function
//   matches the interface declaration before invoking.
//
// INTERFACE DISPATCH SHAPES (§7.5.1; FIX-8 Rev 2):
//
//   1. C++ user-class implementing the interface
//      ----------------------------------------
//      Standard virtual dispatch through the user-class's own vtable.
//      The FInterface descriptor records that the class implements
//      the interface (via the FClass's InterfaceImplementations
//      table); the descriptor does NOT participate in C++-side
//      dispatch.
//
//      Example:
//        class XValve : public IInteractable {
//            virtual void OnInteract() override;
//        };
//      Dispatch: XValve's normal C++ vtable.
//
//   2. C# transpiled-class implementing the interface
//      ----------------------------------------------
//      XIL2CPP emits a C++ class with virtual methods matching the
//      C# interface declaration; dispatch is via the emitted C++
//      vtable, structurally identical to the C++-user-class shape
//      above. The transpiler ensures the vtable layout matches the
//      C++-declared interface.
//
//   3. Blueprint VM / scripting / reflection-driven dispatch
//      ----------------------------------------------------
//      The caller has a function FName and an XObject instance; it
//      must find the implementing function. The lookup walks the
//      FClass's InterfaceImplementations table for the requested
//      FInterface descriptor, finds the FFunctionDescriptor by
//      SignatureHash + FName, and dispatches via the
//      FFunctionDescriptor's resolved function pointer. THIS is the
//      only path that consults the FInterface descriptor at dispatch
//      time.
//
// The InterfaceImplementations table lives on the FClass (one entry
// per implemented interface), keyed by FInterface descriptor pointer,
// valued as TArray<FFunctionDescriptor*> -- one entry per function
// the implementing class provides. XHT emits this at .gen.cpp time
// by inspecting the C# class's interface declarations (or the C++
// class's `public IFoo` inheritance) and emitting the descriptor
// table. Phase 4b.5 ships the FInterface descriptor + the
// InterfaceFunctions table; the FClass-side InterfaceImplementations
// is XReflectionRuntime work (Phase 4b.6).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Containers/TArray.h"
#include "Reflection/EClassCastFlags.h"
#include "Reflection/EInterfaceFlags.h"
#include "Reflection/FField.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FFieldVariant.h"
#include "Reflection/FName.h"

#include <cstddef>

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // FFunctionDescriptor -- one entry in FInterface::InterfaceFunctions.
    //
    // 16 bytes; FName + uint64 SignatureHash per spec §7.5 + §11.3.
    //
    // The SignatureHash is the BLAKE3-truncated 64-bit hash over the
    // function signature (return type + parameter types + qualifiers).
    // Blueprint VM / scripting dispatch consults the hash before
    // invoking the implementing function pointer to verify the
    // signature matches the interface declaration; signature drift
    // (e.g., a stale post-hot-reload patch) is caught at dispatch
    // time rather than producing silent corruption.
    // -----------------------------------------------------------------
    struct alignas(8) FFunctionDescriptor
    {
        FName    Name;            //  0  +8   function name (e.g. "OnInteract")
        ::uint64 SignatureHash;   //  8  +8   BLAKE3-truncated signature hash

        constexpr FFunctionDescriptor() noexcept
            : Name()
            , SignatureHash(0)
        {
        }

        constexpr FFunctionDescriptor(FName InName, ::uint64 InHash) noexcept
            : Name(InName)
            , SignatureHash(InHash)
        {
        }
    };

    static_assert(sizeof(FFunctionDescriptor)  == 16,
                  "FFunctionDescriptor ABI lock: must be exactly 16 bytes "
                  "(FName 8 + uint64 SignatureHash 8). See XCore-4b §7.5 + §11.3.");
    static_assert(alignof(FFunctionDescriptor) == 8,
                  "FFunctionDescriptor ABI lock: 8-byte alignment");
    static_assert(offsetof(FFunctionDescriptor, Name)          == 0,
                  "FFunctionDescriptor ABI lock: Name at offset 0");
    static_assert(offsetof(FFunctionDescriptor, SignatureHash) == 8,
                  "FFunctionDescriptor ABI lock: SignatureHash at offset 8");
    static_assert(::std::is_standard_layout_v<FFunctionDescriptor>,
                  "FFunctionDescriptor must be standard layout");
    static_assert(::std::is_trivially_copyable_v<FFunctionDescriptor>,
                  "FFunctionDescriptor must be trivially copyable");
    static_assert(::std::is_trivially_destructible_v<FFunctionDescriptor>,
                  "FFunctionDescriptor must be trivially destructible");

    // -----------------------------------------------------------------
    // FInterface -- 56-byte FField subclass for reflected interfaces.
    //
    // Per spec §7.5: alignas(8). NO virtual methods.
    //
    // FInterface is non-copyable + non-movable: owns the
    // InterfaceFunctions TArray (heap-allocated FFunctionDescriptor
    // storage).
    // -----------------------------------------------------------------
    struct alignas(8) FInterface : public FField
    {
        // ---- Per-subclass payload (offsets 32-55; 24 bytes) ----

        ::XCore::TArray<FFunctionDescriptor> InterfaceFunctions;  // 32 +24
        EInterfaceFlags                      InterfaceFlags;      // 56  +4
        ::uint32                             _pad;                // 60  +4

        // -------------------------------------------------------------
        // Construction.
        // -------------------------------------------------------------

        FInterface() noexcept
            : FField()
            , InterfaceFunctions()
            , InterfaceFlags(EInterfaceFlags::INTERFACE_None)
            , _pad(0)
        {
        }

        FInterface(FFieldVariant InOwner, FName InName,
                   EInterfaceFlags InFlags = EInterfaceFlags::INTERFACE_None) noexcept;

        // Non-copyable + non-movable: owns InterfaceFunctions TArray.
        FInterface(const FInterface&)            = delete;
        FInterface(FInterface&&)                 = delete;
        FInterface& operator=(const FInterface&) = delete;
        FInterface& operator=(FInterface&&)      = delete;
        ~FInterface() noexcept                   = default;

        // -------------------------------------------------------------
        // Accessors.
        // -------------------------------------------------------------

        [[nodiscard]] XPACT_FORCEINLINE EInterfaceFlags GetInterfaceFlags() const noexcept
        {
            return InterfaceFlags;
        }

        [[nodiscard]] XPACT_FORCEINLINE const ::XCore::TArray<FFunctionDescriptor>&
            GetInterfaceFunctions() const noexcept
        {
            return InterfaceFunctions;
        }

        [[nodiscard]] XPACT_FORCEINLINE ::int32 NumFunctions() const noexcept
        {
            return InterfaceFunctions.Num();
        }

        // -------------------------------------------------------------
        // FindFunctionByName -- linear scan of InterfaceFunctions for
        // a function with the given FName.
        //
        // Returns the matching FFunctionDescriptor* (pointer into the
        // TArray; valid until the TArray is mutated or the FInterface
        // is destroyed) or nullptr if not found.
        //
        // Body in FInterface.cpp.
        // -------------------------------------------------------------
        [[nodiscard]] const FFunctionDescriptor*
            FindFunctionByName(FName FunctionName) const noexcept;

        // -------------------------------------------------------------
        // FindFunctionByHash -- lookup by SignatureHash.
        //
        // Used by Blueprint VM / scripting dispatch to verify a hash
        // match before invoking. Linear scan; the hash space is
        // sparse enough that a TMap lookup would only pay off for
        // interfaces with many functions (uncommon).
        //
        // Body in FInterface.cpp.
        // -------------------------------------------------------------
        [[nodiscard]] const FFunctionDescriptor*
            FindFunctionByHash(::uint64 SignatureHash) const noexcept;

        // -------------------------------------------------------------
        // FConstructFn target.
        // -------------------------------------------------------------
        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;

        // -------------------------------------------------------------
        // StaticClass hook for Cast<T>.
        // -------------------------------------------------------------
        static const FFieldClass* StaticClass() noexcept;
    };

    // ---------------------------------------------------------------------
    // ABI locks (Phase 4b.5 audit-corrected; see SPEC DRIFT in FStruct.h).
    // ---------------------------------------------------------------------
    static_assert(sizeof(FInterface)  == 64,
                  "FInterface ABI lock (audit-corrected): 64 bytes (32 FField + "
                  "32 FInterface-specific). Spec Rev 3 §7.5 declared 56 assuming "
                  "TArray=16; actual TArray=24 makes InterfaceFunctions 24 bytes "
                  "and pushes InterfaceFlags to offset 56. Note Rev 2 also "
                  "declared 64 -- this matches Rev 2's value.");
    static_assert(alignof(FInterface) == 8,
                  "FInterface ABI lock: 8-byte alignment per §7.5 alignas(8)");

    // Member offsets locked per audit-corrected layout.
    static_assert(offsetof(FInterface, InterfaceFunctions) == 32,
                  "FInterface ABI lock: InterfaceFunctions at offset 32");
    static_assert(offsetof(FInterface, InterfaceFlags)     == 56,
                  "FInterface ABI lock (audit-corrected): InterfaceFlags at "
                  "offset 56 (spec said 48; +8 shift from TArray=24)");

    // -----------------------------------------------------------------
    // The FFieldClass + accessor for FInterface.
    //
    // Same pattern as FEnum (Phase 4b.5): FInterface is an FField
    // subclass with its own FFieldClass anchor, but does NOT participate
    // in the FProperty CastFlags hierarchy. CastFlags is kNone;
    // SuperClass is the base FField.
    // -----------------------------------------------------------------
    extern FFieldClass kFInterfaceStaticClass;
    const FFieldClass& GetFInterfaceStaticClass() noexcept;

} // namespace XCore::Reflect
