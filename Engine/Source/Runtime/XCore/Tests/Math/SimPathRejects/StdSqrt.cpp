// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// SimPathRejects/StdSqrt.cpp -- expect-fail TU: std::sqrt in a sim-path
// translation unit must be a build-time error.
// =====================================================================
//
// XCore-4a Rev 3 Section 6.3 + Section 17.3: the sim-path overlay
// header XSimPathMathOverrides.h (Phase 1e) decorates std::sqrt /
// std::sin / etc. as [[deprecated]] in sim-path TUs, with the
// engine's warnings-as-errors discipline turning the deprecation
// into a hard build failure.
//
// EXPECT-FAIL CONTRACT.
//
// This file is INTENTIONALLY not added to the XCore.Tests Build.toml.
// The XBT test runner has no "expect compile failure" fixture type
// yet (Phase 2 enhancement); instead, this file documents the
// expected failure mode so a maintainer can manually verify by
// flipping the sim-path attribute on this TU's module and observing
// the build error.
//
// To exercise: configure XBT to treat this TU as sim-path (e.g., add
// a sim-path test sub-module pointing here), then build. The compile
// MUST fail with a "deprecated" / "use FMath::Sqrt" diagnostic from
// XSimPathMathOverrides.h.
//
// =====================================================================

#include <cmath>

// This call would be a deprecation error if this TU compiled under
// sim-path discipline (XPACT_SIMPATH == 1).
double SimPathBadSqrt(double x)
{
    return std::sqrt(x);  // <-- expected diagnostic site
}

int main()
{
    // The main() exists so this file is a complete TU. When the file
    // is compiled WITHOUT the sim-path overlay (the default Phase 1g
    // behaviour) it compiles cleanly; the contract is "if you flip
    // the sim-path attribute on this TU, the build fails at the
    // std::sqrt call".
    volatile double X = SimPathBadSqrt(2.0);
    (void)X;
    return 0;
}
