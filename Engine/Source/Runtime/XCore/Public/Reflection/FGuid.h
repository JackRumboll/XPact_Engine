// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FGuid.h -- 128-bit GUID identifier (XCore-4b §8.1).
// =====================================================================
//
// XCore-4b Rev 3, Section 8.1: FCustomVersion's Key field is FGuid (16
// bytes). XCore-4a does not ship FGuid (it ships hashes + FName but no
// 128-bit identifier type), so XCore-4b introduces it here at Phase 4b.2
// alongside the FCustomVersion subsystem that requires it.
//
// LAYOUT (locked at 16 bytes per the XPACT_FCUSTOMVERSION_LAYOUT_TAG
// addendum string "FCustomVersion-v1: Key(FGuid 16) + Version(int32) +
// FriendlyName(FName)" -- §11.5):
//
//   struct FGuid {
//       uint32 A;          // 0  +4   bytes 0..3
//       uint32 B;          // 4  +4   bytes 4..7
//       uint32 C;          // 8  +4   bytes 8..11
//       uint32 D;          // 12 +4   bytes 12..15
//   };
//
// The four-uint32 decomposition (rather than `uint8 Bytes[16]` or
// `uint64 Lo; uint64 Hi;`) mirrors UE's FGuid layout (Misc/Guid.h:80) so
// XPact's GUIDs are byte-compatible with any externally-authored
// UE-style GUID literal a developer brings in (e.g., a vendored
// asset-pipeline component GUID). Bytewise equality is well-defined
// across endianness because every uint32 component is read/written via
// native load/store; cross-platform persistence MUST go through the
// SchemaHash discipline (FArchive serialization in XSerialization, Layer
// 9) which serialises to a fixed-endian byte form.
//
// DESIGN PRINCIPLE (Prime Directive: do it right the first time).
//
// FGuid is intentionally a POD aggregate (no constructors that hide the
// bit pattern, no virtual methods, no allocator dependency). The type
// is:
//
//   * Trivially copyable -- memcpy-able by every TArray<FGuid> and the
//     XCore-4a allocator.
//   * Constexpr-constructible -- the FCustomVersion subsystem populates
//     FGuid values at XCore-4b-static-init time (FCustomVersion is
//     constinit per Section 7.1 ConstInit posture).
//   * Hot-reload safe -- no vtables, no static-init dependencies.
//
// HASH SUPPORT.
//
// GetTypeHash(FGuid) is provided for TMap<FGuid, V> consumers. The hash
// is XCore-4a's scalar XXH3 of the 16-byte payload with seed=0 so it is
// bit-exact across Win64 / Linux / Android-ARM64. NOTE: this is the
// content hash of the GUID bytes, NOT a property of the GUID itself --
// two GUIDs that compare equal hash equal, and two distinct GUIDs hash
// distinctly with probability 1 - 2^-64 per pair.
//
// =====================================================================

#include "Hash/FXxh3.h"
#include "Macros/XCoreTypes.h"

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // FGuid -- 128-bit GUID.
    //
    // POD aggregate; layout-locked at 16 bytes; alignof = 4 (the natural
    // alignment of uint32). The 4-byte alignment matches UE's FGuid.
    // -----------------------------------------------------------------
    struct FGuid
    {
        ::uint32 A;
        ::uint32 B;
        ::uint32 C;
        ::uint32 D;

        // -------------------------------------------------------------
        // Equality. Bytewise (== for each uint32 component).
        // -------------------------------------------------------------
        [[nodiscard]] friend constexpr bool operator==(const FGuid& Lhs, const FGuid& Rhs) noexcept
        {
            return Lhs.A == Rhs.A && Lhs.B == Rhs.B && Lhs.C == Rhs.C && Lhs.D == Rhs.D;
        }

        [[nodiscard]] friend constexpr bool operator!=(const FGuid& Lhs, const FGuid& Rhs) noexcept
        {
            return !(Lhs == Rhs);
        }

        // -------------------------------------------------------------
        // Ordering. Byte-major lexicographic compare; used by the sorted
        // FCustomVersionContainer to keep entries in deterministic order
        // for binary search and for byte-exact round-trip.
        // -------------------------------------------------------------
        [[nodiscard]] friend constexpr bool operator<(const FGuid& Lhs, const FGuid& Rhs) noexcept
        {
            if (Lhs.A != Rhs.A) { return Lhs.A < Rhs.A; }
            if (Lhs.B != Rhs.B) { return Lhs.B < Rhs.B; }
            if (Lhs.C != Rhs.C) { return Lhs.C < Rhs.C; }
            return Lhs.D < Rhs.D;
        }

        // -------------------------------------------------------------
        // IsZero -- true iff every byte is zero. The all-zero GUID is
        // the conventional "invalid / uninitialised" sentinel; the
        // FCustomVersionRegistry rejects registrations with a zero GUID
        // because it cannot be distinguished from default-initialised
        // memory.
        // -------------------------------------------------------------
        [[nodiscard]] constexpr bool IsZero() const noexcept
        {
            return A == 0 && B == 0 && C == 0 && D == 0;
        }
    };

    // ABI lock. Any future change here cascades into FCustomVersion's
    // 32-byte total layout and the Stage B addendum's
    // XPACT_FCUSTOMVERSION_LAYOUT_TAG.
    static_assert(sizeof(FGuid)  == 16, "FGuid ABI lock: must be 16 bytes");
    static_assert(alignof(FGuid) ==  4, "FGuid ABI lock: 4-byte alignment");

    // -----------------------------------------------------------------
    // GetTypeHash(FGuid) -- TMap<FGuid, V> support.
    //
    // The hash is XXH3-64 of the 16 raw bytes with seed=0. Bit-exact
    // across the determinism matrix (XCore-4a §11.9 scalar XXH3 fixed
    // seed contract).
    // -----------------------------------------------------------------
    [[nodiscard]] inline ::uint64 GetTypeHash(const FGuid& Guid) noexcept
    {
        return ::XCore::Hash::FXxh3::Hash64(&Guid, sizeof(FGuid), 0);
    }

} // namespace XCore::Reflect
