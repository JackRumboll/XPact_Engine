// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// EStructFlags.h -- per-FStruct trait bitmask (XCore-4b §7.1).
// =====================================================================
//
// XCore-4b Rev 3, Section 7.1 ("FStruct (base)") + Section 11.3 byte
// layout row `StructFlags @ offset 30; uint8`.
//
// EStructFlags is the 8-bit trait bitmask carried directly on FStruct
// at offset 30 (immediately after MinAlignment + before the explicit
// 1-byte pad to the 8-byte boundary). Single byte was chosen by spec
// because the actively-used struct-level traits fit in 8 bits and the
// remaining byte is free padding anyway -- larger storage would shift
// PropertyLink off offset 32 without buying anything.
//
// Bit assignments mirror UE's `EStructFlags` (`Class.h:185` area)
// trimmed to the MVP subset:
//
//   STRUCT_None              0x00   no flags
//   STRUCT_Native            0x01   declared in C++ (not Blueprint)
//   STRUCT_IdenticalNative   0x02   has C++ Identical handler in CppStructOps
//   STRUCT_HasInstancedReference  0x04  contains an instanced subobject reference
//   STRUCT_NoExport          0x08   no UHT export (XPact: no XHT export)
//   STRUCT_Atomic            0x10   atomic for serialization (cannot split mid-struct)
//   STRUCT_Immutable         0x20   cannot be modified after construction
//   STRUCT_PostSerializeNative   0x40  has C++ PostSerialize handler
//   STRUCT_SerializeNative   0x80   has C++ Serialize handler (FArchive bridge)
//
// ABI LOCK: underlying type uint8_t (matches FStruct layout @ offset 30).
//
// =====================================================================

#include "Macros/XCoreTypes.h"

#include <type_traits>

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // EStructFlags -- 8-bit per-FStruct trait bitmask.
    // -----------------------------------------------------------------
    enum class EStructFlags : ::uint8
    {
        STRUCT_None                  = 0,

        STRUCT_Native                = 1U << 0,   // 0x01
        STRUCT_IdenticalNative       = 1U << 1,   // 0x02
        STRUCT_HasInstancedReference = 1U << 2,   // 0x04
        STRUCT_NoExport              = 1U << 3,   // 0x08
        STRUCT_Atomic                = 1U << 4,   // 0x10
        STRUCT_Immutable             = 1U << 5,   // 0x20
        STRUCT_PostSerializeNative   = 1U << 6,   // 0x40
        STRUCT_SerializeNative       = 1U << 7,   // 0x80
    };

    // -----------------------------------------------------------------
    // Bitwise operator surface.
    // -----------------------------------------------------------------

    [[nodiscard]] constexpr EStructFlags operator|(EStructFlags Lhs, EStructFlags Rhs) noexcept
    {
        return static_cast<EStructFlags>(
            static_cast<::uint8>(static_cast<::uint32>(Lhs) | static_cast<::uint32>(Rhs)));
    }

    [[nodiscard]] constexpr EStructFlags operator&(EStructFlags Lhs, EStructFlags Rhs) noexcept
    {
        return static_cast<EStructFlags>(
            static_cast<::uint8>(static_cast<::uint32>(Lhs) & static_cast<::uint32>(Rhs)));
    }

    [[nodiscard]] constexpr EStructFlags operator~(EStructFlags Operand) noexcept
    {
        return static_cast<EStructFlags>(
            static_cast<::uint8>(~static_cast<::uint32>(Operand) & 0xFFU));
    }

    constexpr EStructFlags& operator|=(EStructFlags& Lhs, EStructFlags Rhs) noexcept
    {
        Lhs = Lhs | Rhs;
        return Lhs;
    }

    constexpr EStructFlags& operator&=(EStructFlags& Lhs, EStructFlags Rhs) noexcept
    {
        Lhs = Lhs & Rhs;
        return Lhs;
    }

    // -----------------------------------------------------------------
    // ABI lock.
    // -----------------------------------------------------------------
    static_assert(sizeof(EStructFlags) == 1,
                  "EStructFlags ABI lock: underlying type must be uint8 "
                  "(1 byte; matches FStruct::StructFlags @ offset 30)");
    static_assert(::std::is_same_v<::std::underlying_type_t<EStructFlags>, ::uint8>,
                  "EStructFlags ABI lock: underlying type must be uint8");

} // namespace XCore::Reflect
