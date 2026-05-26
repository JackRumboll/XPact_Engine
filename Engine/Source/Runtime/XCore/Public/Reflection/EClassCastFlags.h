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

        // -------------------------------------------------------------
        // Phase 4b.4a primitive FProperty subclass bits.
        //
        // Per spec §5.5 table: each FProperty subclass owns one
        // dedicated bit; the IsA acceleration in
        // `FField::IsA(FFieldClass*)` becomes a single AND instruction
        // (the target's bit AND'd with the candidate class's CastFlags
        // -- nonzero means "is or descends from").
        //
        // The bit assignments below match §5.5 verbatim and are locked
        // in the Stage B addendum (XPACT_FPROPERTY_LAYOUT_TAG). Any
        // patch DLL that uses CastFlags values directly must match.
        //
        // Phase 4b.4a delivers the 16 PRIMITIVE subclasses (bits 0x01
        // through 0x4000 + 0x80000). The remaining subclasses
        // (Object, Weak, Soft, Struct, Array, Map, Set, Interface,
        // Class, Delegate, Multicast variants) and their bits land in
        // Phase 4b.4b. Their bit assignments are RESERVED here to
        // preserve the §5.5 contract; Phase 4b.4b will populate them.
        // -------------------------------------------------------------

        // Primitive numeric + boolean (bits 0x01-0x400).
        kFBoolProperty            = 1ULL <<  0,   // 0x0000000000000001
        kFByteProperty            = 1ULL <<  1,   // 0x0000000000000002
        kFInt8Property            = 1ULL <<  2,   // 0x0000000000000004
        kFInt16Property           = 1ULL <<  3,   // 0x0000000000000008
        kFIntProperty             = 1ULL <<  4,   // 0x0000000000000010 (spec name FIntProperty; alias for int32)
        kFInt64Property           = 1ULL <<  5,   // 0x0000000000000020
        kFUInt16Property          = 1ULL <<  6,   // 0x0000000000000040
        kFUInt32Property          = 1ULL <<  7,   // 0x0000000000000080
        kFUInt64Property          = 1ULL <<  8,   // 0x0000000000000100
        kFFloatProperty           = 1ULL <<  9,   // 0x0000000000000200
        kFDoubleProperty          = 1ULL << 10,   // 0x0000000000000400

        // String + name + text (bits 0x800 / 0x1000 / 0x1000000).
        kFStrProperty             = 1ULL << 11,   // 0x0000000000000800
        kFNameProperty            = 1ULL << 12,   // 0x0000000000001000

        // -------------------------------------------------------------
        // Reserved bits for Phase 4b.4b (object / ref / container /
        // delegate subclasses). The bit positions match §5.5 and MUST
        // NOT be reused by Phase 4b.4a. Listed as DECLARED but unused
        // here so Phase 4b.4b can populate them without renumbering
        // the Phase 4b.4a-shipped bits.
        // -------------------------------------------------------------
        // kFObjectProperty                = 1ULL << 13,   // 0x0000000000002000
        // kFWeakObjectProperty            = 1ULL << 14,   // 0x0000000000004000
        // kFStructProperty                = 1ULL << 15,   // 0x0000000000008000
        // kFArrayProperty                 = 1ULL << 16,   // 0x0000000000010000
        // kFMapProperty                   = 1ULL << 17,   // 0x0000000000020000
        // kFSetProperty                   = 1ULL << 18,   // 0x0000000000040000
        // [kFEnumProperty assigned bit 19 below; though FEnumProperty
        //  semantically depends on FEnum (Phase 4b.5), the EnumProperty
        //  FProperty subclass itself is shipped at Phase 4b.4a so the
        //  primitive type-family is complete; only the FEnum descriptor
        //  it points at is deferred.]
        kFEnumProperty            = 1ULL << 19,   // 0x0000000000080000

        // FInterfaceProperty / FClassProperty / FDelegateProperty +
        // multicast variants -- Phase 4b.4b.
        // kFInterfaceProperty             = 1ULL << 20,   // 0x0000000000100000
        // kFClassProperty                 = 1ULL << 21,   // 0x0000000000200000
        // kFDelegateProperty              = 1ULL << 22,   // 0x0000000000400000

        // FTextProperty -- Rev 2 MVP add per FIX-3. Distinct bit
        // position per §5.5 (bit 24 = 0x1000000).
        kFTextProperty            = 1ULL << 24,   // 0x0000000001000000

        // FSoftObjectProperty / FSoftClassProperty -- Phase 4b.4b
        // (Rev 2 MVP add per FIX-3).
        // kFSoftObjectProperty           = 1ULL << 25,   // 0x0000000002000000
        // kFSoftClassProperty            = 1ULL << 26,   // 0x0000000004000000

        // FMulticastInline/Sparse -- Phase 4b.4b (Rev 2 MVP add per
        // FIX-3).
        // kFMulticastInlineDelegateProperty   = 1ULL << 27,   // 0x0000000008000000
        // kFMulticastSparseDelegateProperty   = 1ULL << 28,   // 0x0000000010000000

        // -------------------------------------------------------------
        // FProperty parent bit.
        //
        // Every FProperty SUBCLASS sets bit kFProperty in its own
        // CastFlags so a single AND test against kFProperty answers
        // "is this any FProperty?" without walking the SuperClass
        // chain. The bit position is high enough to avoid colliding
        // with any subclass bit (bits 0-28 are reserved for §5.5
        // subclass slots; bit 29 is the parent gate).
        //
        // Discipline: every FProperty subclass's FFieldClass.CastFlags
        // MUST include kFProperty | <its own bit>. The IsA fast path
        // becomes:
        //
        //   bool IsAFProperty = (Field->GetClass()->CastFlags &
        //                        EClassCastFlags::kFProperty) != kNone;
        //
        // -- one AND, one compare, branch-free.
        // -------------------------------------------------------------
        kFProperty                = 1ULL << 29,   // 0x0000000020000000

        // -------------------------------------------------------------
        // Reserved bits 30-63.
        //
        // Bit positions 30-36 are reserved for Phase 4b.4b subclass
        // additions (the post-MVP reservations per §5.5: FFieldPath,
        // FLazyObject, FOptional, FUtf8Str, FAnsiStr).
        //
        // Bits 37-63 are RESERVED for future XPact-only FField
        // subclasses (e.g., FFunctionDescriptor planned post-MVP).
        // -------------------------------------------------------------
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
