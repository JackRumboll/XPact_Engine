// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// ECVarFlags.h -- per-CVar feature flag bitmask (Section 9.1).
// =====================================================================
//
// XCore-4a Rev 3, Section 9.1 wording:
//   "virtual ECVarFlags GetFlags() const noexcept = 0;
//    // ReadOnly | Cheat | Scalability | RenderThreadSafe | SimPathSafe"
//
// The five flags map onto UE's EConsoleVariableFlags subset that
// XPact actually consumes:
//
//   * ReadOnly        -- the CVar value cannot be Set* at runtime
//                        after registration; only the registration
//                        default holds. Useful for build-config-baked
//                        constants exposed to the in-game console for
//                        inspection.
//   * Cheat           -- the CVar is gated by the "cheats enabled"
//                        runtime gate; the console parser rejects Set*
//                        calls when cheats are off (Shipping is always
//                        off; Dev builds toggleable).
//   * Scalability     -- the CVar is part of the scalability group
//                        (UE: ECVF_Scalability). The scalability sink
//                        reads these on platform-tier change.
//   * RenderThreadSafe-- the CVar value is shadowed on the render
//                        thread (per-frame mirror copied at frame
//                        boundary). UE: ECVF_RenderThreadSafe.
//   * SimPathSafe     -- the CVar is safe to read from sim-path TUs
//                        (XPACT_SIMPATH = 1). Non-safe CVars are
//                        compile-error to read in sim-path TUs via a
//                        deprecation diagnostic on the per-CVar
//                        accessor. Closes UE's "CVar changed mid-
//                        frame between sim steps" footgun by
//                        construction.
//
// UE divergence: UE packs SetByPriority and feature-flags into one
// EConsoleVariableFlags uint32 (HAL/IConsoleManager.h:63). XPact
// splits them: ECVarSetByPriority for the priority cascade,
// ECVarFlags for the feature bits. The split clarifies the two
// concerns and avoids the "what does this hex constant mean?" debug
// surface that UE's combined ECVF_* mask creates.
//
// =====================================================================

#include "Macros/XCoreTypes.h"

#include <type_traits>

namespace XCore::Misc
{

    // -----------------------------------------------------------------
    // ECVarFlags -- the feature-flag bitmask.
    //
    // uint8-backed because the five-flag surface fits in a single
    // byte. Future Phase 2 additions consume the reserved upper bits
    // (3 unused bits remain at uint8); a sixth flag is a MINOR ABI
    // bump under the same XPACT_CVAR_ABI_TAG envelope.
    //
    // The enum is unsigned, scoped, and the underlying type is fixed
    // to uint8 so the IL2CPP marshalling surface treats it as a
    // System.Byte at the C# boundary.
    // -----------------------------------------------------------------
    enum class ECVarFlags : ::uint8
    {
        Default          = 0,
        ReadOnly         = 1 << 0,   // 0x01
        Cheat            = 1 << 1,   // 0x02
        Scalability      = 1 << 2,   // 0x04
        RenderThreadSafe = 1 << 3,   // 0x08
        SimPathSafe      = 1 << 4,   // 0x10
        // Bits 5..7 reserved for future expansion.
    };

    static_assert(sizeof(ECVarFlags) == 1, "ECVarFlags ABI lock: 1 byte");

    // -----------------------------------------------------------------
    // Bitwise operators on a scoped enum require explicit overloads
    // (C++20 still doesn't auto-generate them for `enum class`).
    //
    // We provide |, &, ~, |=, &= for ergonomic composition:
    //
    //   ECVarFlags Flags = ECVarFlags::ReadOnly | ECVarFlags::SimPathSafe;
    //   if ((Flags & ECVarFlags::ReadOnly) != ECVarFlags::Default) { ... }
    //
    // The operators are constexpr so they fold at compile time when
    // the operands are constants -- the registration-time flag
    // composition is the common case and folds entirely.
    // -----------------------------------------------------------------

    [[nodiscard]] constexpr ECVarFlags operator|(ECVarFlags Lhs, ECVarFlags Rhs) noexcept
    {
        using U = ::std::underlying_type_t<ECVarFlags>;
        return static_cast<ECVarFlags>(static_cast<U>(Lhs) | static_cast<U>(Rhs));
    }

    [[nodiscard]] constexpr ECVarFlags operator&(ECVarFlags Lhs, ECVarFlags Rhs) noexcept
    {
        using U = ::std::underlying_type_t<ECVarFlags>;
        return static_cast<ECVarFlags>(static_cast<U>(Lhs) & static_cast<U>(Rhs));
    }

    [[nodiscard]] constexpr ECVarFlags operator~(ECVarFlags Value) noexcept
    {
        using U = ::std::underlying_type_t<ECVarFlags>;
        // Mask to the active bit range so the result stays uint8 and
        // doesn't surface stray bits.
        return static_cast<ECVarFlags>(static_cast<U>(~static_cast<U>(Value)) & 0x1Fu);
    }

    constexpr ECVarFlags& operator|=(ECVarFlags& Lhs, ECVarFlags Rhs) noexcept
    {
        Lhs = Lhs | Rhs;
        return Lhs;
    }

    constexpr ECVarFlags& operator&=(ECVarFlags& Lhs, ECVarFlags Rhs) noexcept
    {
        Lhs = Lhs & Rhs;
        return Lhs;
    }

    // -----------------------------------------------------------------
    // HasFlag -- ergonomic predicate for "is bit set?".
    //
    // Returns true iff every bit set in Mask is also set in Value.
    // Mask is typically a single flag.
    // -----------------------------------------------------------------
    [[nodiscard]] constexpr bool HasFlag(ECVarFlags Value, ECVarFlags Mask) noexcept
    {
        return (Value & Mask) == Mask;
    }

} // namespace XCore::Misc
