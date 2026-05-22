// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// SleefULP_Sin.cpp -- Sleef_sinf_u35 ULP-error verification.
// =====================================================================
//
// XCore-4a Rev 3 Section 17.3 C-extra: "ULP tier verified at unit-test
// time: ... sin/cos/tan/atan2 use u35".  The Sleef u35 family
// guarantees AT MOST 35 ULP worst-case error.  Worst case is exercised
// near +-pi/2 + multiples of pi where the absolute value falls off the
// floating-point precision cliff; bulk of the domain returns within
// 1-2 ULP.
//
// This test sweeps 1000 inputs across [-pi, pi] uniformly and asserts
// the ULP distance from the std::sin reference is <= 35.
//
// Reference: std::sin from <cmath>.  As with sqrt, this is one of the
// few cases where libm is the canonical reference: IEEE-754 does NOT
// mandate sin/cos correctly-rounded, but every conforming
// implementation IS within ~2 ULP of correctly-rounded across the
// normal range.  Sleef u35's 35-ULP bound is much looser than typical
// libm bounds in the test domain; the test is essentially a sanity
// check that Sleef_sinf_u35 returns a value in the right neighbourhood.
//
// =====================================================================

#include "Sleef.h"
#include "Macros/XCoreTypes.h"

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace
{
    ::uint32 ULPDistance(float A, float B) noexcept
    {
        if (std::isnan(A) || std::isnan(B)) return UINT32_MAX;
        if (A == B) return 0;
        ::uint32 BitsA, BitsB;
        std::memcpy(&BitsA, &A, sizeof(BitsA));
        std::memcpy(&BitsB, &B, sizeof(BitsB));
        if ((BitsA ^ BitsB) >> 31)
        {
            ::uint32 PosA = BitsA & 0x7fffffffu;
            ::uint32 PosB = BitsB & 0x7fffffffu;
            ::uint64 Sum = static_cast<::uint64>(PosA) + static_cast<::uint64>(PosB);
            return Sum > UINT32_MAX ? UINT32_MAX : static_cast<::uint32>(Sum);
        }
        return BitsA > BitsB ? (BitsA - BitsB) : (BitsB - BitsA);
    }
}

int main()
{
    constexpr ::uint32 ULP_TIER_U35 = 35;
    int Failed = 0;
    ::uint32 MaxULPSeen = 0;
    ::uint32 OverThreshold = 0;

    // Uniform sweep across [-pi, pi].  1000 points; step = 2*pi/1000.
    constexpr int N = 1000;
    constexpr float TwoPi = 6.2831853071795864769f;
    constexpr float Pi = 3.1415926535897932385f;
    for (int I = 0; I < N; ++I)
    {
        const float Input = -Pi + (TwoPi * static_cast<float>(I)) / static_cast<float>(N);
        const float Got = ::Sleef_sinf_u35(Input);
        const float Expected = std::sin(Input);
        const ::uint32 Distance = ULPDistance(Got, Expected);

        if (Distance > MaxULPSeen) MaxULPSeen = Distance;
        if (Distance > ULP_TIER_U35)
        {
            ++OverThreshold;
            if (OverThreshold <= 5)
            {
                std::fprintf(stderr,
                    "FAIL: Sleef_sinf_u35(%.9g) = %.9g, expected %.9g, ULP distance = %u\n",
                    static_cast<double>(Input),
                    static_cast<double>(Got),
                    static_cast<double>(Expected),
                    Distance);
            }
        }
    }

    if (OverThreshold > 0)
    {
        std::fprintf(stderr,
            "SleefULP_Sin: %u inputs out of %d exceed u35 ULP bound (35). Max ULP = %u.\n",
            OverThreshold, N, MaxULPSeen);
        ++Failed;
    }

    if (Failed > 0)
    {
        std::fprintf(stderr, "SleefULP_Sin: FAIL (%d failures)\n", Failed);
        return 1;
    }

    std::printf("SleefULP_Sin: PASS (%d inputs; max ULP = %u; tier-bound = 35)\n", N, MaxULPSeen);
    return 0;
}
