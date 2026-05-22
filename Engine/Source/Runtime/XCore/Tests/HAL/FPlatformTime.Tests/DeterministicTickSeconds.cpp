// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FPlatformTime.Tests/DeterministicTickSeconds.cpp -- sim-path tick.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.3 (determinism contract). The sim-path-safe
// DeterministicTickSeconds is pure arithmetic over the kTickRate
// constant; the test verifies the documented identity:
//
//   DeterministicTickSeconds(TickIndex) == TickIndex / kTickRate
//
// At the default kTickRate = 60 Hz:
//   DeterministicTickSeconds(120) - DeterministicTickSeconds(60) == 1.0
//
// This must be bit-exact across all three platforms (acceptance
// criterion D-extra for the math two-header model + Section 6.6).
//
// All checks are static_assert because DeterministicTickSeconds is
// constexpr-inline in the header (Section 7 spec line 137-140).
//
// =====================================================================

#include "HAL/FPlatformTime.h"

#include <cstdint>
#include <iostream>

namespace XCore::HAL::Tests::DeterministicTickSecondsTest
{

// Compile-time identity at the 60 Hz default kTickRate.
static_assert(FPlatformTime::DeterministicTickSeconds(0)  == 0.0,
              "DeterministicTickSeconds(0) == 0.0");

static_assert(FPlatformTime::DeterministicTickSeconds(60) ==
              FPlatformTime::DeterministicTickSeconds(0) + 1.0,
              "DeterministicTickSeconds advances by 1.0 second per 60 ticks @ 60 Hz");

static_assert(FPlatformTime::DeterministicTickSeconds(120) -
              FPlatformTime::DeterministicTickSeconds(60) == 1.0,
              "DeterministicTickSeconds(120) - DeterministicTickSeconds(60) == 1.0 @ 60 Hz");

static_assert(FPlatformTime::DeterministicTickSeconds(3600) == 60.0,
              "DeterministicTickSeconds(3600) == 60.0 (60 seconds @ 60 Hz)");

// Bit-exactness: the divide by kTickRate constant-folds; the result
// is a bit-exact double across all three platforms (no libm; pure
// IEEE 754 arithmetic).

} // namespace XCore::HAL::Tests::DeterministicTickSecondsTest

int main()
{
    // The actual test is the static_asserts above; this binary exists
    // so the test runner can confirm the TU compiled.
    std::cout << "DeterministicTickSeconds: PASS (compile-time checks)\n";
    return 0;
}
