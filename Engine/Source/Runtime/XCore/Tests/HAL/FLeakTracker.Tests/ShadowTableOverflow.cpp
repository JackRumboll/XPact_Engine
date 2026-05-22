// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// ShadowTableOverflow.cpp -- LRU eviction smoke test.
// =====================================================================
//
// XCore-4a Section 12.2 fix A-MIN5: "On overflow the table evicts the
// oldest entry (LRU); an eviction emits a single warning of the form
// 'shadow table overflow at N allocations; oldest entries forgotten'
// and increments a leak-attribution-loss counter that the captured
// report surfaces."
//
// This test allocates more than kMaxLiveAllocations records to trigger
// LRU eviction. The kMaxLiveAllocations constant is 10 M; allocating
// 10 M + 1 records in a unit test is impractical (the test would take
// minutes, and consume gigabytes of VM). Instead the test:
//
//   * Confirms that GetLiveAllocationCount stays well below
//     kMaxLiveAllocations after a modest churn (sanity check that
//     OnFree decrements correctly).
//   * Confirms that GetSkippedAttributionCount == 0 for a workload
//     that never hits the eviction threshold.
//
// The actual overflow trigger is gated on Phase 1c when we can
// either lower the threshold via a test-only setter, or use a perf
// rig that has the time/RAM budget. For Phase 1b we exercise the
// counter-correctness side of the contract.
//
// TODO(Phase 1c): out-of-process / parameterised test that lowers
// kMaxLiveAllocations to ~10k via a test-only setter, allocates
// 11k items, and verifies GetSkippedAttributionCount > 0 + the
// warning message appears on stderr.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XCoreDefines.h"

#if XPACT_LEAK_TRACKING_ENABLED

#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "HAL/FLeakTracker.h"

#include <cstdio>
#include <vector>

int main()
{
    using ::XCore::HAL::FMemory;
    using ::XCore::HAL::FMemTag;
    using ::XCore::HAL::FLeakTracker;

    FMemory::__Init();
    FLeakTracker::__Init();

    // Churn 10k allocations.
    constexpr int kChurn = 10000;
    std::vector<void*> Ptrs;
    Ptrs.reserve(kChurn);

    for (int I = 0; I < kChurn; ++I)
    {
        void* P = FMemory::Malloc(32, 8, FMemTag::Container);
        if (P == nullptr)
        {
            std::fprintf(stderr, "FAIL: Malloc null at iter %d\n", I);
            return 1;
        }
        Ptrs.push_back(P);
    }

    const ::SIZE_T LiveAfterAlloc = FLeakTracker::GetLiveAllocationCount();
    if (LiveAfterAlloc < static_cast<::SIZE_T>(kChurn))
    {
        std::fprintf(stderr,
                     "FAIL: GetLiveAllocationCount = %llu after %d allocs (expected >= %d)\n",
                     static_cast<unsigned long long>(LiveAfterAlloc), kChurn, kChurn);
        return 1;
    }

    // Free all.
    for (void* P : Ptrs)
    {
        FMemory::Free(P);
    }

    const ::SIZE_T LiveAfterFree = FLeakTracker::GetLiveAllocationCount();
    if (LiveAfterFree != 0)
    {
        std::fprintf(stderr,
                     "FAIL: GetLiveAllocationCount = %llu after all frees (expected 0)\n",
                     static_cast<unsigned long long>(LiveAfterFree));
        return 1;
    }

    // No overflow at 10k.
    const ::SIZE_T Skipped = FLeakTracker::GetSkippedAttributionCount();
    if (Skipped != 0)
    {
        std::fprintf(stderr,
                     "FAIL: GetSkippedAttributionCount = %llu (expected 0; 10k well below 10M cap)\n",
                     static_cast<unsigned long long>(Skipped));
        return 1;
    }

    FLeakTracker::__Shutdown();
    FMemory::__Shutdown();
    std::printf("ShadowTableOverflow: PASS\n");
    return 0;
}

#else

#include <cstdio>
int main()
{
    std::printf("ShadowTableOverflow: SKIP (XPACT_LEAK_TRACKING_ENABLED = 0)\n");
    return 0;
}

#endif
