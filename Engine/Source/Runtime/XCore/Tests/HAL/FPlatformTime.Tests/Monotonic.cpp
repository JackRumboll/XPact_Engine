// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FPlatformTime.Tests/Monotonic.cpp -- Cycles64 monotonicity.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.2 (threading contract). FPlatformTime is
// documented thread-safe; Cycles64 is monotonically non-decreasing on
// the same thread (the platform contract is delegated to QPC on Win64
// and clock_gettime(CLOCK_MONOTONIC_RAW) on Linux/Android).
//
// Test plan: call Cycles64 1,000,000 times in a tight loop and verify
// every successive value is >= the previous. The spec test strategy
// (Section 7.7) calls for "FPlatformTime monotonicity over 10 M
// samples"; we use 1 M for Phase 1b to keep test runtime sane (the
// 10 M sample run lands in the integration test suite).
//
// =====================================================================

#include "HAL/FPlatformTime.h"

#include <cstdint>
#include <iostream>

int main()
{
    using ::XCore::HAL::FPlatformTime;

    // Initialise the timing subsystem (in the engine bootstrap this
    // happens during PreStaticInit; in this standalone test we call
    // explicitly).
    FPlatformTime::__Init();

    constexpr int N = 1'000'000;
    std::uint64_t Prev = FPlatformTime::Cycles64();
    int Violations = 0;

    for (int i = 0; i < N; ++i)
    {
        const std::uint64_t Cur = FPlatformTime::Cycles64();
        if (Cur < Prev)
        {
            ++Violations;
            if (Violations <= 5)  // limit log spam
            {
                std::cerr << "FAIL: Cycles64 non-monotonic at iter=" << i
                          << " (prev=" << Prev << ", cur=" << Cur << ")\n";
            }
        }
        Prev = Cur;
    }

    if (Violations > 0)
    {
        std::cerr << "FAIL: " << Violations
                  << " monotonicity violations across " << N << " samples\n";
        return 1;
    }

    std::cout << "Monotonic: PASS (1M samples; final cycles=" << Prev << ")\n";
    return 0;
}
