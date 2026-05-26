// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FStructProperty.h -- reflected nested-struct property
// (XCore-4b §5.5 + §11.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.5 + Section 11.2 row `FStructProperty: 112
// bytes (extends FProperty + StructPtr @ offset 104)`.
//
// FStructProperty is the FProperty subclass for nested-struct fields
// (e.g., `FVector Position`, `FTransform Origin`). The per-subclass
// payload is a single 8-byte pointer to the `FStruct*` (technically an
// `FScriptStruct*` for the rich-script structs) that describes the
// nested struct's reflected layout.
//
// SEMANTICS:
//
//   * The value slot in the owner struct holds the nested struct's
//     bytes INLINE (not a pointer to a heap-allocated nested instance).
//     The ElementSize field on the base FProperty captures the nested
//     struct's sizeof; the dispatch slots use this to walk the bytes.
//   * ContainsObjectReference delegates to the wrapped FStruct's
//     ObjectRefProperties walk (FIX-13): if any of the nested struct's
//     own properties contain an XObject reference, this FStructProperty
//     does too. The EncounteredStructProps argument tracks recursion
//     across nested struct definitions to avoid infinite loops on
//     circular struct references.
//   * Identical / GetValueTypeHash delegate to the FStruct's own
//     bytewise compare / hash (the FStruct's FCppStructOps fake-vtable
//     -- FIX-R2-MAJ-2 -- carries these handlers for the rich-script
//     case; default plain-bytes case at Phase 4b.4b).
//
// FStruct FORWARD DECLARED -- the full FStruct type lands at Phase
// 4b.5. Phase 4b.4b stores + null-checks the pointer.
//
// Per spec §5.5 table the payload type is `FScriptStruct*`; XPact's
// hierarchy puts FScriptStruct under FStruct (Phase 4b.5). The
// FStructProperty stores a base `FStruct*`; the Phase 4b.5 typed
// accessor will narrow to FScriptStruct* via Cast<FScriptStruct> when
// rich-script semantics (e.g., custom Identical / Serialize) are
// needed.
//
// CastFlag bit: kFStructProperty (0x8000) | kFProperty.
//
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    // FStructProperty is already forward-declared in FProperty.h for
    // the ContainsObjectReference dispatch-slot signature. The full
    // definition here is consistent with that forward declaration.

    // FStruct lands at Phase 4b.5; forward-declared here.
    // (FStruct is also forward-declared in FProperty.h.)

    // -----------------------------------------------------------------
    // FStructProperty -- 112-byte FProperty subclass for nested
    // structs.
    // -----------------------------------------------------------------
    struct alignas(8) FStructProperty : public FProperty
    {
        // ---- Per-subclass payload (offset 104; 8 bytes) ----

        // The runtime descriptor for the nested struct's reflected
        // layout. nullptr is structurally invalid (an FStructProperty
        // must point at SOMETHING for ContainsObjectReference + dispatch
        // delegation to work); the XHT codegen path populates this at
        // FClass::Link time.
        FStruct* Struct;             // 104 +8

        // ---- Construction ----

        constexpr FStructProperty() noexcept
            : FProperty()
            , Struct(nullptr)
        {
        }

        FStructProperty(FFieldVariant InOwner, FName InName,
                        FStruct* InStruct = nullptr) noexcept;

        // ---- FConstructFn target ----

        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;

        // ---- StaticClass hook for Cast<T> ----

        static const FFieldClass* StaticClass() noexcept;

        // ---- Per-subclass accessors ----

        [[nodiscard]] XPACT_FORCEINLINE FStruct* GetStruct() const noexcept
        {
            return Struct;
        }

        XPACT_FORCEINLINE void SetStruct(FStruct* InStruct) noexcept
        {
            Struct = InStruct;
        }
    };

    // ABI lock.
    static_assert(sizeof(FStructProperty)  == 112,
                  "FStructProperty ABI lock: 112 bytes "
                  "(104 FProperty + 8 Struct pointer)");
    static_assert(alignof(FStructProperty) == 8,
                  "FStructProperty ABI lock: 8-byte alignment");
    static_assert(offsetof(FStructProperty, Struct) == 104,
                  "FStructProperty ABI lock: Struct @ offset 104");
    static_assert(::std::is_trivially_copyable_v<FStructProperty>,
                  "FStructProperty must be trivially copyable");
    static_assert(::std::is_trivially_destructible_v<FStructProperty>,
                  "FStructProperty must be trivially destructible");

    extern const FFakeVTable kFStructPropertyFakeVTable;
    extern FFieldClass       kFStructPropertyStaticClass;
    const FFieldClass& GetFStructPropertyStaticClass() noexcept;

} // namespace XCore::Reflect
