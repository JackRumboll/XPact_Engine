// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// LocTableBytewise.h -- header-private little-endian byte helpers.
// =====================================================================
//
// Used by FLocTable.cpp + FLocTableLoader.cpp. Lives in Private/ so it
// is not part of the public XCore-4a surface; consumers outside the
// Internationalization subsystem MUST NOT include this header.
//
// All multi-byte loctable fields are written little-endian regardless
// of host endianness. The helpers below are pure byte-level (no <bit>
// / std::endian dependence), so the same code compiles bit-exact on
// every supported target. On the LE platforms XPact ships, the
// helpers compile to memcpy + no swap; on a hypothetical BE target
// they would swap explicitly.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include <cstdint>

namespace XCore::Loc::Bytewise
{
    XPACT_FORCEINLINE void WriteU16Le(::uint8* Dst, ::std::uint16_t V) noexcept
    {
        Dst[0] = static_cast<::uint8>(V & 0xFFu);
        Dst[1] = static_cast<::uint8>((V >> 8) & 0xFFu);
    }

    XPACT_FORCEINLINE void WriteU32Le(::uint8* Dst, ::std::uint32_t V) noexcept
    {
        Dst[0] = static_cast<::uint8>(V & 0xFFu);
        Dst[1] = static_cast<::uint8>((V >> 8)  & 0xFFu);
        Dst[2] = static_cast<::uint8>((V >> 16) & 0xFFu);
        Dst[3] = static_cast<::uint8>((V >> 24) & 0xFFu);
    }

    XPACT_FORCEINLINE void WriteU64Le(::uint8* Dst, ::std::uint64_t V) noexcept
    {
        for (int I = 0; I < 8; ++I)
        {
            Dst[I] = static_cast<::uint8>((V >> (I * 8)) & 0xFFu);
        }
    }

    [[nodiscard]] XPACT_FORCEINLINE ::std::uint16_t ReadU16Le(const ::uint8* Src) noexcept
    {
        return static_cast<::std::uint16_t>(
            static_cast<::std::uint16_t>(Src[0]) |
            (static_cast<::std::uint16_t>(Src[1]) << 8));
    }

    [[nodiscard]] XPACT_FORCEINLINE ::std::uint32_t ReadU32Le(const ::uint8* Src) noexcept
    {
        return static_cast<::std::uint32_t>(Src[0]) |
               (static_cast<::std::uint32_t>(Src[1]) << 8) |
               (static_cast<::std::uint32_t>(Src[2]) << 16) |
               (static_cast<::std::uint32_t>(Src[3]) << 24);
    }

    [[nodiscard]] XPACT_FORCEINLINE ::std::uint64_t ReadU64Le(const ::uint8* Src) noexcept
    {
        ::std::uint64_t V = 0;
        for (int I = 0; I < 8; ++I)
        {
            V |= (static_cast<::std::uint64_t>(Src[I]) << (I * 8));
        }
        return V;
    }
} // namespace XCore::Loc::Bytewise
