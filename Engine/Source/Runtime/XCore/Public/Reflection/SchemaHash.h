// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// SchemaHash.h -- BLAKE3-backed per-type schema-hash computation.
// =====================================================================
//
// XCore-4b Rev 3, Section 8.4 (Per-type SchemaHash discipline).
//
// Per §8.4: "The per-type SchemaHash on FStruct ... is computed by XHT
// at emit time from a stable canonical form of the declared shape
// (FIX-10). ... Builder.Finalize().LowerUInt64() // BLAKE3 lower 64 bits."
//
// PHASE 4b.2 SCOPE: this file ships the COMPUTATION wrapper around
// XCore-4a's BLAKE3 implementation -- the input-canonicalisation layer
// (declared shape -> canonical UTF-8 byte stream) comes in Phase 4b.4+
// when FProperty subclass schemas exist and XHT's emitter can produce
// the canonical stream. The wrapper accepts a raw byte buffer so the
// computation is reusable by:
//
//   * Unit tests verifying determinism (Acceptance gate D / C3).
//   * Phase 4b.4+ XHT integration once the canonical-byte-stream layer
//     lands.
//   * Any downstream consumer that wants to fingerprint a declared
//     shape independent of the FProperty graph (e.g., XSerialization
//     header keying).
//
// TRUNCATION: BLAKE3 produces 256-bit (32-byte) digests; SchemaHash is
// a `uint64`. Per §8.4 the truncation is the LOWER 64 bits (bytes 0..7
// of the BLAKE3 digest interpreted as little-endian uint64). The §8.4.2
// collision policy notes the 2^-64 collision probability per pair; XHT
// detects truncation collisions at link time via the manifest-emitted
// (SchemaHash, FullTypeName) uniqueness check.
//
// BLAKE3 STABILITY (§8.4.4): SchemaHash values are NOT stable across
// BLAKE3 implementation swaps. The current XCore-4a BLAKE3 is clean-
// room scalar; a future swap to a vendored implementation would
// invalidate all SchemaHashes (deliberate -- the swap is scheduled and
// the clean-build expectation is documented in the Master Plan §2a B-3
// future-eval marker).
//
// DETERMINISM: bit-exact across Win64 / Linux / Android-ARM64 (XCore-4a
// §11.9 BLAKE3 determinism contract). Same input bytes always produce
// the same SchemaHash regardless of host architecture / compile flags.
//
// =====================================================================

#include "Hash/FBlake3.h"
#include "Macros/XCoreTypes.h"

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // ComputeSchemaHashImpl -- BLAKE3-lower-64 over the canonical byte
    // stream.
    //
    // Input: a pre-canonicalised byte stream representing the declared
    // shape of the type. The canonicalisation (declared property list
    // in stable order, with name + type-name + flags + replication
    // metadata, etc.) is the caller's responsibility; this function
    // is a deterministic wrapper that produces a 64-bit fingerprint.
    //
    // Output: the lower 64 bits of the BLAKE3 digest, interpreted as a
    // little-endian uint64 (bytes 0..7 of the 32-byte digest packed as
    // `bytes[0] | (bytes[1] << 8) | ... | (bytes[7] << 56)`).
    //
    // Edge cases:
    //   * ByteCount == 0: the empty-input BLAKE3 digest is well-defined
    //     (per the BLAKE3 spec). The empty-input SchemaHash is the
    //     lower-64 of BLAKE3("") and is a stable constant.
    //   * CanonicalBytes == nullptr requires ByteCount == 0 (mirrors
    //     FBlake3::Compute's contract).
    //
    // Thread-safety: stateless and re-entrant (FBlake3::Compute is
    // re-entrant).
    // -----------------------------------------------------------------
    [[nodiscard]] ::uint64 ComputeSchemaHashImpl(const ::uint8* CanonicalBytes,
                                                ::uint64 ByteCount) noexcept;

    // -----------------------------------------------------------------
    // TruncateBlake3ToUInt64 -- helper exposing the truncation rule.
    //
    // The truncation is split out for re-use in tests and for callers
    // that already have a BLAKE3 digest in hand. The rule: lower 64
    // bits of the digest, interpreted as little-endian.
    // -----------------------------------------------------------------
    [[nodiscard]] inline ::uint64 TruncateBlake3ToUInt64(const ::XCore::Hash::FBlake3Digest& Digest) noexcept
    {
        // Manual little-endian unpack so the result is bit-exact across
        // all targets (the targets are all LE so this matches a
        // straight reinterpret-cast, but the manual form is the
        // engineering-principles-correct path for future architecture
        // proofing).
        return  static_cast<::uint64>(Digest.Bytes[0])
             | (static_cast<::uint64>(Digest.Bytes[1]) <<  8)
             | (static_cast<::uint64>(Digest.Bytes[2]) << 16)
             | (static_cast<::uint64>(Digest.Bytes[3]) << 24)
             | (static_cast<::uint64>(Digest.Bytes[4]) << 32)
             | (static_cast<::uint64>(Digest.Bytes[5]) << 40)
             | (static_cast<::uint64>(Digest.Bytes[6]) << 48)
             | (static_cast<::uint64>(Digest.Bytes[7]) << 56);
    }

} // namespace XCore::Reflect
