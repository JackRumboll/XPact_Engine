// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FRandomStream.h -- xoshiro256++ deterministic RNG (Section 6.1.6).
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1.6 (fix B-MIN3). The engine's
// deterministic RNG. The state is 4 x uint64 = 32 bytes; the
// algorithm has a 2^256 - 1 period and passes BigCrush.
//
// Reference: David Blackman & Sebastiano Vigna, 2019, public-domain
// implementation at https://prng.di.unimi.it/xoshiro256plusplus.c
//
// SIM-PATH SAFETY:
//   The RNG is sim-path-safe: no platform RNG, no time-based seed.
//   The seed comes from the session's synchronised seed established
//   at session-start. Identical seed + identical call sequence
//   produces bit-identical output across Win64-x86_64,
//   Linux-x86_64, Android-ARM64.
//
//   The runtime NEVER seeds sim-path code from time(), /dev/urandom,
//   or any platform entropy source. Sub-systems that legitimately
//   need non-deterministic randomness (editor random-asset preview;
//   plugin authentication tokens; one-shot debugging-state
//   perturbation) construct an FRandomStream from
//   FPlatformMisc::GetEntropy -- the latter is sim-path-deprecated
//   via the sim-path overlay header, so sim-path TUs cannot reach
//   the non-deterministic surface.
//
// SEEDING:
//   The constructor takes a uint64 seed; the seed is run through
//   splitmix64 to expand it to the full 256-bit state. This avoids
//   the "small seed -> small initial entropy" landmine that naive
//   xoshiro256++ initialisation exhibits.
//
// FORKING:
//   Fork() derives a child stream's seed deterministically from the
//   parent's current state via splitmix64. The child is uncorrelated
//   from subsequent parent draws while remaining bit-reproducible
//   from the same starting state.
//
// =====================================================================

#include "Macros/XCoreTypes.h"  // uint64
#include "Macros/XPactMacros.h"

namespace XCore::Math
{

// ---------------------------------------------------------------------
// FRandomStream -- xoshiro256++ state + draw interface.
//
// 32-byte state (4 x uint64); 8-byte aligned. The state is private;
// callers can only mutate via the Next* methods. Copying is allowed
// (a copy snapshots the state and produces an identical subsequent
// sequence; useful for "what-if" replay).
// ---------------------------------------------------------------------

class FRandomStream
{
public:
    // -----------------------------------------------------------------
    // Constructor. The seed is run through splitmix64 to expand it to
    // the full 256-bit state. A zero seed is forbidden (xoshiro256++
    // is undefined on all-zero state); the constructor maps zero to
    // a hard-coded fallback seed (0xC6A4A7935BD1E995ULL; the
    // splitmix64 increment constant) to avoid this trap.
    // -----------------------------------------------------------------

    explicit FRandomStream(::uint64 Seed) noexcept;

    // -----------------------------------------------------------------
    // Fork -- derive a child stream. Each call to Fork on a given
    // parent state produces a different child seed; calling Fork
    // twice in a row from the same parent state produces two
    // different children (because the first Fork advances the
    // parent's state).
    // -----------------------------------------------------------------

    [[nodiscard]] FRandomStream Fork() noexcept;

    // -----------------------------------------------------------------
    // Next* -- advance the state and return the next draw.
    //
    // NextUInt64       -- the canonical primitive; 64 bits of entropy.
    // NextDouble       -- [0, 1) with full 53-bit mantissa.
    // NextFloat        -- [0, 1) with full 24-bit mantissa.
    // NextInt32InRange -- [Min, MaxExclusive); Lemire's unbiased method.
    // -----------------------------------------------------------------

    [[nodiscard]] ::uint64 NextUInt64() noexcept;
    [[nodiscard]] double   NextDouble() noexcept;
    [[nodiscard]] float    NextFloat()  noexcept;
    [[nodiscard]] ::int32  NextInt32InRange(::int32 Min, ::int32 MaxExclusive) noexcept;

private:
    // xoshiro256++ state. Exactly 4 x uint64 = 32 bytes.
    ::uint64 m_state[4];

    // Internal constructor for Fork() -- bypasses the splitmix64 seed
    // expansion (the four state words are already supplied).
    FRandomStream(::uint64 S0, ::uint64 S1, ::uint64 S2, ::uint64 S3) noexcept
    {
        m_state[0] = S0;
        m_state[1] = S1;
        m_state[2] = S2;
        m_state[3] = S3;
    }
};

static_assert(sizeof(FRandomStream)  == 32, "FRandomStream xoshiro256++ state ABI lock: 32 bytes");
static_assert(alignof(FRandomStream) ==  8, "FRandomStream ABI lock: 8-byte aligned");

} // namespace XCore::Math
