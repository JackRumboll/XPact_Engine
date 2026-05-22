// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// MallocFreeRoundTrip.cpp -- 100K random Malloc/Free cycles.
// =====================================================================
//
// XCore-4a Section 17.1 A1: "100 k random-sized alloc/free workload
// completes without leaks/double-frees; fragmentation < 30%".
//
// This test exercises a 100K random-sized alloc + matching free cycle
// (size in [8, 8192], align in {8, 16, 32}, every alloc tagged
// FMemTag::Container) and asserts GetAllocatedBytes(Container) returns
// 0 after all frees. The fragmentation check is not exercised here
// (that is the perf benchmark's job in Phase 1c); this test is the
// correctness gate.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>
#include <cstdlib>
#include <random>
#include <vector>

int main()
{
    using ::XCore::HAL::FMemory;
    using ::XCore::HAL::FMemTag;

    FMemory::__Init();

    constexpr int kIterations = 100000;
    constexpr ::SIZE_T kMaxAllocSize = 8192;

    std::mt19937_64 RNG(42);  // deterministic seed (Section 4.3 contract)
    std::uniform_int_distribution<::SIZE_T> SizeDist(8, kMaxAllocSize);
    std::uniform_int_distribution<::SIZE_T> AlignDist(0, 2);  // index into {8, 16, 32}
    const ::SIZE_T Aligns[3] = { 8, 16, 32 };

    std::vector<void*> Pointers;
    Pointers.reserve(kIterations);

    // Allocate phase.
    for (int I = 0; I < kIterations; ++I)
    {
        const ::SIZE_T Size  = SizeDist(RNG);
        const ::SIZE_T Align = Aligns[AlignDist(RNG)];
        void* P = FMemory::Malloc(Size, Align, FMemTag::Container);
        if (P == nullptr)
        {
            std::fprintf(stderr, "FAIL: Malloc returned null at iter %d (size=%llu align=%llu)\n",
                         I, static_cast<unsigned long long>(Size),
                         static_cast<unsigned long long>(Align));
            return 1;
        }
        Pointers.push_back(P);
    }

    // Free phase.
    for (void* P : Pointers)
    {
        FMemory::Free(P);
    }

    // Round-trip check.
    const ::uint64 RemainingBytes = FMemory::GetAllocatedBytes(FMemTag::Container);
    if (RemainingBytes != 0)
    {
        std::fprintf(stderr, "FAIL: GetAllocatedBytes(Container) = %llu after all frees (expected 0)\n",
                     static_cast<unsigned long long>(RemainingBytes));
        return 1;
    }

    FMemory::__Shutdown();
    std::printf("MallocFreeRoundTrip: PASS\n");
    return 0;
}
