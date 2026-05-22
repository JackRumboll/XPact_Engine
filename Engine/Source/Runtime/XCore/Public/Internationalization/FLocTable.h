// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FLocTable.h -- per-locale binary loctable format (Section 18 OPEN-5).
// =====================================================================
//
// XCore-4a Rev 3, Section 18 OPEN-5 RECOMMENDED:
//   "per-locale binary file with BLAKE3 header. Phase 1 flat directory;
//    Phase 2 packaged in XPak archive without changing the per-file
//    format."
//
// Why a binary format (instead of JSON / XLIFF / PO):
//   * Self-describing (magic + version).
//   * Integrity-checked (BLAKE3 payload digest).
//   * Language-agnostic (UTF-8 strings with explicit lengths).
//   * Survives Phase 2 archive migration without format change.
//   * Bit-exact serialization (no whitespace ambiguity, no number
//     formatting; loads byte-identical across all three platforms).
//
// LOCTABLE V1 BINARY LAYOUT
// =====================================================================
//
//   Offset  Size      Field
//   ------  --------  ------------------------------------------------
//   0       4         Magic = 0x4C4F4354 = 'LOCT' (little-endian)
//   4       2         Version = 1 (uint16, little-endian)
//   6       2         Flags (reserved; must be 0)
//   8       32        FBlake3Digest of the payload below (entries
//                     section + EntryCount, NOT the 40-byte header)
//   40      8         EntryCount (uint64, little-endian)
//   48      ...       Variable-length entries
//
// Each entry, in order:
//   2 bytes (uint16 LE): NamespaceLen
//   NamespaceLen bytes: Namespace (UTF-8; NOT null-terminated)
//   2 bytes (uint16 LE): KeyLen
//   KeyLen bytes: Key (UTF-8; NOT null-terminated)
//   4 bytes (uint32 LE): ValueLen
//   ValueLen bytes: Value (UTF-8; NOT null-terminated)
//
// The fixed-size header is 40 bytes (magic + version + flags + digest);
// the EntryCount at offset 40 starts the "payload" region that is
// covered by the BLAKE3 digest. The verifier hashes from offset 40
// to end-of-file and compares to the digest at offset 8.
//
// Endianness: little-endian throughout. The format is target-host-
// endian-agnostic because every multi-byte field is read/written via
// explicit byte-swap helpers (FBytewise::Read* / Write* below). The
// serializer and parser produce/consume byte-identical files on
// Win64-LE, Linux-LE, and Android-ARM64-LE; if XPact ever ships a
// big-endian target, the byte-swap helpers handle the conversion.
//
// CAPACITY LIMITS (load-bearing for the parser's overflow checks):
//   * NamespaceLen <= 65535 (uint16). Localisation namespaces are
//     short identifiers; 64 KiB is wildly generous.
//   * KeyLen       <= 65535 (uint16). Same reasoning.
//   * ValueLen     <= 4 GiB (uint32). A single localised value larger
//     than 4 GiB is nonsensical; the cap keeps the on-disk size field
//     to 4 bytes and avoids platform-dependent size_t.
//   * EntryCount   <= 2^64-1 (uint64). Effectively unbounded.
//
// The parser validates each length against the remaining file size
// before consuming bytes; a truncated file produces a clean
// FParseError, never reads out of bounds.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "Hash/FBlake3.h"

#include <cstdint>

namespace XCore::Loc
{

// ---------------------------------------------------------------------
// Loctable format constants.
//
// kLoctableMagic: 0x4C4F4354 = 'LOCT' (little-endian; bytes spell
// "LOCT" on disk).
//
// kLoctableVersion: 1 (the only version shipped at Phase 1f). Future
// revisions (e.g., compressed value blob, plural-form metadata)
// bump this; the loader rejects mismatched versions with
// FParseError::InvalidVersion.
// ---------------------------------------------------------------------
inline constexpr ::std::uint32_t kLoctableMagic   = 0x4C4F4354u;  // 'LOCT'
inline constexpr ::std::uint16_t kLoctableVersion = 1u;
inline constexpr ::std::uint16_t kLoctableFlagsReserved = 0u;

// ---------------------------------------------------------------------
// kLoctableHeaderSize: fixed-size header before the EntryCount.
//   4 (magic) + 2 (version) + 2 (flags) + 32 (BLAKE3 digest) = 40 bytes.
// ---------------------------------------------------------------------
inline constexpr ::SIZE_T kLoctableHeaderSize = 40u;

// ---------------------------------------------------------------------
// kLoctableMaxNamespaceLen: 65535 (uint16 max). Localisation namespaces
// are short identifiers; 64 KiB is a generous cap.
// ---------------------------------------------------------------------
inline constexpr ::std::uint32_t kLoctableMaxNamespaceLen = 65535u;
inline constexpr ::std::uint32_t kLoctableMaxKeyLen       = 65535u;

// ---------------------------------------------------------------------
// FLocTableEntry -- one (namespace, key) -> value triple as parsed
// from disk.
//
// The Namespace and Key fields are owned const char* pointers into a
// heap buffer allocated by the parser (the same buffer that backs all
// entries' namespace+key bytes; tagged FMemTag::Localization). The
// Value FString owns its own heap allocation.
//
// The shared heap buffer for namespace+key bytes is freed when the
// TArray<FLocTableEntry> is destroyed; this requires the loader to
// expose a teardown handle. For Phase 1f we use the simpler shape
// where each entry's Namespace and Key are individual FString
// allocations -- the extra allocation cost is negligible (loctable
// load is once per locale, not per frame) and the lifetime story is
// trivial.
//
// TODO(Phase 2): coalesce namespace+key bytes into one buffer per
// loctable for cache-locality during Lookup. Phase 1f's correctness-
// first shape uses one FString allocation per field.
// ---------------------------------------------------------------------

} // namespace XCore::Loc
