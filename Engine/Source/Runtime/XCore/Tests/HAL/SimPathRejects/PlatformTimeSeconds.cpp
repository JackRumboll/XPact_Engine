// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// SimPathRejects/PlatformTimeSeconds.cpp -- expect-fail TU:
// FPlatformTime::Seconds() in sim-path is a build error.
// =====================================================================
//
// XCore-4a Rev 3 Section 7.3: wall-clock time is non-deterministic
// by definition; sim-path TUs cannot read FPlatformTime::Seconds().
// The Phase 1e sim-path overlay header decorates Seconds() with
// [[deprecated("not sim-path-safe; use ...")]]; warnings-as-errors
// turns the deprecation into a hard build failure.
//
// EXPECT-FAIL CONTRACT (see Math/SimPathRejects/StdSqrt.cpp for the
// rationale). The file is not added to a sim-path test module by
// default; a maintainer flips the sim-path attribute to exercise the
// gate manually.
//
// =====================================================================

#include "HAL/FPlatformTime.h"

double SimPathBadSeconds()
{
    return ::XCore::HAL::FPlatformTime::Seconds();  // <-- expected diagnostic
}

int main()
{
    volatile double T = SimPathBadSeconds();
    (void)T;
    return 0;
}
