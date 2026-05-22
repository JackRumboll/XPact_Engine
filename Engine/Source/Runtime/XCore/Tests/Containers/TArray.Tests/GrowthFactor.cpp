// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TArray.Tests/GrowthFactor.cpp -- verify 1.375x grow formula.
// =====================================================================
//
// XCore-4a Section 5.1 ("NewCap = NewMax + (3/8) * NewMax + 16 (about
// 1.375x), quantised by the allocator's QuantizeSize") + Section 5.6
// test-strategy bullet "Add/Remove/Reserve/Reset round-trips per type".
//
// Adds 100 int32 elements; at each Add that triggers a grow, asserts:
//   * The new Max() is at least ComputeGrowCapacity(prev_max + 1,
//     prev_max), which is the spec's 1.375x + 16 formula.
//   * The new Max() is reasonable (not absurdly large; we cap the
//     ratio at 2.0x to catch a bug that would double-grow per step).
//
// The allocator's bin-rounding may push Max() slightly above the
// computed value -- we expect >= the computed value, never <.
//
// =====================================================================

#include "Containers/TArray.h"
#include "Containers/TArrayCore.h"  // ComputeGrowCapacity
#include "HAL/FMemory.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>

int main()
{
    ::XCore::HAL::FMemory::__Init();

    {
        ::XCore::TArray<::int32, ::XCore::DefaultAllocator> Arr;
        ::int32 PrevMax = 0;
        ::int32 GrowCount = 0;

        for (::int32 I = 0; I < 100; ++I)
        {
            Arr.Add(I);

            if (Arr.Max() != PrevMax)
            {
                // A grow happened. Compute the expected new max via
                // the spec formula.
                const ::int32 ComputedNewMax =
                    ::XCore::Detail::ComputeGrowCapacity(I + 1, PrevMax);

                if (Arr.Max() < ComputedNewMax)
                {
                    std::fprintf(stderr,
                        "FAIL: grow at I=%d produced Max=%d (expected >= %d)\n",
                        I, Arr.Max(), ComputedNewMax);
                    return 1;
                }

                // Sanity cap: the new max should not exceed 2x the
                // computed (would indicate a misimplementation).
                if (Arr.Max() > ComputedNewMax * 2 && ComputedNewMax >= 8)
                {
                    std::fprintf(stderr,
                        "FAIL: grow at I=%d produced Max=%d (expected within 2x of %d)\n",
                        I, Arr.Max(), ComputedNewMax);
                    return 1;
                }

                ++GrowCount;
                PrevMax = Arr.Max();
            }
        }

        // Sanity: at least 2 grows happened in 100 Adds (the first
        // alloc is 4 elements; the second grow should fire around
        // I=4; subsequent grows every 1.375x).
        if (GrowCount < 2)
        {
            std::fprintf(stderr,
                "FAIL: only %d grows in 100 Adds (expected >= 2)\n", GrowCount);
            return 1;
        }
    }

    ::XCore::HAL::FMemory::__Shutdown();
    std::printf("TArray.GrowthFactor: PASS (grows verified against formula)\n");
    return 0;
}
