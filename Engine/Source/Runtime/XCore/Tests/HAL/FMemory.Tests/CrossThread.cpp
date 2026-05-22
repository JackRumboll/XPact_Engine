// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// CrossThread.cpp -- 8-thread alloc+cross-thread-free hammer.
// =====================================================================
//
// XCore-4a Section 17.1 A2: "Cross-thread alloc/free via SPSC queue:
// no leaks, TSan-clean."
//
// 8 threads each allocate N blocks; they swap their allocations with
// each other via a shared exchange array; each thread then frees the
// blocks it received. The test asserts:
//   * No null returns from Malloc.
//   * GetAllocatedBytes(Container) returns 0 at the end.
//   * The test runs to completion without deadlock.
//
// TSan cleanliness is verified by running this test under
// `clang++ -fsanitize=thread` (XBT will eventually integrate this as
// a build target; Phase 1b documents the manual invocation).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "Macros/XCoreTypes.h"

#include <atomic>
#include <cstdio>
#include <thread>
#include <vector>

int main()
{
    using ::XCore::HAL::FMemory;
    using ::XCore::HAL::FMemTag;

    FMemory::__Init();

    constexpr int kThreadCount       = 8;
    constexpr int kAllocsPerThread   = 1000;
    constexpr ::SIZE_T kAllocSize    = 128;
    constexpr ::SIZE_T kAlign        = 16;

    // Per-thread allocation arrays. Each thread allocates into its
    // own slot; cross-thread free reads from a neighbour's slot.
    std::vector<std::vector<void*>> ThreadAllocations(kThreadCount);
    for (auto& V : ThreadAllocations)
    {
        V.resize(kAllocsPerThread, nullptr);
    }

    std::atomic<bool> AllocPhaseDone{false};

    auto WorkerFn = [&](int ThreadIdx) {
        // Allocate phase.
        for (int I = 0; I < kAllocsPerThread; ++I)
        {
            void* P = FMemory::Malloc(kAllocSize, kAlign, FMemTag::Container);
            if (P == nullptr)
            {
                std::fprintf(stderr, "FAIL: Malloc null in thread %d alloc %d\n", ThreadIdx, I);
                std::exit(1);
            }
            ThreadAllocations[ThreadIdx][I] = P;
        }
    };

    // Phase 1: parallel alloc.
    {
        std::vector<std::thread> Workers;
        for (int I = 0; I < kThreadCount; ++I)
        {
            Workers.emplace_back(WorkerFn, I);
        }
        for (auto& T : Workers)
        {
            T.join();
        }
    }

    AllocPhaseDone.store(true);

    // Phase 2: cross-thread free. Thread N frees thread (N+1)%
    // kThreadCount's allocations.
    auto FreeFn = [&](int ThreadIdx) {
        const int TargetIdx = (ThreadIdx + 1) % kThreadCount;
        for (int I = 0; I < kAllocsPerThread; ++I)
        {
            FMemory::Free(ThreadAllocations[TargetIdx][I]);
        }
    };

    {
        std::vector<std::thread> Workers;
        for (int I = 0; I < kThreadCount; ++I)
        {
            Workers.emplace_back(FreeFn, I);
        }
        for (auto& T : Workers)
        {
            T.join();
        }
    }

    // Verify.
    const ::uint64 Remaining = FMemory::GetAllocatedBytes(FMemTag::Container);
    if (Remaining != 0)
    {
        std::fprintf(stderr, "FAIL: GetAllocatedBytes(Container) = %llu after cross-thread free\n",
                     static_cast<unsigned long long>(Remaining));
        return 1;
    }

    FMemory::__Shutdown();
    std::printf("CrossThread: PASS\n");
    return 0;
}
