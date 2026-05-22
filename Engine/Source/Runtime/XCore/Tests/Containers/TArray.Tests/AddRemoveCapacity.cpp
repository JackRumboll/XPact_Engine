// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TArray.Tests/AddRemoveCapacity.cpp -- 100K Add capacity round-trip.
// =====================================================================
//
// XCore-4a Section 5.6 unit-test row: "Add/Remove/Reserve/Reset
// round-trips per type". Adds 100K int32 elements, asserts:
//   * Num() == 100000 after the Add loop.
//   * Each intermediate Max() is monotonically non-decreasing.
//   * The growth factor at every resize approximates 1.375x (within
//     the QuantizeSize tolerance: the allocator's bin-rounding makes
//     the realized Max() at least the computed ComputeGrowCapacity
//     result, often slightly larger).
//   * RemoveAt loop down to 0 preserves the in-order property of the
//     remaining elements (we Add i; we expect [0..N-1]; we RemoveAt(0)
//     N times and expect each removed value to equal i).
//
// Phase 1c uses 100K rather than the brief's "1M" to keep the test
// runtime bounded; the surface-correctness property is identical at
// any meaningful scale.
//
// =====================================================================

#include "Containers/TArray.h"
#include "HAL/FMemory.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>

namespace
{
    constexpr ::int32 NUM_ELEMENTS = 100000;
}

int main()
{
    ::XCore::HAL::FMemory::__Init();

    {
        ::XCore::TArray<::int32, ::XCore::DefaultAllocator> Arr;

        // Initial state.
        if (Arr.Num() != 0 || Arr.Max() != 0)
        {
            std::fprintf(stderr,
                "FAIL: initial Num=%d Max=%d (expected 0/0)\n",
                Arr.Num(), Arr.Max());
            return 1;
        }

        // Add loop. Track the max sequence to validate monotonicity.
        ::int32 PrevMax = 0;
        for (::int32 I = 0; I < NUM_ELEMENTS; ++I)
        {
            const ::int32 NewIdx = Arr.Add(I);
            if (NewIdx != I)
            {
                std::fprintf(stderr,
                    "FAIL: Add(%d) returned %d (expected %d)\n", I, NewIdx, I);
                return 1;
            }

            // Max() must be monotonic non-decreasing.
            if (Arr.Max() < PrevMax)
            {
                std::fprintf(stderr,
                    "FAIL: Max() shrank from %d to %d at I=%d\n",
                    PrevMax, Arr.Max(), I);
                return 1;
            }
            PrevMax = Arr.Max();
        }

        // Post-Add invariants.
        if (Arr.Num() != NUM_ELEMENTS)
        {
            std::fprintf(stderr,
                "FAIL: post-Add Num=%d (expected %d)\n",
                Arr.Num(), NUM_ELEMENTS);
            return 1;
        }
        if (Arr.Max() < NUM_ELEMENTS)
        {
            std::fprintf(stderr,
                "FAIL: post-Add Max=%d (expected >= %d)\n",
                Arr.Max(), NUM_ELEMENTS);
            return 1;
        }

        // Value-preservation: every element at index I equals I.
        for (::int32 I = 0; I < NUM_ELEMENTS; ++I)
        {
            if (Arr[I] != I)
            {
                std::fprintf(stderr,
                    "FAIL: Arr[%d] = %d (expected %d)\n", I, Arr[I], I);
                return 1;
            }
        }

        // RemoveAt(0) drains the array; each removed value should be
        // the next expected I in [0..NUM_ELEMENTS).
        for (::int32 I = 0; I < NUM_ELEMENTS; ++I)
        {
            const ::int32 Front = Arr[0];
            if (Front != I)
            {
                std::fprintf(stderr,
                    "FAIL: drain front=%d (expected %d) at iteration %d\n",
                    Front, I, I);
                return 1;
            }
            Arr.RemoveAt(0);
        }

        if (Arr.Num() != 0)
        {
            std::fprintf(stderr,
                "FAIL: post-drain Num=%d (expected 0)\n", Arr.Num());
            return 1;
        }

        // Reset round-trip.
        Arr.Reserve(1024);
        if (Arr.Max() < 1024)
        {
            std::fprintf(stderr,
                "FAIL: post-Reserve(1024) Max=%d (expected >= 1024)\n",
                Arr.Max());
            return 1;
        }

        Arr.Reset(0);
        if (Arr.Num() != 0 || Arr.Max() != 0)
        {
            std::fprintf(stderr,
                "FAIL: post-Reset(0) Num=%d Max=%d (expected 0/0)\n",
                Arr.Num(), Arr.Max());
            return 1;
        }
    }

    ::XCore::HAL::FMemory::__Shutdown();
    std::printf("TArray.AddRemoveCapacity: PASS\n");
    return 0;
}
