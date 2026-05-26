// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// EEnumFlags.h -- per-FEnum trait bitmask (XCore-4b §7.4).
// =====================================================================
//
// XCore-4b Rev 3, Section 7.4 ("FEnum") + Section 11.3 byte layout
// row `EnumFlags @ offset 32; uint32`.
//
// EEnumFlags is the 32-bit trait bitmask carried on FEnum at offset
// 32 (immediately after the FField base). Mirrors UE's `EEnumFlags`
// (`Class.h:2877` area) trimmed to the MVP subset.
//
// Bit assignments:
//
//   ENUM_None         0x00
//   ENUM_Flags        0x01   the enum is a bitflag enum (each value is a 1<<N bit)
//   ENUM_NewType      0x02   the enum was declared post-Rev-1; XHT consumers
//                            may need to handle missing legacy values
//
// ABI LOCK: underlying type uint32 (matches FEnum layout @ offset 32
// in a 4-byte slot followed by 4-byte _pad).
//
// =====================================================================

#include "Macros/XCoreTypes.h"

#include <type_traits>

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // EEnumFlags -- 32-bit per-FEnum trait bitmask.
    // -----------------------------------------------------------------
    enum class EEnumFlags : ::uint32
    {
        ENUM_None    = 0U,

        ENUM_Flags   = 1U << 0,   // 0x01
        ENUM_NewType = 1U << 1,   // 0x02
    };

    // -----------------------------------------------------------------
    // Bitwise operator surface.
    // -----------------------------------------------------------------

    [[nodiscard]] constexpr EEnumFlags operator|(EEnumFlags Lhs, EEnumFlags Rhs) noexcept
    {
        return static_cast<EEnumFlags>(
            static_cast<::uint32>(Lhs) | static_cast<::uint32>(Rhs));
    }

    [[nodiscard]] constexpr EEnumFlags operator&(EEnumFlags Lhs, EEnumFlags Rhs) noexcept
    {
        return static_cast<EEnumFlags>(
            static_cast<::uint32>(Lhs) & static_cast<::uint32>(Rhs));
    }

    [[nodiscard]] constexpr EEnumFlags operator~(EEnumFlags Operand) noexcept
    {
        return static_cast<EEnumFlags>(~static_cast<::uint32>(Operand));
    }

    constexpr EEnumFlags& operator|=(EEnumFlags& Lhs, EEnumFlags Rhs) noexcept
    {
        Lhs = Lhs | Rhs;
        return Lhs;
    }

    constexpr EEnumFlags& operator&=(EEnumFlags& Lhs, EEnumFlags Rhs) noexcept
    {
        Lhs = Lhs & Rhs;
        return Lhs;
    }

    // -----------------------------------------------------------------
    // ABI lock.
    // -----------------------------------------------------------------
    static_assert(sizeof(EEnumFlags) == 4,
                  "EEnumFlags ABI lock: underlying type must be uint32 "
                  "(4 bytes; matches FEnum::EnumFlags @ offset 32)");
    static_assert(::std::is_same_v<::std::underlying_type_t<EEnumFlags>, ::uint32>,
                  "EEnumFlags ABI lock: underlying type must be uint32");

} // namespace XCore::Reflect
