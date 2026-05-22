// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FCrc32.h -- CRC-32C (Castagnoli) checksum (Section 11.9).
// =====================================================================
//
// XCore-4a Rev 3, Section 11.9 (fix A-M8 hash portion + A-M19).
//
// Per Section 11.9 declaration:
//
//   "Hand-rolled CRC-32C (~30 LoC; Castagnoli polynomial 0x1EDC6F41).
//    Sim-path-safe; used for legacy file-format checksums and FArchive
//    integrity tags."
//
// Sim-path-safe: pure scalar implementation, no SIMD, no platform
// branching. Bit-exact across Win64-x86_64, Linux-x86_64, Android-ARM64;
// the bit-exactness contract in Section 5.3 ("bit-exact replay across
// Win64 / Linux / Android-ARM64 for sim-path TUs") is honored because
// the table-driven loop has no platform-specific intrinsic.
//
// Algorithm. Castagnoli CRC-32C uses the reverse-bit polynomial
// 0x82F63B78 (the reflection of 0x1EDC6F41) with table-driven
// byte-at-a-time processing. The precomputed 256-entry table is
// initialized at module load via a constexpr constructor (so the table
// is in .rdata, not constructed dynamically). The hot loop is:
//
//   crc = 0xFFFFFFFF;                  // init
//   for byte b in data: crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
//   return crc ^ 0xFFFFFFFF;           // final XOR
//
// This is the IEEE / iSCSI / SCTP variant used by every sane CRC-32C
// consumer (the standard test vector "123456789" yields 0xE3069283
// under the CRC-32C polynomial; the CRC-32 IEEE 802.3 polynomial would
// produce 0xCBF43926 -- the brief mentions the IEEE vector but the
// Section 11.9 spec body specifies the Castagnoli polynomial, so we
// implement CRC-32C and the test vectors below match the Castagnoli
// expected output).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

namespace XCore::Hash
{
    // -----------------------------------------------------------------
    // FCrc32 -- CRC-32C (Castagnoli) checksum facility.
    //
    // Threading: stateless static method; thread-safe for concurrent
    // callers (the lookup table is constinit data in .rdata).
    //
    // ABI: returns a 32-bit unsigned integer in the canonical CRC-32C
    // byte order (low byte first as integer; bit-exact across all
    // supported platforms).
    // -----------------------------------------------------------------
    class FCrc32
    {
    public:
        // -------------------------------------------------------------
        // Compute -- CRC-32C of [Data, Data+Length).
        //
        // Data may be null only when Length == 0; otherwise must point
        // at Length readable bytes. Returns the 32-bit CRC-32C digest.
        // -------------------------------------------------------------
        [[nodiscard]] static ::uint32 Compute(const void* Data, ::SIZE_T Length) noexcept;
    };

} // namespace XCore::Hash
