// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FDateTime.Tests/UtcNowSanity.cpp -- UtcNow plausibility check.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.5. Verifies FDateTime::UtcNow returns a
// timestamp within a plausible window:
//
//   Lower bound: 2026-01-01T00:00:00Z (the current XPact dev cycle)
//   Upper bound: 2030-12-31T23:59:59Z (5 years out for build longevity)
//
// A value outside this window indicates either (a) the wall-clock-
// origin capture failed at __Init, or (b) the platform clock is
// catastrophically misconfigured. Either case is a bug worth a build
// failure.
//
// =====================================================================

#include "HAL/FDateTime.h"
#include "HAL/FPlatformTime.h"

#include <cstdint>
#include <iostream>

int main()
{
    using ::XCore::HAL::FDateTime;
    using ::XCore::HAL::FPlatformTime;

    // Initialise platform time + FDateTime origin capture. In the
    // engine bootstrap this happens in PreStaticInit; in the standalone
    // test we call FPlatformTime::__Init explicitly and let the lazy
    // EnsureInitialised inside UtcNow handle FDateTime's capture.
    FPlatformTime::__Init();

    const FDateTime Now = FDateTime::UtcNow();
    const std::int64_t NowMicros = Now.ToUnixMicros();

    // Bounds: 2026-01-01T00:00:00Z = 1767225600 unix seconds
    //         2030-12-31T23:59:59Z = 1924991999 unix seconds
    constexpr std::int64_t LOWER_MICROS = 1767225600LL  * 1'000'000LL;
    constexpr std::int64_t UPPER_MICROS = 1924991999LL  * 1'000'000LL;

    if (NowMicros < LOWER_MICROS)
    {
        std::cerr << "FAIL: UtcNow=" << NowMicros
                  << " < lower bound 2026-01-01 (" << LOWER_MICROS << ")\n";
        return 1;
    }
    if (NowMicros > UPPER_MICROS)
    {
        std::cerr << "FAIL: UtcNow=" << NowMicros
                  << " > upper bound 2030-12-31 (" << UPPER_MICROS << ")\n";
        return 1;
    }

    std::cout << "UtcNowSanity: PASS (now=" << NowMicros
              << " us since epoch, year=" << Now.Year()
              << " month=" << Now.Month()
              << " day=" << Now.Day() << ")\n";
    return 0;
}
