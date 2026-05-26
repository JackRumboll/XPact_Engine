// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// EEditFlags.h -- per-FProperty Editor-tier metadata (XCore-4b §5.3).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.3 ("FProperty base") + Section 11.2 byte
// layout table: `EditFlags @ offset 76` (4 bytes; uint32-backed).
//
// EEditFlags carries the Editor-tier metadata: EditAnywhere,
// EditDefaultsOnly, VisibleAnywhere, etc. The split from EPropertyFlags
// follows the same rationale as EBlueprintFlags -- EPropertyFlags is
// focused on runtime behaviour; the Editor tier's metadata is
// quarantined into its own slot.
//
// XPact's Editor tier (Layer 20) is post-MVP; this enum exists today
// so the FProperty byte layout (§11.2) is correct (4-byte slot at
// offset 76) and so XHT-emitted .gen.cpp populating Editor metadata
// at constinit can resolve the type at the call site. The bit
// assignments are placeholder MVP entries; Layer 20 / Layer 21
// owners will refine the bit-position contract when those tiers ship.
//
// ABI LOCK: the underlying type MUST be uint32. The FProperty layout
// (§11.2) carries EditFlags at offset 76 in a 4-byte slot.
//
// =====================================================================

#include "Macros/XCoreTypes.h"

#include <type_traits>

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // EEditFlags -- 32-bit Editor-tier metadata bitmask.
    // -----------------------------------------------------------------
    enum class EEditFlags : ::uint32
    {
        // No flags set -- the default state.
        EF_None              = 0U,

        // Editable anywhere: archetype + instance both editable.
        EF_EditAnywhere      = 1U << 0,

        // Editable on archetype only (CDO / blueprint default).
        EF_EditDefaultsOnly  = 1U << 1,

        // Editable on instance only (placed actor / specific instance).
        EF_EditInstanceOnly  = 1U << 2,

        // Visible everywhere; not editable.
        EF_VisibleAnywhere   = 1U << 3,

        // Visible on archetype only; not editable.
        EF_VisibleDefaultsOnly = 1U << 4,

        // Visible on instance only; not editable.
        EF_VisibleInstanceOnly = 1U << 5,

        // Advanced display: collapsed under "Advanced" expander.
        EF_AdvancedDisplay   = 1U << 6,

        // Inline-editable (sub-object editable in same details panel).
        EF_EditInline        = 1U << 7,

        // Reserved bits 8-31 (Layer 20 / Layer 21 owners populate).
    };

    // -----------------------------------------------------------------
    // Bitwise operator surface.
    // -----------------------------------------------------------------

    [[nodiscard]] constexpr EEditFlags operator|(EEditFlags Lhs, EEditFlags Rhs) noexcept
    {
        return static_cast<EEditFlags>(
            static_cast<::uint32>(Lhs) | static_cast<::uint32>(Rhs));
    }

    [[nodiscard]] constexpr EEditFlags operator&(EEditFlags Lhs, EEditFlags Rhs) noexcept
    {
        return static_cast<EEditFlags>(
            static_cast<::uint32>(Lhs) & static_cast<::uint32>(Rhs));
    }

    [[nodiscard]] constexpr EEditFlags operator~(EEditFlags Operand) noexcept
    {
        return static_cast<EEditFlags>(~static_cast<::uint32>(Operand));
    }

    constexpr EEditFlags& operator|=(EEditFlags& Lhs, EEditFlags Rhs) noexcept
    {
        Lhs = Lhs | Rhs;
        return Lhs;
    }

    constexpr EEditFlags& operator&=(EEditFlags& Lhs, EEditFlags Rhs) noexcept
    {
        Lhs = Lhs & Rhs;
        return Lhs;
    }

    // -----------------------------------------------------------------
    // ABI lock.
    // -----------------------------------------------------------------
    static_assert(sizeof(EEditFlags) == 4,
                  "EEditFlags ABI lock: underlying type must be uint32 "
                  "(4 bytes; matches FProperty::EditFlags @ offset 76)");
    static_assert(::std::is_same_v<::std::underlying_type_t<EEditFlags>, ::uint32>,
                  "EEditFlags ABI lock: underlying type must be uint32");

} // namespace XCore::Reflect
