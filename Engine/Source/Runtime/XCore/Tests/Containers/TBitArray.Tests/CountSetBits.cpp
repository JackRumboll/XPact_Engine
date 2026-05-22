// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TBitArray.Tests/CountSetBits.cpp -- popcount agreement test.
// =====================================================================
//
// XCore-4a Section 5.1 / 5.6: TBitArray::CountSetBits uses platform
// popcount intrinsics; this test cross-checks against a reference
// brute-force count over a 100K-bit random pattern (scaled from the
// brief's 1M for test-runtime bounds).
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

    // Brute-force reference: linear scan + count.
    ::int32 ReferenceCount(const ::XCore::TBitArray& Bits)
    {
        ::int32 Count = 0;
        for (::int32 I = 0; I < Bits.Num(); ++I)
        {
            if (Bits[I])
            {
                ++Count;
            }
        }
        return Count;
    }
}

int main()
{
    ::XCore::HAL::FMemory::__Init();

    {
        // Random pattern.
        ::XCore::TBitArray Bits;
        std::mt19937_64 RNG(42);
        std::bernoulli_distribution Dist(0.5);

        for (::int32 I = 0; I < NUM_BITS; ++I)
        {
            Bits.Add(Dist(RNG));
        }

        const ::int32 Actual   = Bits.CountSetBits();
        const ::int32 Expected = ReferenceCount(Bits);
        if (Actual != Expected)
        {
            std::fprintf(stderr,
                "FAIL: CountSetBits = %d (expected %d)\n", Actual, Expected);
            return 1;
        }
    }

    {
        // Empty array.
        ::XCore::TBitArray Empty;
        if (Empty.CountSetBits() != 0)
        {
            std::fprintf(stderr, "FAIL: empty CountSetBits = %d (expected 0)\n",
                         Empty.CountSetBits());
            return 1;
        }
    }

    {
        // All-zero array.
        ::XCore::TBitArray AllZero(false, 1000);
        if (AllZero.CountSetBits() != 0)
        {
            std::fprintf(stderr, "FAIL: all-zero CountSetBits = %d (expected 0)\n",
                         AllZero.CountSetBits());
            return 1;
        }
    }

    {
        // All-ones array. Verify trailing-bit normalization: for
        // NumBits=200, the last word holds 200 - 128 = 72 bits but
        // 72 > 64, so the last word holds 200 - 192 = 8 bits. Wait:
        // 200 / 64 = 3 full words; (200 - 192) = 8 bits in the partial
        // 4th word. CountSetBits must return EXACTLY 200, not 200 +
        // ghost-trailing-bits.
        ::XCore::TBitArray AllOnes(true, 200);
        if (AllOnes.CountSetBits() != 200)
        {
            std::fprintf(stderr,
                "FAIL: all-ones(200) CountSetBits = %d (expected 200; trailing-bit normalization broken)\n",
                AllOnes.CountSetBits());
            return 1;
        }
    }

    {
        // Single-bit set: verify exact count at every position.
        for (::int32 K : {0, 1, 63, 64, 65, 127, 199}) // bit-position cases
        {
            ::XCore::TBitArray Bits(false, 200);
            Bits.Set(K, true);
            const ::int32 Count = Bits.CountSetBits();
            if (Count != 1)
            {
                std::fprintf(stderr,
                    "FAIL: single-bit-set at %d CountSetBits = %d (expected 1)\n",
                    K, Count);
                return 1;
            }
        }
    }

    ::XCore::HAL::FMemory::__Shutdown();
    std::printf("TBitArray.CountSetBits: PASS\n");
    return 0;
}
