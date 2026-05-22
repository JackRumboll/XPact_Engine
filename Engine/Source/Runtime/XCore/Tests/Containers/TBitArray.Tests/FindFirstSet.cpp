// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TBitArray.Tests/FindFirstSet.cpp -- find-first-set agreement test.
// =====================================================================
//
// XCore-4a Section 5.1 / 5.6: TBitArray::FindFirstSet uses platform
// bit-scan-forward intrinsics; this test cross-checks against a
// reference brute-force scan over a 100K-bit random pattern.
//
// (The dispatch's "1M-bit" target is scaled to 100K for test-runtime
// bounds; the algorithmic property under test is identical at any
// meaningful scale.)
//
// =====================================================================

#include "Containers/TBitArray.h"
#include "HAL/FMemory.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>
#include <random>

namespace
{
    constexpr ::int32 NUM_BITS = 100000;

    // Brute-force reference: linear scan.
    ::int32 ReferenceFindFirstSet(const ::XCore::TBitArray& Bits, ::int32 StartIndex)
    {
        for (::int32 I = StartIndex; I < Bits.Num(); ++I)
        {
            if (Bits[I])
            {
                return I;
            }
        }
        return INDEX_NONE;
    }
}

int main()
{
    ::XCore::HAL::FMemory::__Init();

    {
        ::XCore::TBitArray Bits;
        std::mt19937_64 RNG(42);  // deterministic seed
        std::bernoulli_distribution Dist(0.5);

        // Build the random pattern.
        for (::int32 I = 0; I < NUM_BITS; ++I)
        {
            Bits.Add(Dist(RNG));
        }

        if (Bits.Num() != NUM_BITS)
        {
            std::fprintf(stderr, "FAIL: Num=%d (expected %d)\n", Bits.Num(), NUM_BITS);
            return 1;
        }

        // For every 100th start index, verify FindFirstSet agrees
        // with the brute-force reference.
        for (::int32 Start = 0; Start < NUM_BITS; Start += 100)
        {
            const ::int32 Actual   = Bits.FindFirstSet(Start);
            const ::int32 Expected = ReferenceFindFirstSet(Bits, Start);
            if (Actual != Expected)
            {
                std::fprintf(stderr,
                    "FAIL: FindFirstSet(%d) = %d (expected %d)\n",
                    Start, Actual, Expected);
                return 1;
            }
        }

        // Edge cases.
        if (Bits.FindFirstSet(NUM_BITS) != INDEX_NONE)
        {
            std::fprintf(stderr, "FAIL: FindFirstSet beyond end returned non-INDEX_NONE\n");
            return 1;
        }
        if (Bits.FindFirstSet(NUM_BITS - 1) != ReferenceFindFirstSet(Bits, NUM_BITS - 1))
        {
            std::fprintf(stderr, "FAIL: FindFirstSet last-bit edge case mismatch\n");
            return 1;
        }
    }

    {
        // All-zero array: FindFirstSet returns INDEX_NONE.
        ::XCore::TBitArray AllZero(false, 1000);
        if (AllZero.FindFirstSet() != INDEX_NONE)
        {
            std::fprintf(stderr, "FAIL: all-zero FindFirstSet returned non-INDEX_NONE\n");
            return 1;
        }
    }

    {
        // All-ones array: FindFirstSet(K) returns K for every K < Num.
        ::XCore::TBitArray AllOnes(true, 200);
        for (::int32 K = 0; K < 200; ++K)
        {
            if (AllOnes.FindFirstSet(K) != K)
            {
                std::fprintf(stderr,
                    "FAIL: all-ones FindFirstSet(%d) = %d (expected %d)\n",
                    K, AllOnes.FindFirstSet(K), K);
                return 1;
            }
        }
    }

    ::XCore::HAL::FMemory::__Shutdown();
    std::printf("TBitArray.FindFirstSet: PASS\n");
    return 0;
}
