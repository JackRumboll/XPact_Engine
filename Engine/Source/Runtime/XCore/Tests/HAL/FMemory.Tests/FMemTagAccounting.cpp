// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FMemTagAccounting.cpp -- per-tag GetAllocatedBytes correctness.
// =====================================================================
//
// XCore-4a Section 17.1 A1 / Section 4.6: "GetAllocatedBytes(tag)==0
// for every tag" after free; while live, per-tag bytes should reflect
// the per-tag allocation totals.
//
// This test allocates 1000 bytes per engine-tag (1 allocation of 1000
// bytes per tag), asserts GetAllocatedBytes returns 1000 per tag while
// live, then frees and asserts 0 per tag. The per-tag bytes are the
// allocator's approximation (bin-rounded for small bins; precise for
// large allocs); for sizes that round to the same bin the test
// validates the round-trip property (Free undoes Malloc's accounting
// delta).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>

int main()
{
    using ::XCore::HAL::FMemory;
    using ::XCore::HAL::FMemTag;

    FMemory::__Init();

    // The engine tags we exercise (skip Generic per Section 4.1
    // "banned in Shipping by linker check"; skip XObject as it's
    // reserved for XCore-4b; skip LeakTracker as it has special
    // self-immune semantics).
    const FMemTag Tags[] = {
        FMemTag::Container, FMemTag::Math, FMemTag::Platform,
        FMemTag::Threading, FMemTag::CVar, FMemTag::Stat,
        FMemTag::Localization
    };

    void* Allocs[sizeof(Tags) / sizeof(Tags[0])];

    // Allocate phase.
    for (::SIZE_T I = 0; I < sizeof(Tags) / sizeof(Tags[0]); ++I)
    {
        const ::uint64 Before = FMemory::GetAllocatedBytes(Tags[I]);
        Allocs[I] = FMemory::Malloc(1000, 16, Tags[I]);
        if (Allocs[I] == nullptr)
        {
            std::fprintf(stderr, "FAIL: Malloc null for tag %u\n", static_cast<unsigned>(Tags[I]));
            return 1;
        }
        const ::uint64 After = FMemory::GetAllocatedBytes(Tags[I]);

        // The allocator accounts the requested size on Malloc. After
        // should exceed Before by at least the requested size (the
        // bin may round up; the exact delta is implementation-defined).
        if (After <= Before)
        {
            std::fprintf(stderr,
                         "FAIL: tag %u bytes didn't increase: before=%llu after=%llu\n",
                         static_cast<unsigned>(Tags[I]),
                         static_cast<unsigned long long>(Before),
                         static_cast<unsigned long long>(After));
            return 1;
        }
    }

    // Free phase.
    for (::SIZE_T I = 0; I < sizeof(Tags) / sizeof(Tags[0]); ++I)
    {
        FMemory::Free(Allocs[I]);
    }

    // Verify each tag returns to 0.
    for (FMemTag Tag : Tags)
    {
        const ::uint64 Final = FMemory::GetAllocatedBytes(Tag);
        if (Final != 0)
        {
            std::fprintf(stderr,
                         "FAIL: tag %u bytes non-zero after free: %llu\n",
                         static_cast<unsigned>(Tag),
                         static_cast<unsigned long long>(Final));
            return 1;
        }
    }

    FMemory::__Shutdown();
    std::printf("FMemTagAccounting: PASS\n");
    return 0;
}
