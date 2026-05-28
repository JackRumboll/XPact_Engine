// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FLocTableLoader.cpp -- loctable file I/O (Section 11.2 + 18 OPEN-5).
// =====================================================================
//
// XCore-4a Rev 3 Section 11.2 + Section 18 OPEN-5 implementation.
//
// Reads a binary loctable from disk, verifies the BLAKE3 payload
// digest, and parses the entries into a TArray<FLocEntry>. The
// inverse (SaveToFile) is provided for test fixture generation; the
// runtime engine only calls LoadFromFile.
//
// The file I/O uses <cstdio> (fopen / fread / fwrite / fclose). This
// is the most portable surface for a one-shot read of a small binary
// file (loctables are kilobytes-scale; no streaming required). The
// alternative -- a custom FFileHandle abstraction -- belongs to a
// future XSerialization module (Section 1.2 of the spec deferring
// FArchive / FOutputDevice / FBitReader / FBitWriter to that module);
// XCore-4a's loctable I/O is the only file-read in the entire System
// 3 surface, so we use std stdio directly rather than building a HAL
// just for this one call.
//
// =====================================================================

#include "Internationalization/FLocTableLoader.h"
#include "Internationalization/FLocTable.h"

#include "LocTableBytewise.h"

#include "Hash/FBlake3.h"
#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"

#include <cstdio>
#include <cstring>

