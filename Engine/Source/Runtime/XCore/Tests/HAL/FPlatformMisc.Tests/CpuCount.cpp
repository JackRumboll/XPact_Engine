// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FPlatformMisc.Tests/CpuCount.cpp -- CPU count sanity check.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1. Verifies GetCpuCount returns a
// plausible value (>= 1, <= 256). 256 is the engineering-principle
// upper bound for current desktop / server hardware -- a result above
// suggests a parsing bug (e.g., reading uninitialised SYSTEM_INFO).
//
// =====================================================================

#include "HAL/FPlatformMisc.h"

#include <cstdint>
#include <iostream>

int main()
{
    using ::XCore::HAL::FPlatformMisc;

    const std::uint32_t N = FPlatformMisc::GetCpuCount();

    if (N < 1)
    {
        std::cerr << "FAIL: GetCpuCount returned " << N << " (must be >= 1)\n";
        return 1;
    }
    if (N > 256)
    {
        std::cerr << "FAIL: GetCpuCount returned " << N
                  << " (must be <= 256; sanity-check upper bound)\n";
        return 1;
    }

    std::cout << "CpuCount: PASS (" << N << " logical processors)\n";
    return 0;
}
