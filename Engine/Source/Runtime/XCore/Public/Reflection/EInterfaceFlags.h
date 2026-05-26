// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// EInterfaceFlags.h -- per-FInterface trait bitmask (XCore-4b §7.5).
// =====================================================================
//
// XCore-4b Rev 3, Section 7.5 ("FInterface") + Section 11.3 byte
// layout row `InterfaceFlags @ offset 48; uint32`.
//
// EInterfaceFlags is the 32-bit trait bitmask carried on FInterface
// at offset 48 (immediately after the 16-byte InterfaceFunctions
// TArray). MVP set carries the load-bearing posture bits.
//
// Bit assignments:
//
//   INTERFACE_None              0x00
//   INTERFACE_BlueprintImpl     0x01   may be implemented from Blueprint
//   INTERFACE_NativeOnly        0x02   C++-only (rejected from Blueprint)
//   INTERFACE_CannotImplement   0x04   marker / tag interface (no methods)
//
// ABI LOCK: underlying type uint32 (matches FInterface layout @
// offset 48 in a 4-byte slot followed by 4-byte _pad).
//
// =====================================================================

#include "Macros/XCoreTypes.h"

#include <type_traits>

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // EInterfaceFlags -- 32-bit per-FInterface trait bitmask.
    // -----------------------------------------------------------------
    enum class EInterfaceFlags : ::uint32
    {
        INTERFACE_None            = 0U,

        INTERFACE_BlueprintImpl   = 1U << 0,   // 0x01
        INTERFACE_NativeOnly      = 1U << 1,   // 0x02
        INTERFACE_CannotImplement = 1U << 2,   // 0x04
    };

    // -----------------------------------------------------------------
    // Bitwise operator surface.
    // -----------------------------------------------------------------

    [[nodiscard]] constexpr EInterfaceFlags operator|(
        EInterfaceFlags Lhs, EInterfaceFlags Rhs) noexcept
    {
        return static_cast<EInterfaceFlags>(
            static_cast<::uint32>(Lhs) | static_cast<::uint32>(Rhs));
    }

    [[nodiscard]] constexpr EInterfaceFlags operator&(
        EInterfaceFlags Lhs, EInterfaceFlags Rhs) noexcept
    {
        return static_cast<EInterfaceFlags>(
            static_cast<::uint32>(Lhs) & static_cast<::uint32>(Rhs));
    }

    [[nodiscard]] constexpr EInterfaceFlags operator~(EInterfaceFlags Operand) noexcept
    {
        return static_cast<EInterfaceFlags>(~static_cast<::uint32>(Operand));
    }

    constexpr EInterfaceFlags& operator|=(
        EInterfaceFlags& Lhs, EInterfaceFlags Rhs) noexcept
    {
        Lhs = Lhs | Rhs;
        return Lhs;
    }

    constexpr EInterfaceFlags& operator&=(
        EInterfaceFlags& Lhs, EInterfaceFlags Rhs) noexcept
    {
        Lhs = Lhs & Rhs;
        return Lhs;
    }

    // -----------------------------------------------------------------
    // ABI lock.
    // -----------------------------------------------------------------
    static_assert(sizeof(EInterfaceFlags) == 4,
                  "EInterfaceFlags ABI lock: underlying type must be uint32 "
                  "(4 bytes; matches FInterface::InterfaceFlags @ offset 48)");
    static_assert(::std::is_same_v<::std::underlying_type_t<EInterfaceFlags>, ::uint32>,
                  "EInterfaceFlags ABI lock: underlying type must be uint32");

} // namespace XCore::Reflect