namespace XCore::Loc
{

// ---------------------------------------------------------------------
// LoadFromFile -- read + verify + parse a loctable.
//
// Behaviour:
//   1. Open the file in binary mode. Empty / nonexistent file ->
//      FParseError::Empty.
//   2. Read the entire file into a heap buffer (loctables are small).
//   3. Validate fixed header: magic + version + flags-zero. Mismatch ->
//      FParseError::Malformed.
//   4. BLAKE3-hash bytes [40 .. EOF) and compare to bytes [8 .. 40).
//      Mismatch -> FParseError::Malformed (digest tamper).
//   5. Read EntryCount (uint64 at offset 40).
//   6. For each entry: read NamespaceLen (u16), Namespace bytes,
//      KeyLen (u16), Key bytes, ValueLen (u32), Value bytes.
//      Each step bounds-checks against the remaining buffer; out-of-
//      bounds -> FParseError::Malformed.
//   7. Emit each entry as an FLocEntry with FString-owned bytes.
// ---------------------------------------------------------------------
::XCore::Result<
    ::XCore::TArray<FLocTableLoader::FLocEntry, ::XCore::DefaultAllocator>,
    ::XCore::FParseError>
FLocTableLoader::LoadFromFile(const ::XCore::FString& Path)
{
    using ResultT = ::XCore::Result<
        ::XCore::TArray<FLocTableLoader::FLocEntry, ::XCore::DefaultAllocator>,
        ::XCore::FParseError>;

    // -----------------------------------------------------------------
    // Open + size.
    // -----------------------------------------------------------------
    std::FILE* Fp = std::fopen(Path.ToUtf8Cstr(), "rb");
    if (Fp == nullptr)
    {
        return ::XCore::Unexpected(::XCore::FParseError::Empty);
    }

    if (std::fseek(Fp, 0, SEEK_END) != 0)
    {
        std::fclose(Fp);
        return ::XCore::Unexpected(::XCore::FParseError::Malformed);
    }
    long FileLen = std::ftell(Fp);
    if (FileLen < 0)
    {
        std::fclose(Fp);
        return ::XCore::Unexpected(::XCore::FParseError::Malformed);
    }
    if (FileLen == 0)
    {
        std::fclose(Fp);
        return ::XCore::Unexpected(::XCore::FParseError::Empty);
    }
    if (static_cast<::SIZE_T>(FileLen) < kLoctableHeaderSize + 8u)
    {
        // Below the header + EntryCount minimum (40 + 8 = 48 bytes).
        std::fclose(Fp);
        return ::XCore::Unexpected(::XCore::FParseError::Malformed);
    }

    std::fseek(Fp, 0, SEEK_SET);

    // -----------------------------------------------------------------
    // Read entire file into a heap buffer.
    // -----------------------------------------------------------------
    void* Buffer = ::XCore::HAL::FMemory::MallocOrAbort(
        static_cast<::SIZE_T>(FileLen),
        /*Align=*/1,
        ::XCore::HAL::FMemTag::Localization);
    ::uint8* Bytes = static_cast<::uint8*>(Buffer);

    ::SIZE_T BytesRead = std::fread(Bytes, 1, static_cast<::SIZE_T>(FileLen), Fp);
    std::fclose(Fp);

    if (BytesRead != static_cast<::SIZE_T>(FileLen))
    {
        ::XCore::HAL::FMemory::Free(Buffer);
        return ::XCore::Unexpected(::XCore::FParseError::Malformed);
    }

    // -----------------------------------------------------------------
    // Header validation: magic + version + flags-zero.
    // -----------------------------------------------------------------
    const ::std::uint32_t Magic = Bytewise::ReadU32Le(Bytes + 0);
    if (Magic != kLoctableMagic)
    {
        ::XCore::HAL::FMemory::Free(Buffer);
        return ::XCore::Unexpected(::XCore::FParseError::Malformed);
    }

    const ::std::uint16_t Version = Bytewise::ReadU16Le(Bytes + 4);
    if (Version != kLoctableVersion)
    {
        ::XCore::HAL::FMemory::Free(Buffer);
        return ::XCore::Unexpected(::XCore::FParseError::Malformed);
    }

    const ::std::uint16_t Flags = Bytewise::ReadU16Le(Bytes + 6);
    if (Flags != kLoctableFlagsReserved)
    {
        ::XCore::HAL::FMemory::Free(Buffer);
        return ::XCore::Unexpected(::XCore::FParseError::Malformed);
    }

    // -----------------------------------------------------------------
    // BLAKE3 verification: digest at [8 .. 40); payload at [40 .. EOF).
    // -----------------------------------------------------------------
    ::XCore::Hash::FBlake3Digest StoredDigest;
    std::memcpy(StoredDigest.Bytes, Bytes + 8, 32);

    const ::SIZE_T PayloadLen = static_cast<::SIZE_T>(FileLen) - kLoctableHeaderSize;
    const ::XCore::Hash::FBlake3Digest ComputedDigest =
        ::XCore::Hash::FBlake3::Compute(Bytes + kLoctableHeaderSize, PayloadLen);

    if (!(StoredDigest == ComputedDigest))
    {
        ::XCore::HAL::FMemory::Free(Buffer);
        return ::XCore::Unexpected(::XCore::FParseError::Malformed);
    }

    // -----------------------------------------------------------------
    // Read EntryCount at offset 40.
    // -----------------------------------------------------------------
    const ::std::uint64_t EntryCount = Bytewise::ReadU64Le(Bytes + kLoctableHeaderSize);

    // Parse each entry.
    // Uniform initialization (braces) avoids C++ most-vexing-parse: a
    // parenthesized argument `DefaultAllocator(::XCore::HAL::FMemTag::Localization)`
    // gets parsed as a function declaration (the qualified name is
    // interpreted as a parameter name, which is illegal). Braces force
    // direct-list-initialization unambiguously.
    ::XCore::TArray<FLocEntry, ::XCore::DefaultAllocator> Entries{
        ::XCore::DefaultAllocator{::XCore::HAL::FMemTag::Localization}};

    ::SIZE_T Cursor = kLoctableHeaderSize + 8u;
    const ::SIZE_T BufEnd = static_cast<::SIZE_T>(FileLen);

    for (::std::uint64_t I = 0; I < EntryCount; ++I)
    {
        // -- NamespaceLen (u16) + Namespace bytes --
        if (Cursor + 2 > BufEnd)
        {
            ::XCore::HAL::FMemory::Free(Buffer);
            return ::XCore::Unexpected(::XCore::FParseError::Malformed);
        }
        const ::std::uint16_t NsLen = Bytewise::ReadU16Le(Bytes + Cursor);
        Cursor += 2;
        if (Cursor + NsLen > BufEnd)
        {
            ::XCore::HAL::FMemory::Free(Buffer);
            return ::XCore::Unexpected(::XCore::FParseError::Malformed);
        }
        const char* NsPtr = reinterpret_cast<const char*>(Bytes + Cursor);
        ::XCore::FString NsStr(NsPtr, static_cast<::int32>(NsLen));
        Cursor += NsLen;

        // -- KeyLen (u16) + Key bytes --
        if (Cursor + 2 > BufEnd)
        {
            ::XCore::HAL::FMemory::Free(Buffer);
            return ::XCore::Unexpected(::XCore::FParseError::Malformed);
        }
        const ::std::uint16_t KeyLen = Bytewise::ReadU16Le(Bytes + Cursor);
        Cursor += 2;
        if (Cursor + KeyLen > BufEnd)
        {
            ::XCore::HAL::FMemory::Free(Buffer);
            return ::XCore::Unexpected(::XCore::FParseError::Malformed);
        }
        const char* KeyPtr = reinterpret_cast<const char*>(Bytes + Cursor);
        ::XCore::FString KeyStr(KeyPtr, static_cast<::int32>(KeyLen));
        Cursor += KeyLen;

        // -- ValueLen (u32) + Value bytes --
        if (Cursor + 4 > BufEnd)
        {
            ::XCore::HAL::FMemory::Free(Buffer);
            return ::XCore::Unexpected(::XCore::FParseError::Malformed);
        }
        const ::std::uint32_t ValLen = Bytewise::ReadU32Le(Bytes + Cursor);
        Cursor += 4;
        if (Cursor + ValLen > BufEnd)
        {
            ::XCore::HAL::FMemory::Free(Buffer);
            return ::XCore::Unexpected(::XCore::FParseError::Malformed);
        }
        const char* ValPtr = reinterpret_cast<const char*>(Bytes + Cursor);
        ::XCore::FString ValStr(ValPtr, static_cast<::int32>(ValLen));
        Cursor += ValLen;

        FLocEntry Entry;
        Entry.Namespace = ::std::move(NsStr);
        Entry.Key       = ::std::move(KeyStr);
        Entry.Value     = ::std::move(ValStr);
        Entries.Emplace(::std::move(Entry));
    }

    ::XCore::HAL::FMemory::Free(Buffer);

    // Optional: verify no trailing garbage. We do not enforce
    // strict-EOF here -- a future revision may extend the format
    // with optional trailing sections (e.g., metadata, signature)
    // without bumping the version number, so trailing bytes are
    // tolerated.

    return ResultT{::std::move(Entries)};
}

// ---------------------------------------------------------------------
// SaveToFile -- inverse of LoadFromFile.
//
// Used by the test fixture-generation pipeline. The runtime engine
// never calls this; it's a tool / test helper.
// ---------------------------------------------------------------------
bool FLocTableLoader::SaveToFile(
    const ::XCore::FString& Path,
    const ::XCore::TArray<FLocTableLoader::FLocEntry, ::XCore::DefaultAllocator>& Entries)
{
    // First pass: compute total payload size.
    //
    // Payload layout: EntryCount (8 bytes) + entries.
    // Each entry: 2 (NsLen) + NsBytes + 2 (KeyLen) + KeyBytes + 4 (ValLen) + ValBytes.
    ::SIZE_T PayloadSize = 8;
    for (::int32 I = 0; I < Entries.Num(); ++I)
    {
        const FLocEntry& E = Entries[I];
        PayloadSize += 2u + static_cast<::SIZE_T>(E.Namespace.LenBytes());
        PayloadSize += 2u + static_cast<::SIZE_T>(E.Key.LenBytes());
        PayloadSize += 4u + static_cast<::SIZE_T>(E.Value.LenBytes());

        // Validate per-entry size limits (must round-trip through the loader).
        if (static_cast<::std::uint32_t>(E.Namespace.LenBytes()) > kLoctableMaxNamespaceLen ||
            static_cast<::std::uint32_t>(E.Key.LenBytes())       > kLoctableMaxKeyLen)
        {
            return false;
        }
    }

    const ::SIZE_T TotalSize = kLoctableHeaderSize + PayloadSize;
    void* Buffer = ::XCore::HAL::FMemory::MallocOrAbort(
        TotalSize,
        /*Align=*/1,
        ::XCore::HAL::FMemTag::Localization);
    ::uint8* Bytes = static_cast<::uint8*>(Buffer);

    // -----------------------------------------------------------------
    // Write payload section first (we need it to compute the digest).
    // -----------------------------------------------------------------
    ::uint8* PayloadStart = Bytes + kLoctableHeaderSize;
    ::uint8* Cursor       = PayloadStart;

    Bytewise::WriteU64Le(Cursor, static_cast<::std::uint64_t>(Entries.Num()));
    Cursor += 8;

    for (::int32 I = 0; I < Entries.Num(); ++I)
    {
        const FLocEntry& E = Entries[I];
        const ::int32 NsLen  = E.Namespace.LenBytes();
        const ::int32 KeyLen = E.Key.LenBytes();
        const ::int32 ValLen = E.Value.LenBytes();

        Bytewise::WriteU16Le(Cursor, static_cast<::std::uint16_t>(NsLen));
        Cursor += 2;
        if (NsLen > 0)
        {
            std::memcpy(Cursor, E.Namespace.ToUtf8Ptr(), static_cast<::SIZE_T>(NsLen));
            Cursor += NsLen;
        }

        Bytewise::WriteU16Le(Cursor, static_cast<::std::uint16_t>(KeyLen));
        Cursor += 2;
        if (KeyLen > 0)
        {
            std::memcpy(Cursor, E.Key.ToUtf8Ptr(), static_cast<::SIZE_T>(KeyLen));
            Cursor += KeyLen;
        }

        Bytewise::WriteU32Le(Cursor, static_cast<::std::uint32_t>(ValLen));
        Cursor += 4;
        if (ValLen > 0)
        {
            std::memcpy(Cursor, E.Value.ToUtf8Ptr(), static_cast<::SIZE_T>(ValLen));
            Cursor += ValLen;
        }
    }

    // -----------------------------------------------------------------
    // Compute BLAKE3 of payload.
    // -----------------------------------------------------------------
    const ::XCore::Hash::FBlake3Digest Digest =
        ::XCore::Hash::FBlake3::Compute(PayloadStart, PayloadSize);

    // -----------------------------------------------------------------
    // Write header.
    // -----------------------------------------------------------------
    Bytewise::WriteU32Le(Bytes + 0, kLoctableMagic);
    Bytewise::WriteU16Le(Bytes + 4, kLoctableVersion);
    Bytewise::WriteU16Le(Bytes + 6, kLoctableFlagsReserved);
    std::memcpy(Bytes + 8, Digest.Bytes, 32);

    // -----------------------------------------------------------------
    // Write file.
    // -----------------------------------------------------------------
    std::FILE* Fp = std::fopen(Path.ToUtf8Cstr(), "wb");
    if (Fp == nullptr)
    {
        ::XCore::HAL::FMemory::Free(Buffer);
        return false;
    }

    const ::SIZE_T BytesWritten = std::fwrite(Bytes, 1, TotalSize, Fp);
    std::fclose(Fp);
    ::XCore::HAL::FMemory::Free(Buffer);

    return BytesWritten == TotalSize;
}

} // namespace XCore::Loc
