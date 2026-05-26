// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// EBlueprintFlags.h -- per-FProperty Blueprint metadata (XCore-4b §5.3).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.3 ("FProperty base") + Section 11.2 byte
// layout table: `BlueprintFlags @ offset 72` (4 bytes; uint32-backed).
//
// EBlueprintFlags carries the Blueprint-tier metadata for a property
// that does NOT belong in EPropertyFlags. The split rationale:
//
//   * EPropertyFlags  -- low-level engine behaviour (replication,
//                        persistence, C++ semantics) that the runtime
//                        REFLECTS UPON at every dispatch.
//   * EBlueprintFlags -- higher-level Blueprint visibility / category
//                        / accessibility metadata that the Editor +
//                        Blueprint VM consult. Layered out of
//                        EPropertyFlags so the 64-bit EPropertyFlags
//                        slot stays focused on runtime behaviour.
//
// XPact's Editor + Blueprint tiers are post-MVP; this enum exists
// today so the FProperty byte layout (§11.2) is correct (4-byte slot
// at offset 72) and so XHT-emitted .gen.cpp populating Blueprint
// metadata at constinit can resolve the type at the call site. The
// bit assignments are placeholder MVP entries; Layer 20 / Layer 21
// owners will refine the bit-position contract when those tiers ship.
//
// ABI LOCK: the underlying type MUST be uint32. The FProperty layout
// (§11.2) carries BlueprintFlags at offset 72 in a 4-byte slot; any
// change to the underlying type breaks the layout.
//
// =====================================================================

#include "Macros/XCoreTypes.h"

#include <type_traits>

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // EBlueprintFlags -- 32-bit Blueprint metadata bitmask.
    // -----------------------------------------------------------------
    enum class EBlueprintFlags : ::uint32
    {
        // No flags set -- the default state.
        BPF_None              = 0U,

        // Read/Write surface from Blueprint graph.
        BPF_BlueprintReadWrite = 1U << 0,

        // Assignable from Blueprint graph (delegate properties).
        BPF_BlueprintAssignable = 1U << 1,

        // Callable from Blueprint graph (event-dispatcher properties).
        BPF_BlueprintCallable = 1U << 2,

        // Pure: no side effects; Blueprint VM may cache.
        BPF_BlueprintPure     = 1U << 3,

        // The property declares a category (the actual category name
        // is stored separately in metadata; this bit is the "has a
        // category" gate).
        BPF_HasCategory       = 1U << 4,

        // Latent: kicks off async work; resumes on a future tick.
        BPF_Latent            = 1U << 5,

        // Hidden from default Blueprint search (queryable via API).
        BPF_HiddenFromSearch  = 1U << 6,

        // Reserved bits 7-31 (Layer 20 / Layer 21 owners populate).
    };

    // -----------------------------------------------------------------
    // Bitwise operator surface.
    // -----------------------------------------------------------------

    [[nodiscard]] constexpr EBlueprintFlags operator|(EBlueprintFlags Lhs, EBlueprintFlags Rhs) noexcept
    {
        return static_cast<EBlueprintFlags>(
            static_cast<::uint32>(Lhs) | static_cast<::uint32>(Rhs));
    }

    [[nodiscard]] constexpr EBlueprintFlags operator&(EBlueprintFlags Lhs, EBlueprintFlags Rhs) noexcept
    {
        return static_cast<EBlueprintFlags>(
            static_cast<::uint32>(Lhs) & static_cast<::uint32>(Rhs));
    }

    [[nodiscard]] constexpr EBlueprintFlags operator~(EBlueprintFlags Operand) noexcept
    {
        return static_cast<EBlueprintFlags>(~static_cast<::uint32>(Operand));
    }

    constexpr EBlueprintFlags& operator|=(EBlueprintFlags& Lhs, EBlueprintFlags Rhs) noexcept
    {
        Lhs = Lhs | Rhs;
        return Lhs;
    }

    constexpr EBlueprintFlags& operator&=(EBlueprintFlags& Lhs, EBlueprintFlags Rhs) noexcept
    {
        Lhs = Lhs & Rhs;
        return Lhs;
    }

    // -----------------------------------------------------------------
    // ABI lock.
    // -----------------------------------------------------------------
    static_assert(sizeof(EBlueprintFlags) == 4,
                  "EBlueprintFlags ABI lock: underlying type must be uint32 "
                  "(4 bytes; matches FProperty::BlueprintFlags @ offset 72)");
    static_assert(::std::is_same_v<::std::underlying_type_t<EBlueprintFlags>, ::uint32>,
                  "EBlueprintFlags ABI lock: underlying type must be uint32");

} // namespace XCore::Reflect
