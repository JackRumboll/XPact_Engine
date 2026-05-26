// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// EPropertyFlags.h -- 64-bit FProperty flag bitmask (XCore-4b §5.3 +
// §5.8 + FIX-1 + FIX-18 + FIX-20).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.3 + Section 5.8 (C#-specific reflection
// mappings).
//
// EPropertyFlags is the 64-bit bitmask carried on every FProperty at
// offset 48. It encodes per-property visibility, replication discipline,
// C#-binding hints, and persistence policy. Each bit's semantics mirror
// UE's `EPropertyFlags` (`Engine/Source/Runtime/CoreUObject/Public/UObject/
// ObjectMacros.h:282-516`) with the following deliberate divergences:
//
//   * CPF_PushModel  (bit 56)  -- replaces UE's per-FProperty PushModelBits
//                                 bitfield (Rev 2 FIX-1 + FIX-20). One flag
//                                 bit is enough; XNetworking's
//                                 FReplicationStateDescriptorRegistry caches
//                                 push-state per-descriptor at session-init.
//   * CPF_DeltaCompressed (bit 57) -- replaces UE's per-FProperty
//                                 DeltaHintFlags bitfield (Rev 2 FIX-1 +
//                                 FIX-20). Sub-mode bits can be reintroduced
//                                 as additional CPF_* if a concrete need
//                                 surfaces.
//   * CPF_NullableReferenceType (bit 60) -- XPact-only; declares a C#
//                                 nullable reference annotation
//                                 (`string?`, `SomeXObject?`) is on the
//                                 declaring property. XIL2CPP preserves
//                                 the bit across the C++ emit boundary
//                                 so C# consumers can interrogate
//                                 declared-nullability via reflection.
//                                 Per FIX-18 (XCore-4b §5.8).
//   * CPF_RequiredInit (bit 61) -- XPact-only; declares C# `required`
//                                 init-only members. XIL2CPP emits the
//                                 runtime initialisation check.
//                                 Per FIX-18 (XCore-4b §5.8).
//
// BITS 62-63 are RESERVED for future XPact-only annotations (no
// committed semantics in Rev 3). Bits 0-55 mirror UE's CPF_* set.
//
// ABI LOCK (Rev 3 + Stage B addendum §11.2):
//
//   * `XPACT_FPROPERTY_LAYOUT_TAG` includes "PropertyFlags @ offset 48
//     in 8-byte slot" -- changing the underlying type or width of
//     EPropertyFlags ABI-breaks every FProperty subclass.
//   * The 64-bit width is locked at 8 bytes via static_assert below.
//   * Bit assignments 0-13 + 56-61 are MVP-locked; bits 14-55 + 62-63
//     are reserved for future expansion. A new CPF_ landing later
//     consumes the next free bit; the Stage B addendum gains a row
//     at Contract Rev 13.9+.
//
// =====================================================================

#include "Macros/XCoreTypes.h"

