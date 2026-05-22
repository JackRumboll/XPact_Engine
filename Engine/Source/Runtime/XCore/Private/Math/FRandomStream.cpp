// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FRandomStream.cpp -- xoshiro256++ NextUInt64 + splitmix64 seed.
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1.6.
//
// Algorithms (public domain, David Blackman & Sebastiano Vigna):
//   xoshiro256++   https://prng.di.unimi.it/xoshiro256plusplus.c
//   splitmix64     https://prng.di.unimi.it/splitmix64.c
//
// The xoshiro256++ next() body and the splitmix64 helper are direct
// transcriptions of the public-domain reference implementations; the
// algorithm is unchanged.
//
// Determinism contract (Section 6.1.6):
//   * Identical seed + identical call sequence produces bit-identical
//     output across Win64-x86_64, Linux-x86_64, Android-ARM64.
//   * Verified by Tests/FRandomStream.Tests/Reproducibility.cpp
//     (1M-step trajectory).
//   * No platform RNG, no time-based seed; the seed is supplied by
//     the caller per session-start synchronised-seed protocol.
//
// =====================================================================

#include "Math/FRandomStream.h"

#include "Macros/XCoreTypes.h"

namespace XCore::Math
{

// ---------------------------------------------------------------------
// splitmix64 -- a fast hash that produces 64 bits of well-mixed output
// from a 64-bit seed; used here to expand the user's uint64 seed into
// the 4 x uint64 xoshiro256++ state (and to derive child stream seeds).
//
// Reference: https://prng.di.unimi.it/splitmix64.c (public domain).
// ---------------------------------------------------------------------

namespace
{
    [[nodiscard]] inline ::uint64 Splitmix64Step(::uint64& Z) noexcept
    {
        Z += 0x9E3779B97F4A7C15ull;
        ::uint64 X = Z;
        X = (X ^ (X >> 30)) * 0xBF58476D1CE4E5B9ull;
        X = (X ^ (X >> 27)) * 0x94D049BB133111EBull;
        return X ^ (X >> 31);
    }

