// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TSet.Tests/Rehash.cpp -- forced rehash at 7/8 load factor.
// =====================================================================
//
// XCore-4a Section 5.6: "force rehashes at the 7/8 load factor; assert
// no element loss".
//
// Strategy: add elements one at a time. At the moment the set grows
// past its current 7/8 threshold the implementation should rehash; we
// verify that every previously-Added element is still findable AFTER
// the implicit rehash.
//
// We add sequential integers 0..N to keep the test deterministic and
// the post-rehash search trivial to verify. The expected behavior:
// after adding N elements, every value in [0, N) is findable; the
// set's Num() equals N; and Max() (capacity) is >= the smallest
// power-of-2 satisfying the 7/8 load factor for N.
//
// =====================================================================

#include "Containers/TSet.h"
#include "HAL/FMemory.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>

int main()
{
    ::XCore::HAL::FMemory::__Init();

    {
        // Add a healthy 4096 elements -- this forces multiple rehashes
        // (the initial 16-slot capacity grows to 32, 64, 128, 256, ...
        // 4096 / 8192 / 16384 depending on the exact growth pattern at
        // 7/8 load factor).
        constexpr ::int32 kNumElements = 4096;
        ::XCore::TSet<::int32> S;

        ::int32 PreviousMax = 0;
        for (::int32 I = 0; I < kNumElements; ++I)
        {
            if (!S.Add(I))
            {
                std::fprintf(stderr, "FAIL: Add(%d) returned false (should be true)\n", I);
                ::XCore::HAL::FMemory::__Shutdown();
                return 1;
            }

            // After each Add the size must equal I+1.
            if (S.Num() != I + 1)
            {
                std::fprintf(stderr,
                    "FAIL: after Add(%d), Num()=%d (expected %d)\n",
                    I, S.Num(), I + 1);
                ::XCore::HAL::FMemory::__Shutdown();
                return 1;
            }

            // Max() (capacity) is monotonically non-decreasing.
            if (S.Max() < PreviousMax)
            {
                std::fprintf(stderr,
                    "FAIL: Max() shrank from %d to %d at I=%d\n",
                    PreviousMax, S.Max(), I);
                ::XCore::HAL::FMemory::__Shutdown();
                return 1;
            }
            PreviousMax = S.Max();
        }

        // Every value in [0, kNumElements) must be findable post-rehash.
        for (::int32 I = 0; I < kNumElements; ++I)
        {
            if (!S.Contains(I))
            {
                std::fprintf(stderr,
                    "FAIL: post-rehash Contains(%d) false (element lost during grow)\n",
                    I);
                ::XCore::HAL::FMemory::__Shutdown();
                return 1;
            }
        }

        // Capacity should be at least 7/8-load-factor-compatible.
        // For N=4096, the smallest power-of-2 P such that 7/8 * P >= 4096
        // is P = 8192 (since 7/8 * 4096 = 3584 < 4096). Verify Max() is
        // at least 8192.
        if (S.Max() < 8192)
        {
            std::fprintf(stderr,
                "FAIL: Max()=%d after adding %d elements (expected >= 8192)\n",
                S.Max(), kNumElements);
            ::XCore::HAL::FMemory::__Shutdown();
            return 1;
        }
    }

    ::XCore::HAL::FMemory::__Shutdown();
    std::printf("TSet.Rehash: PASS\n");
    return 0;
}
