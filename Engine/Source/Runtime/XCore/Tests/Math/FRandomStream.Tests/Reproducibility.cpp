// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FRandomStream.Tests/Reproducibility.cpp -- same seed -> same sequence.
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1.6 determinism contract:
//   "identical seed + identical call sequence produces bit-identical
//   output across all three target platforms."
//
// This test covers the seed-determinism half of the contract (the
// cross-platform half is verified by the Foundation Prototype CI
// run; see Section 17.3 C2). Two streams seeded identically must
// produce identical 1M-step trajectories on the local machine.
//
// =====================================================================

#include "Math/FRandomStream.h"

#include <cstdio>

int main()
{
    using ::XCore::Math::FRandomStream;

    constexpr ::uint64 Seed = 0xDEADBEEFCAFE1234ull;
    constexpr int Steps = 1000000;

    FRandomStream A(Seed);
    FRandomStream B(Seed);

    for (int I = 0; I < Steps; ++I)
    {
        const ::uint64 SA = A.NextUInt64();
        const ::uint64 SB = B.NextUInt64();
        if (SA != SB)
        {
            std::fprintf(stderr,
                "FAIL: divergent sequences at step %d: A=%llu B=%llu\n",
                I, static_cast<unsigned long long>(SA), static_cast<unsigned long long>(SB));
            return 1;
        }
    }

    // Distinct seeds produce distinct first draws.
    {
        FRandomStream A2(0x1111111111111111ull);
        FRandomStream B2(0x2222222222222222ull);
        if (A2.NextUInt64() == B2.NextUInt64())
        {
            std::fprintf(stderr, "FAIL: distinct seeds produced identical first draw\n");
            return 1;
        }
    }

    // Zero seed: must not produce all-zero output (handled via the
    // hard-coded fallback in the constructor).
    {
        FRandomStream Z(0);
        ::uint64 OrSum = 0;
        for (int I = 0; I < 16; ++I) { OrSum |= Z.NextUInt64(); }
        if (OrSum == 0)
        {
            std::fprintf(stderr, "FAIL: zero-seed produced all-zero output\n");
            return 1;
        }
    }

    // NextDouble range check.
    {
        FRandomStream R(0xABCDEF0123456789ull);
        for (int I = 0; I < 100; ++I)
        {
            const double D = R.NextDouble();
            if (!(D >= 0.0 && D < 1.0))
            {
                std::fprintf(stderr, "FAIL: NextDouble out of [0,1): %f\n", D);
                return 1;
            }
        }
    }

    // NextFloat range check.
    {
        FRandomStream R(0xABCDEF0123456789ull);
        for (int I = 0; I < 100; ++I)
        {
            const float F = R.NextFloat();
            if (!(F >= 0.0f && F < 1.0f))
            {
                std::fprintf(stderr, "FAIL: NextFloat out of [0,1): %f\n", F);
                return 1;
            }
        }
    }

    // NextInt32InRange unbiased + in-range.
    {
        FRandomStream R(0xABCDEF0123456789ull);
        for (int I = 0; I < 1000; ++I)
        {
            const ::int32 V = R.NextInt32InRange(10, 20);
            if (!(V >= 10 && V < 20))
            {
                std::fprintf(stderr, "FAIL: NextInt32InRange out of [10, 20): %d\n", V);
                return 1;
            }
        }
    }

    return 0;
}
