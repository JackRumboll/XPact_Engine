// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// SyntheticLeak.cpp -- 100x64-byte leak detection test.
// =====================================================================
//
// XCore-4a Section 12.7 / Section 17.9 I1: "Deliberate leak in Debug
// reported at exit with full callstack on all three platforms";
// dispatch spec for this test: "100x64-byte leak; assert FLeakReport
// has count=100, bytes=6400, single stack bucket".
//
// The "single stack bucket" portion is gated on Phase 1c (TArray<
// FStackBucket> needs to be populated). For Phase 1b this test
// asserts the aggregate counts only (TotalLeakedBytes = 6400,
// LeakedAllocationCount = 100). The per-bucket TArray validation is
// the (Phase 1c) follow-up.
//
// Gated on XPACT_LEAK_TRACKING_ENABLED. In Test + Shipping (where
// tracking is off), this test compiles to a no-op main().
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XCoreDefines.h"

#if XPACT_LEAK_TRACKING_ENABLED

#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "HAL/FLeakTracker.h"

#include <cstdio>

int main()
{
    using ::XCore::HAL::FMemory;
    using ::XCore::HAL::FMemTag;
    using ::XCore::HAL::FLeakTracker;

    FMemory::__Init();
    FLeakTracker::__Init();

    // Allocate 100 blocks of 64 bytes each; do NOT free them.
    constexpr int kLeakCount    = 100;
    constexpr ::SIZE_T kLeakSize = 64;

    for (int I = 0; I < kLeakCount; ++I)
    {
        void* P = FMemory::Malloc(kLeakSize, 8, FMemTag::Container);
        if (P == nullptr)
        {
            std::fprintf(stderr, "FAIL: Malloc returned null at alloc %d\n", I);
            return 1;
        }
        (void)P;  // intentionally leaked
    }

    // Capture the report.
    FLeakTracker::FLeakReport Report;
    FLeakTracker::CaptureReport(Report);

    // Assert TotalLeakedBytes >= 6400 (the actual byte count is the
    // sum of requested sizes; per-alloc accounting in OnMalloc stores
    // the requested size, so the total should equal exactly 100 * 64
    // = 6400).
    constexpr ::SIZE_T kExpectedBytes = static_cast<::SIZE_T>(kLeakCount) * kLeakSize;
    if (Report.TotalLeakedBytes != kExpectedBytes)
    {
        std::fprintf(stderr, "FAIL: TotalLeakedBytes = %llu (expected %llu)\n",
                     static_cast<unsigned long long>(Report.TotalLeakedBytes),
                     static_cast<unsigned long long>(kExpectedBytes));
        return 1;
    }

    if (Report.LeakedAllocationCount != static_cast<::SIZE_T>(kLeakCount))
    {
        std::fprintf(stderr, "FAIL: LeakedAllocationCount = %llu (expected %d)\n",
                     static_cast<unsigned long long>(Report.LeakedAllocationCount),
                     kLeakCount);
        return 1;
    }

    // TODO(Phase 1c): verify Report.Buckets.Num() == 1 + every
    // bucket's TotalBytes == 6400. Requires TArray<FStackBucket>
    // working body.

    // Don't bother freeing the leaks -- the shutdown report should
    // surface them as "LEAKS at shutdown".
    FLeakTracker::__Shutdown();
    FMemory::__Shutdown();
    std::printf("SyntheticLeak: PASS\n");
    return 0;
}

#else  // XPACT_LEAK_TRACKING_ENABLED == 0

#include <cstdio>
int main()
{
    std::printf("SyntheticLeak: SKIP (XPACT_LEAK_TRACKING_ENABLED = 0)\n");
    return 0;
}

#endif