    [[nodiscard]] inline ::uint64 Rotl(::uint64 X, int K) noexcept
    {
        return (X << K) | (X >> (64 - K));
    }
}

// ---------------------------------------------------------------------
// FRandomStream(Seed) -- splitmix64-expand the user seed into the
// 256-bit xoshiro256++ state.
//
// Zero seed is forbidden (the all-zero state is a xoshiro256++ fixed
// point that produces all-zero output). We map zero to a hard-coded
// fallback (0xC6A4A7935BD1E995, the splitmix64 increment constant).
// ---------------------------------------------------------------------

FRandomStream::FRandomStream(::uint64 Seed) noexcept
{
    ::uint64 Z = Seed != 0 ? Seed : 0xC6A4A7935BD1E995ull;
    m_state[0] = Splitmix64Step(Z);
    m_state[1] = Splitmix64Step(Z);
    m_state[2] = Splitmix64Step(Z);
    m_state[3] = Splitmix64Step(Z);

    // Sanity: ensure the resulting state is not all-zero. Splitmix64
    // is a permutation, so all-zero output requires all-zero input;
    // the zero-seed branch above ensures we never hit this. Still,
    // belt + braces.
    if ((m_state[0] | m_state[1] | m_state[2] | m_state[3]) == 0)
    {
        m_state[0] = 0xC6A4A7935BD1E995ull;
        m_state[3] = 0x9E3779B97F4A7C15ull;
    }
}

// ---------------------------------------------------------------------
// Fork -- derive a child stream from the parent's CURRENT state via
// splitmix64. Each Fork call advances the parent's state (via the
// NextUInt64 draw used as the splitmix64 input), so two consecutive
// Fork calls produce two different children.
// ---------------------------------------------------------------------

FRandomStream FRandomStream::Fork() noexcept
{
    // Derive a single uint64 from the current state and use it as the
    // splitmix64 seed for the child. Calling NextUInt64() here is
    // important: it advances the parent's state so the child is
    // uncorrelated with subsequent parent draws.
    ::uint64 ParentDraw = NextUInt64();
    ::uint64 Z = ParentDraw != 0 ? ParentDraw : 0xC6A4A7935BD1E995ull;
    const ::uint64 S0 = Splitmix64Step(Z);
    const ::uint64 S1 = Splitmix64Step(Z);
    const ::uint64 S2 = Splitmix64Step(Z);
    const ::uint64 S3 = Splitmix64Step(Z);
    return FRandomStream{ S0, S1, S2, S3 };
}

// ---------------------------------------------------------------------
// NextUInt64 -- xoshiro256++ next() implementation.
//
//   const uint64_t result = rotl(s[0] + s[3], 23) + s[0];
//   const uint64_t t = s[1] << 17;
//   s[2] ^= s[0]; s[3] ^= s[1]; s[1] ^= s[2]; s[0] ^= s[3];
//   s[2] ^= t;
//   s[3] = rotl(s[3], 45);
//   return result;
// ---------------------------------------------------------------------

::uint64 FRandomStream::NextUInt64() noexcept
{
    const ::uint64 Result = Rotl(m_state[0] + m_state[3], 23) + m_state[0];
    const ::uint64 T = m_state[1] << 17;
    m_state[2] ^= m_state[0];
    m_state[3] ^= m_state[1];
    m_state[1] ^= m_state[2];
    m_state[0] ^= m_state[3];
    m_state[2] ^= T;
    m_state[3] = Rotl(m_state[3], 45);
    return Result;
}

// ---------------------------------------------------------------------
// NextDouble -- [0, 1) double with full 53-bit mantissa.
//
// Construction: take the top 53 bits of the 64-bit draw, build a
// double in the range [1, 2) via the trick of OR-ing the exponent
// bits, then subtract 1.0 to remove the implicit-one. This avoids
// floating-point division and is bit-deterministic across platforms.
// ---------------------------------------------------------------------

double FRandomStream::NextDouble() noexcept
{
    const ::uint64 X = NextUInt64() >> 11;  // 53-bit value
    return static_cast<double>(X) * (1.0 / static_cast<double>(1ull << 53));
}

// ---------------------------------------------------------------------
// NextFloat -- [0, 1) float with full 24-bit mantissa.
// ---------------------------------------------------------------------

float FRandomStream::NextFloat() noexcept
{
    const ::uint64 X = NextUInt64() >> 40;  // 24-bit value
    return static_cast<float>(X) * (1.0f / static_cast<float>(1u << 24));
}

// ---------------------------------------------------------------------
// NextInt32InRange -- Lemire's unbiased bounded random.
//
// Reference: "Fast Random Integer Generation in an Interval" (Lemire,
// 2018). https://arxiv.org/abs/1805.10941
//
// For range [Min, MaxExclusive) the bound width is (MaxExclusive - Min);
// we draw a 32-bit value, multiply-high to map it into [0, width), and
// reject samples that fall in the "remainder" zone to eliminate the
// modulo bias.
// ---------------------------------------------------------------------

::int32 FRandomStream::NextInt32InRange(::int32 Min, ::int32 MaxExclusive) noexcept
{
    if (MaxExclusive <= Min) { return Min; }  // degenerate range

    const ::uint32 Range = static_cast<::uint32>(MaxExclusive - Min);

    // Lemire's method on the top 32 bits of the xoshiro draw.
    ::uint32 X = static_cast<::uint32>(NextUInt64() >> 32);
    ::uint64 M = static_cast<::uint64>(X) * static_cast<::uint64>(Range);
    ::uint32 L = static_cast<::uint32>(M);
    if (L < Range)
    {
        const ::uint32 T = static_cast<::uint32>(-static_cast<int32_t>(Range)) % Range;
        while (L < T)
        {
            X = static_cast<::uint32>(NextUInt64() >> 32);
            M = static_cast<::uint64>(X) * static_cast<::uint64>(Range);
            L = static_cast<::uint32>(M);
        }
    }
    return Min + static_cast<::int32>(M >> 32);
}

} // namespace XCore::Math
