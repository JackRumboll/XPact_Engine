// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XCoreDefines.h -- build-configuration flags.
// =====================================================================
//
// XCore-4a Rev 3 Section 13. The five canonical TargetConfigurations
// per master plan and Contract Rev 13.7 Section 9 (TargetRules):
//
//   XPACT_DEBUG        Debug build         -- all asserts live, no opt, leak tracking on
//   XPACT_DEVELOPMENT  Development build   -- asserts live, opt on, leak tracking on
//   XPACT_TEST         Test build          -- asserts as Shipping, opt on, no leak tracking
//   XPACT_SHIPPING     Shipping build      -- asserts compiled out, max opt, no leak tracking
//
// Exactly one of these is `1` per TU; the rest are `0`. XBT defines the
// active configuration via the -D flag on the compiler command line
// (see Engine/Source/Programs/XBT/.../TargetRules.cs). The header
// defaults to XPACT_DEVELOPMENT when no flag is set so that ad-hoc
// compile tests outside the XBT pipeline (e.g. clangd in an IDE, or a
// scratch-cpp paste) reach the assertion-live code path.
//
// Platform flags (XPACT_PLATFORM_WIN64 / XPACT_PLATFORM_LINUX /
// XPACT_PLATFORM_ANDROID) are defined by the Platform HAL header set
// (XCore-4a Step 2, NOT this step). XPactMacros.h's
// XPACT_TLS_MODULE_SAFE expansion consults those flags; if XPactMacros.h
// is included before the platform flags are defined the macro falls
// through to the non-Win64-non-Android branch (which is the Linux
// `thread_local` branch -- safe default for the not-yet-decided case).
// Step 2 will land a small XPlatformProperties.h that defines the
// platform flags before XPactMacros.h sees them.
//
// XPACT_SIMPATH is defined to 1 by `XSimPathMathOverrides.h` when
// included by a sim-path TU (Section 6 / Section 13 cross-reference).
// The default in this header is 0; XBT's per-module sim-path flag
// (Section 9 of the Contract; the per-TU `/FI` flag in XMSVCToolChain
// / XClangToolChain) `#include`s `XSimPathMathOverrides.h` ahead of
// every sim-path TU's preprocessor work, so the override fires before
// any XCore-4a header sees the flag.
//
// =====================================================================

// ---------------------------------------------------------------------
// Build-configuration flags. XBT defines exactly one of these to 1 via
// `-D` on the compiler invocation. Default is XPACT_DEVELOPMENT (the
// "I forgot to pass -DXPACT_SHIPPING=1" failsafe -- assertions live
// rather than silently optimised away).
// ---------------------------------------------------------------------

#if !defined(XPACT_DEBUG) && !defined(XPACT_DEVELOPMENT) && !defined(XPACT_TEST) && !defined(XPACT_SHIPPING)
    #define XPACT_DEVELOPMENT 1
#endif

#ifndef XPACT_DEBUG
    #define XPACT_DEBUG       0
#endif
#ifndef XPACT_DEVELOPMENT
    #define XPACT_DEVELOPMENT 0
#endif
#ifndef XPACT_TEST
    #define XPACT_TEST        0
#endif
#ifndef XPACT_SHIPPING
    #define XPACT_SHIPPING    0
#endif

// Exactly-one-active invariant. The build is misconfigured if zero or
// more than one configuration flag is set to 1; static_assert here
// surfaces the misconfiguration at compile time at every TU rather
// than producing subtly-wrong assertion behaviour.
static_assert((XPACT_DEBUG + XPACT_DEVELOPMENT + XPACT_TEST + XPACT_SHIPPING) == 1,
              "Exactly one of XPACT_DEBUG / XPACT_DEVELOPMENT / XPACT_TEST / XPACT_SHIPPING must be 1");

// ---------------------------------------------------------------------
// Leak-tracking flag (Section 12.5). 1 in Debug + Development; 0 in
// Test + Shipping. The header here exposes the gate; FLeakTracker
// (Step 5 of the dependency graph) consumes it.
// ---------------------------------------------------------------------

#if XPACT_DEBUG || XPACT_DEVELOPMENT
    #define XPACT_LEAK_TRACKING_ENABLED 1
#else
    #define XPACT_LEAK_TRACKING_ENABLED 0
#endif

// ---------------------------------------------------------------------
// Sim-path flag. Default 0; XSimPathMathOverrides.h flips it to 1 on
// sim-path TUs. See Section 6.3 and Section 13. XBT injects the
// override header per-TU via the toolchain's `/FI` (MSVC) /
// `-include` (Clang) flag for modules whose .Build.toml declares
// `sim_path = true`.
// ---------------------------------------------------------------------

#ifndef XPACT_SIMPATH
    #define XPACT_SIMPATH 0
#endif