#include <type_traits>

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // EPropertyFlags -- 64-bit FProperty bitmask.
    //
    // Underlying type is uint64 (8 bytes; consumes the entire 64-bit
    // slot on FProperty at offset 48 per §11.2 layout table).
    //
    // The enum is `enum class` (strongly typed) so accidental conversion
    // from a raw uint64 is forbidden. Bitwise operators below provide
    // the compose-and-test surface; `static_cast<::uint64>` is the only
    // path to a raw integer.
    //
    // The MVP locks the bit-position contract for the listed entries.
    // CPF_None (== 0) is the no-flags sentinel.
    // -----------------------------------------------------------------
    enum class EPropertyFlags : ::uint64
    {
        // No flags set -- the default-constructed FProperty state.
        CPF_None                       = 0ULL,

        // ---------------------------------------------------------------
        // Editor / Blueprint visibility (bits 0-3).
        //
        // These mirror UE's CPF_Edit / CPF_BlueprintVisible / etc.
        // The semantics differ slightly because XPact has no Editor in
        // MVP, but the bit-layout discipline is preserved so XHT-emitted
        // .gen.cpp can be ABI-stable against UE-derived data.
        // ---------------------------------------------------------------
        CPF_Edit                       = 1ULL << 0,   // editable in editor
        CPF_BlueprintVisible           = 1ULL << 1,   // exposed to Blueprint
        CPF_BlueprintReadOnly          = 1ULL << 2,   // Blueprint can read but not write
        CPF_ExposeOnSpawn              = 1ULL << 3,   // exposed on actor-spawn dialog

        // ---------------------------------------------------------------
        // Replication (bits 4-7).
        //
        // CPF_Net is the primary "this property replicates" gate;
        // additional state lives in the BlueprintReplicationCondition
        // byte + RepNotifyFunc FName + CPF_PushModel / CPF_DeltaCompressed
        // bits below.
        // ---------------------------------------------------------------
        CPF_Net                        = 1ULL << 4,   // replicated
        CPF_RepNotify                  = 1ULL << 5,   // RepNotifyFunc fires on receive
        CPF_NetOwner                   = 1ULL << 6,   // owner-only replication semantics
        CPF_RepSkipOwner               = 1ULL << 7,   // skip-owner replication

        // ---------------------------------------------------------------
        // Persistence (bits 8-11).
        // ---------------------------------------------------------------
        CPF_Transient                  = 1ULL << 8,   // not persisted on save
        CPF_Const                      = 1ULL << 9,   // C++ const member
        CPF_Static                     = 1ULL << 10,  // C++ static member
        CPF_GlobalConfig               = 1ULL << 11,  // saved in global config file

        // ---------------------------------------------------------------
        // Storage / encoding (bits 12-15).
        // ---------------------------------------------------------------
        CPF_Config                     = 1ULL << 12,  // saved in config file
        CPF_DisableEditOnInstance      = 1ULL << 13,  // editable on archetype only
        CPF_DisableEditOnTemplate      = 1ULL << 14,  // editable on instance only
        CPF_Deprecated                 = 1ULL << 15,  // deprecated; emit warning on use

        // ---------------------------------------------------------------
        // Reference semantics (bits 16-19).
        // ---------------------------------------------------------------
        CPF_Ref                        = 1ULL << 16,  // pass-by-reference parameter
        CPF_OutParam                   = 1ULL << 17,  // out-parameter on function
        CPF_ReturnParam                = 1ULL << 18,  // function return value
        CPF_Parm                       = 1ULL << 19,  // function parameter

        // ---------------------------------------------------------------
        // Container-element semantics (bits 20-23).
        // ---------------------------------------------------------------
        CPF_ContainsInstancedReference = 1ULL << 20,  // contains an instanced sub-object reference
        CPF_InstancedReference         = 1ULL << 21,  // the property IS an instanced sub-object reference
        CPF_ExportObject               = 1ULL << 22,  // export-text serialises by value, not by ref
        CPF_PersistentInstance         = 1ULL << 23,  // instanced sub-object persists across reload

        // ---------------------------------------------------------------
        // Editor-specific (bits 24-29).
        // ---------------------------------------------------------------
        CPF_EditorOnly                 = 1ULL << 24,  // stripped from cooked builds
        CPF_NonTransactional           = 1ULL << 25,  // excluded from undo/redo transactions
        CPF_AutoWeak                   = 1ULL << 26,  // implicit weak-ptr conversion at C# binding
        CPF_AdvancedDisplay            = 1ULL << 27,  // hidden under "Advanced" expander in editor
        CPF_Protected                  = 1ULL << 28,  // C++ protected access
        CPF_BlueprintCallable          = 1ULL << 29,  // callable from Blueprint (for delegate properties)

        // ---------------------------------------------------------------
        // Function / delegate semantics (bits 30-33).
        // ---------------------------------------------------------------
        CPF_BlueprintAuthorityOnly     = 1ULL << 30,  // delegate fires server-side only
        CPF_TextExportTransient        = 1ULL << 31,  // skipped from text export
        CPF_NonPIEDuplicateTransient   = 1ULL << 32,  // not duplicated for PIE copies
        CPF_ExposeFunctionCategories   = 1ULL << 33,  // delegate function category exposed in editor

        // ---------------------------------------------------------------
        // Reserved bits (34-55).
        //
        // Bit positions 34-55 are RESERVED for future MVP CPF_* additions.
        // Adding a new flag consumes the next free bit; the Stage B
        // addendum gains a row when each lands.
        // ---------------------------------------------------------------

        // ---------------------------------------------------------------
        // XPact-novel replication bits (Rev 2 FIX-1 + FIX-20).
        // Bits 56-57 collapse Rev 1's per-FProperty PushModelBits +
        // DeltaHintFlags bitfields into single flag bits; XNetworking
        // consults the flags at descriptor-build time.
        // ---------------------------------------------------------------
        CPF_PushModel                  = 1ULL << 56,  // opt-in to push-model replication
        CPF_DeltaCompressed            = 1ULL << 57,  // opt-in to delta compression

        // ---------------------------------------------------------------
        // Reserved bits 58-59 (RESERVED).
        // ---------------------------------------------------------------

        // ---------------------------------------------------------------
        // XPact-novel C# reflection bits (Rev 2 FIX-18; XCore-4b §5.8).
        // Bits 60-61 carry C#-only annotations that XIL2CPP propagates
        // across the C++ emit boundary so C# consumers can interrogate
        // declared-nullability + required-init via reflection.
        // ---------------------------------------------------------------
        CPF_NullableReferenceType      = 1ULL << 60,  // C# `string?` / `SomeXObject?`
        CPF_RequiredInit               = 1ULL << 61,  // C# `required` init-only members

        // ---------------------------------------------------------------
        // Reserved bits 62-63 (RESERVED for future XPact-only flags).
        // ---------------------------------------------------------------
    };

    // -----------------------------------------------------------------
    // Bitwise operator surface.
    //
    // Strongly-typed enum classes do not implicitly support bitwise
    // composition; we provide the operators explicitly so callers can
    // write `Flags1 | Flags2` without a static_cast at every site.
    // All operators are constexpr so they can fold at compile time
    // (XHT-emitted .gen.cpp aggregates property flags via OR-composition
    // at the call site).
    // -----------------------------------------------------------------

    [[nodiscard]] constexpr EPropertyFlags operator|(EPropertyFlags Lhs, EPropertyFlags Rhs) noexcept
    {
        return static_cast<EPropertyFlags>(
            static_cast<::uint64>(Lhs) | static_cast<::uint64>(Rhs));
    }

    [[nodiscard]] constexpr EPropertyFlags operator&(EPropertyFlags Lhs, EPropertyFlags Rhs) noexcept
    {
        return static_cast<EPropertyFlags>(
            static_cast<::uint64>(Lhs) & static_cast<::uint64>(Rhs));
    }

    [[nodiscard]] constexpr EPropertyFlags operator^(EPropertyFlags Lhs, EPropertyFlags Rhs) noexcept
    {
        return static_cast<EPropertyFlags>(
            static_cast<::uint64>(Lhs) ^ static_cast<::uint64>(Rhs));
    }

    [[nodiscard]] constexpr EPropertyFlags operator~(EPropertyFlags Operand) noexcept
    {
        return static_cast<EPropertyFlags>(
            ~static_cast<::uint64>(Operand));
    }

    constexpr EPropertyFlags& operator|=(EPropertyFlags& Lhs, EPropertyFlags Rhs) noexcept
    {
        Lhs = Lhs | Rhs;
        return Lhs;
    }

    constexpr EPropertyFlags& operator&=(EPropertyFlags& Lhs, EPropertyFlags Rhs) noexcept
    {
        Lhs = Lhs & Rhs;
        return Lhs;
    }

    constexpr EPropertyFlags& operator^=(EPropertyFlags& Lhs, EPropertyFlags Rhs) noexcept
    {
        Lhs = Lhs ^ Rhs;
        return Lhs;
    }

    // -----------------------------------------------------------------
    // HasAnyPropertyFlags / HasAllPropertyFlags helpers.
    //
    // Predicate sugar over the bitwise operators. Mirrors the pattern
    // in EClassCastFlags.h.
    // -----------------------------------------------------------------

    [[nodiscard]] constexpr bool HasAnyPropertyFlags(EPropertyFlags Combined,
                                                     EPropertyFlags FlagsToCheck) noexcept
    {
        return (Combined & FlagsToCheck) != EPropertyFlags::CPF_None;
    }

    [[nodiscard]] constexpr bool HasAllPropertyFlags(EPropertyFlags Combined,
                                                     EPropertyFlags FlagsToCheck) noexcept
    {
        return (Combined & FlagsToCheck) == FlagsToCheck;
    }

    // -----------------------------------------------------------------
    // ABI lock: the underlying type MUST be uint64. The FProperty layout
    // (§11.2) carries PropertyFlags at offset 48 occupying 8 bytes; any
    // change to the underlying type breaks the layout.
    // -----------------------------------------------------------------
    static_assert(sizeof(EPropertyFlags) == 8,
                  "EPropertyFlags ABI lock: underlying type must be uint64 "
                  "(8 bytes; matches FProperty::PropertyFlags slot in §11.2)");
    static_assert(::std::is_same_v<::std::underlying_type_t<EPropertyFlags>, ::uint64>,
                  "EPropertyFlags ABI lock: underlying type must be uint64 "
                  "(strongly typed; raw integer access requires explicit cast)");

} // namespace XCore::Reflect
