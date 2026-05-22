// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FXxh3.h -- scalar XXH3 64-bit hash (Section 11.9).
// =====================================================================
//
// XCore-4a Rev 3, Section 11.9 (fix A-M8 hash portion + A-M19) +
// Section 5.3 (determinism contract).
//
// Per Section 11.9:
//
//   "xxHash vendor at known revision; 64-bit fast hash. SCALAR FALLBACK
//    ONLY -- no SIMD code paths. The vendored fork strips the XXH3_SIMD_*
//    paths to preserve bit-exactness across architectures. Used internally
//    by TMap and TSet (sim-path-safe when the fixed zero seed is honoured)."
//
// And Section 11.9 Trade-off:
//
//   "Scalar XXH3 is ~30% slower than SIMD XXH3 on x86_64. The performance
//    gap is accepted because bit-exactness across Win64-x86_64 /
//    Linux-x86_64 / Android-ARM64 is a load-bearing replay-determinism
//    contract; a TMap built on a SIMD-XXH3-x86_64 host that replays on
//    Android-ARM64 with a different hash output would have different
//    iteration order, different probe sequences, and (in worst case)
//    different collision behaviour. The trade is non-negotiable."
//
// Implementation note. Like FBlake3, this is a clean-room scalar
// implementation rather than a SIMD-stripped vendor. The advantage of
// clean-room: zero risk of accidentally leaving a SIMD path enabled,
// zero build-system entanglement with the upstream xxHash CMake, and
// the entire surface is reviewable in one ~400 LoC source file.
//
// The implementation follows the XXH3 specification at
// https://github.com/Cyan4973/xxHash/blob/dev/xxhash.h verbatim,
// with sizes branched into the canonical four cases:
//
//   * len 0..16:    "small data" path (mix the first few words with the
//                   secret block).
//   * len 17..128:  "midsize data" path (two-by-two accumulator over
//                   first/last 16-byte windows).
//   * len 129..240: "midrange" path (multiple two-by-two passes).
//   * len 241+:     "long data" path (full 256-byte striped accumulator
//                   with avalanche).
//
// Sim-path-safe: pure 64-bit unsigned arithmetic, no SIMD intrinsics,
// no branching on detected CPU features. The same source compiles to
// byte-identical hash output on every supported target.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

namespace XCore::Hash
{
    // -----------------------------------------------------------------
    // FXxh3 -- scalar XXH3 64-bit hash facility.
    //
    // Threading: stateless static method; thread-safe for concurrent
    // callers (the kXxh3Secret table is constinit data in .rdata).
    //
    // Returns a uint64 in the canonical XXH3-64 byte order.
    //
    // Seed: the standard XXH3 64-bit hash accepts an optional seed; per
    // Section 5.3 the engine's containers always pass zero. The seed
    // parameter is preserved for callers that want custom hashes (e.g.,
    // unit tests verifying KAT vectors that use a non-zero seed in the
    // upstream test suite). XCore::TMap and XCore::TSet hard-code
    // seed = 0 at every call site.
    // -----------------------------------------------------------------
    class FXxh3
    {
    public:
        // -------------------------------------------------------------
        // Hash64 -- XXH3 64-bit hash of [Data, Data+Length) with the
        // given seed.
        //
        // Data may be null only when Length == 0; otherwise must point
        // at Length readable bytes. Returns the 64-bit XXH3 digest.
        //
        // Seed defaults to 0 (the engine's container-mode seed); the
        // Section 5.3 determinism contract uses Seed = 0 throughout.
        // -------------------------------------------------------------
        [[nodiscard]] static ::uint64 Hash64(const void* Data, ::SIZE_T Length, ::uint64 Seed = 0) noexcept;
    };

} // namespace XCore::Hash
