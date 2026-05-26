// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FCustomVersion.cpp -- byte-level serialization helpers.
// =====================================================================
//
// XCore-4b Rev 3, Section 8.1. The struct itself is defined in the
// header; this TU only carries the WriteToBuffer / ReadFromBuffer
// byte helpers because they include a tiny amount of explicit-endianness
// scaffolding that does not need to be inlined into every consumer.
//
// On-the-wire byte order (matching the XCore-4a §11.4 loctable header
// discipline): little-endian for every integer field. The host targets
// are all little-endian today (Win64 x86_64, Linux x86_64, Android
// ARM64), so this is a no-op on every supported platform; the explicit
// byte helpers exist to make the contract independent of host
// endianness for future-proofing and to make the on-disk format
// inspectable byte-by-byte.
//
// =====================================================================

#include "Reflection/FCustomVersion.h"

#include "Macros/XCoreTypes.h"

namespace
{
    // -----------------------------------------------------------------
    // Tiny header-private little-endian helpers. Mirrors the pattern in
    // Private/Internationalization/LocTableBytewise.h but kept local
    // to this TU because (a) the FCustomVersion file count is small,
    // (b) the helpers would otherwise need a dedicated header just for
    // this one TU.
    // -----------------------------------------------------------------

    inline void WriteU32Le(::uint8* OutBuffer, ::uint32 Value) noexcept
    {
        OutBuffer[0] = static_cast<::uint8>( Value        & 0xFFu);
        OutBuffer[1] = static_cast<::uint8>((Value >>  8) & 0xFFu);
        OutBuffer[2] = static_cast<::uint8>((Value >> 16) & 0xFFu);
        OutBuffer[3] = static_cast<::uint8>((Value >> 24) & 0xFFu);
    }

    inline ::uint32 ReadU32Le(const ::uint8* InBuffer) noexcept
    {
        return  static_cast<::uint32>(InBuffer[0])
             | (static_cast<::uint32>(InBuffer[1]) <<  8)
             | (static_cast<::uint32>(InBuffer[2]) << 16)
             | (static_cast<::uint32>(InBuffer[3]) << 24);
    }

    inline void WriteI32Le(::uint8* OutBuffer, ::int32 Value) noexcept
    {
        WriteU32Le(OutBuffer, static_cast<::uint32>(Value));
    }

    inline ::int32 ReadI32Le(const ::uint8* InBuffer) noexcept
    {
        return static_cast<::int32>(ReadU32Le(InBuffer));
    }
} // anonymous namespace

namespace XCore::Reflect
{

void FCustomVersion::WriteToBuffer(::uint8* OutBuffer) const noexcept
{
    // Layout (byte offsets in OutBuffer):
    //   [0..15]   Key (4 x uint32 little-endian)
    //   [16..19]  Version (int32 little-endian)
    //   [20..23]  _Reserved (uint32 little-endian)
    //   [24..27]  FriendlyName.Index (uint32 little-endian)
    //   [28..31]  FriendlyName.SerialNumber (uint32 little-endian)
    WriteU32Le(OutBuffer +  0, Key.A);
    WriteU32Le(OutBuffer +  4, Key.B);
    WriteU32Le(OutBuffer +  8, Key.C);
    WriteU32Le(OutBuffer + 12, Key.D);
    WriteI32Le(OutBuffer + 16, Version);
    WriteU32Le(OutBuffer + 20, _Reserved);
    WriteU32Le(OutBuffer + 24, FriendlyName.Index);
    WriteU32Le(OutBuffer + 28, FriendlyName.SerialNumber);
}

FCustomVersion FCustomVersion::ReadFromBuffer(const ::uint8* InBuffer) noexcept
{
    FCustomVersion Result;
    Result.Key.A                       = ReadU32Le(InBuffer +  0);
    Result.Key.B                       = ReadU32Le(InBuffer +  4);
    Result.Key.C                       = ReadU32Le(InBuffer +  8);
    Result.Key.D                       = ReadU32Le(InBuffer + 12);
    Result.Version                     = ReadI32Le(InBuffer + 16);
    Result._Reserved                   = ReadU32Le(InBuffer + 20);
    Result.FriendlyName.Index          = ReadU32Le(InBuffer + 24);
    Result.FriendlyName.SerialNumber   = ReadU32Le(InBuffer + 28);
    return Result;
}

} // namespace XCore::Reflect
