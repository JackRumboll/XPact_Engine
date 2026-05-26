// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// EClassCastFlags.h -- FField cast-acceleration bitmask (XCore-4b §5.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.2 ("FFieldClass") + Section 5.5 (FProperty
// subclass family).
//
// EClassCastFlags is the 64-bit bitmask carried on every FFieldClass
// instance. Each FProperty subclass owns one dedicated bit; the IsA
// acceleration in `FField::IsA(FFieldClass*)` becomes a single AND
// instruction (the target's bit AND'd with the candidate class's
// CastFlags -- nonzero means "is or descends from").
//
// The bit assignments for the 28 FProperty subclasses are defined in
// Phase 4b.4 (FProperty.h) per the spec §5.5 table. Phase 4b.3 (this
// file) only needs:
//
//   * The strongly-typed enum class declaration (so FFieldClass can
//     carry an EClassCastFlags field).
//   * The constexpr bitwise operators (|, &, ^, ~, |=, &=, ^=) so
//     callers can compose CastFlags values ergonomically.
//   * The kNone sentinel (no bits set; used by the base FField class
//     itself, which has no dedicated cast bit -- the base is
//     identified by SuperClass == nullptr, not by a CastFlags bit).
//
// Bit-assignment matrix (Phase 4b.4 will populate this):
//
//   0x0000000000000001  FBoolProperty
//   0x0000000000000002  FByteProperty
//   0x0000000000000004  FInt8Property
//   0x0000000000000008  FInt16Property
//   0x0000000000000010  FIntProperty
//   0x0000000000000020  FInt64Property
//   ...
//   0x0000000010000000  FMulticastSparseDelegateProperty
//
// The bit assignments are locked in the Stage B addendum
// (XPACT_FPROPERTY_LAYOUT_TAG) so any patch DLL that uses CastFlags
// values directly must match.
//
// =====================================================================

#include "Macros/XCoreTypes.h"

#include <type_traits>

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // EClassCastFlags -- 64-bit cast-acceleration bitmask.
    //
    // Underlying type is uint64 (8 bytes; consumes the entire 64-bit
    // slot on FFieldClass at offset 16 per §11.2 layout table).
    //
    // The enum is `enum class` (strongly typed) so accidental conversion
    // from a raw uint64 is forbidden. Bitwise operators below provide
    // the compose-and-test surface; `static_cast<::uint64>` is the only
    // path to a raw integer.
    //
    // The base "Field" class (the root FFieldClass for the FField type
    // itself) carries kNone -- IsA-via-CastFlags is meaningless for
    // the base because every FField is structurally an FField. IsA on
    // the base is handled by walking SuperClass (which is nullptr for
    // the base).
    // -----------------------------------------------------------------
    enum class EClassCastFlags : ::uint64
    {
        kNone = 0,
        // Phase 4b.4 will add per-subclass bit values here. The base
        // is the only entry shipped in Phase 4b.3.
    };

    // -----------------------------------------------------------------
    // Bitwise operator surface.
    //
    // Strongly-typed enum classes do not implicitly support bitwise
    // composition; we provide the operators explicitly so callers can
    // write `Flags1 | Flags2` without a static_cast at every site.
    // All operators are constexpr so they can fold at compile time
    // (the constinit-emitted FFieldClass aggregates use the OR'd-
    // composition form at the call site).
    // -----------------------------------------------------------------

    [[nodiscard]] constexpr EClassCastFlags operator|(EClassCastFlags Lhs, EClassCastFlags Rhs) noexcept
    {
        return static_cast<EClassCastFlags>(
            static_cast<::uint64>(Lhs) | static_cast<::uint64>(Rhs));
    }

    [[nodiscard]] constexpr EClassCastFlags operator&(EClassCastFlags Lhs, EClassCastFlags Rhs) noexcept
    {
        return static_cast<EClassCastFlags>(
            static_cast<::uint64>(Lhs) & static_cast<::uint64>(Rhs));
    }

    [[nodiscard]] constexpr EClassCastFlags operator^(EClassCastFlags Lhs, EClassCastFlags Rhs) noexcept
    {
        return static_cast<EClassCastFlags>(
            static_cast<::uint64>(Lhs) ^ static_cast<::uint64>(Rhs));
    }

    [[nodiscard]] constexpr EClassCastFlags operator~(EClassCastFlags Operand) noexcept
    {
        return static_cast<EClassCastFlags>(
            ~static_cast<::uint64>(Operand));
    }

    constexpr EClassCastFlags& operator|=(EClassCastFlags& Lhs, EClassCastFlags Rhs) noexcept
    {
        Lhs = Lhs | Rhs;
        return Lhs;
    }

    constexpr EClassCastFlags& operator&=(EClassCastFlags& Lhs, EClassCastFlags Rhs) noexcept
    {
        Lhs = Lhs & Rhs;
        return Lhs;
    }

    constexpr EClassCastFlags& operator^=(EClassCastFlags& Lhs, EClassCastFlags Rhs) noexcept
    {
        Lhs = Lhs ^ Rhs;
        return Lhs;
    }

    // -----------------------------------------------------------------
    // HasAnyCastFlags / HasAllCastFlags helpers.
    //
    // `HasAnyCastFlags(combo, bits)` returns true if any of the bits in
    // `bits` are set in `combo`. `HasAllCastFlags` requires all the
    // bits.
    //
    // Equivalent to UE's `FFieldClass::HasAnyCastFlags` /
    // `FFieldClass::HasAllCastFlags` semantically (`Field.h:151`); the
    // namespace-level form here mirrors the EnumHasAnyFlags pattern
    // XCore-4a uses for ECVarFlags etc.
    // -----------------------------------------------------------------

    [[nodiscard]] constexpr bool HasAnyCastFlags(EClassCastFlags Combined,
                                                  EClassCastFlags FlagsToCheck) noexcept
    {
        return (Combined & FlagsToCheck) != EClassCastFlags::kNone;
    }

    [[nodiscard]] constexpr bool HasAllCastFlags(EClassCastFlags Combined,
                                                  EClassCastFlags FlagsToCheck) noexcept
    {
        return (Combined & FlagsToCheck) == FlagsToCheck;
    }

    // -----------------------------------------------------------------
    // ABI lock: the underlying type MUST be uint64. The FFieldClass
    // layout (§11.2) carries EClassCastFlags at offset 16 occupying
    // 8 bytes; any change to the underlying type breaks the layout.
    // -----------------------------------------------------------------
    static_assert(sizeof(EClassCastFlags) == 8,
                  "EClassCastFlags ABI lock: underlying type must be uint64 "
                  "(8 bytes; matches FFieldClass::CastFlags slot in §11.2)");
    static_assert(::std::is_same_v<::std::underlying_type_t<EClassCastFlags>, ::uint64>,
                  "EClassCastFlags ABI lock: underlying type must be uint64 "
                  "(strongly typed; raw integer access requires explicit cast)");

} // namespace XCore::Reflect
