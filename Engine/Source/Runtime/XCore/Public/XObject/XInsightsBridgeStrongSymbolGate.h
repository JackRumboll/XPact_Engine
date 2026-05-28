// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XInsightsBridgeStrongSymbolGate.h -- compile-time gate that disables
// the XInsights bridge stub when a strong-symbol provider is in the
// link (XCoreXObject Rev 4 §10.5; mirror of
// XGCWriteBarrierStrongSymbolGate.h pattern).
// =====================================================================
//
// This header exists to address the MSVC link-time hazard described in
// XInsightsBridge.h: MSVC has no portable function-level weak-symbol
// attribute, so the default stub's `XCore_XInsights_Emit` body ships
// as strong-linkage on MSVC. If XInsights's strong provider TU ALSO
// ships its strong-linkage body in the same link unit, MSVC raises
// LNK2005 (duplicate symbol).
//
// THE GATE MECHANISM:
//
//   * Public/XObject/XInsightsBridgeStub.cpp's body is wrapped in
//       #if !defined(XPACT_HAS_XINSIGHTS_STRONG_BRIDGE) || \
//           (XPACT_HAS_XINSIGHTS_STRONG_BRIDGE == 0)
//     ...
//       #endif
//
//   * When the consumer DLL (XInsights) ships its strong provider, it
//     ALSO includes this gate header somewhere in its compilation
//     scope. The gate header defines
//       #define XPACT_HAS_XINSIGHTS_STRONG_BRIDGE 1
//     unconditionally. The stub TU then compiles to an empty unit on
//     MSVC, eliminating the LNK2005 hazard.
//
// USAGE CONVENTION (when XInsights ships):
//
//   1. XInsights's `Engine/Source/Runtime/XInsights/` module ships
//      a TU that defines a strong `XCore_XInsights_Emit` body.
//   2. That TU's CPP file (or the XInsights module's PCH / first-
//      include header) includes
//        #include "XObject/XInsightsBridgeStrongSymbolGate.h"
//      to bump XPACT_HAS_XINSIGHTS_STRONG_BRIDGE to 1 across the
//      module.
//   3. The build system links XInsights into the same target as
//      XCoreXObject. The gate's #define propagates to the same
//      compile target (because the stub TU also includes the gate
//      header) and the stub body is excluded.
//
// On Clang/GCC the gate is COSMETIC: the [[gnu::weak]] attribute on
// the stub's body would let the strong override win even without the
// gate. The gate is still included for symmetry + so a hypothetical
// future Clang/GCC ABI change does not break the layering.
//
// NOTE on transitive includes: this header is intentionally LIGHT
// (zero #include) so it can be included from any compile unit without
// pulling in C++ header churn. The single `#define` is the entire
// surface.
//
// =====================================================================

// Phase 5.k: when the strong provider lands (XInsights ships its own
// implementation), that provider's compile scope includes this gate
// header to suppress the stub TU body.
//
// At Phase 5.k, XInsights has NOT shipped yet (the XCoreXObject layer
// ships the weak stub only). The default `XPACT_HAS_XINSIGHTS_STRONG_
// BRIDGE` is undefined (or zero); the stub body compiles.
//
// When a later phase ships the XInsights strong provider, that
// provider's module Build.toml will arrange for this gate header to
// be force-included into the XCoreXObject stub TU's compile (via the
// XInsights module's Public include path + an explicit #include in
// the stub TU's own header chain), thereby bumping the macro to 1 and
// excluding the stub body.
//
// The gate header is INTENTIONALLY NOT a #pragma once-only file; the
// single #define MUST land exactly once per TU regardless of include
// count. The standard #pragma once + redefinition guards below cover
// both cases.

#ifndef XPACT_HAS_XINSIGHTS_STRONG_BRIDGE
    // The gate is currently NOT firing -- this is the Phase 5.k
    // baseline. When XInsights ships, its providing TU's transitive
    // include chain will override this default by including the gate
    // ahead of XInsightsBridgeStub.cpp's body and thereby defining
    // the macro to 1.
    //
    // The default of "undefined" rather than "0" is deliberate: it
    // means the stub body's `#if !defined(...) || (... == 0)` test
    // takes the non-skip branch cleanly, AND a typo at the consumer
    // side (defining the macro without a value) does not silently
    // disable the gate (preprocessor-defined macros without values
    // are truthy in `#if` only via empty-expansion, which the
    // explicit `== 0` check catches).
    //
    // The macro NAME is documented in the strong-provider's TU; the
    // consumer-side wiring is documented here.
#endif

// Diagnostic: when a strong override is wired (post-Phase-5.k), the
// downstream consumer can include this header to surface a one-line
// compile-time diagnostic stating that the bridge is wired strong.
// The diagnostic uses a pragma message rather than a #warning so it
// is informational only (Build succeeds).
//
// The diagnostic is gated on XPACT_DEBUG so production builds do not
// emit it.
#if defined(XPACT_HAS_XINSIGHTS_STRONG_BRIDGE) && (XPACT_HAS_XINSIGHTS_STRONG_BRIDGE != 0)
    #if defined(XPACT_DEBUG) && XPACT_DEBUG
        // The pragma form differs MSVC vs Clang/GCC. Both are
        // information-only.
        #if defined(_MSC_VER)
            #pragma message("XCoreXObject: XInsights strong bridge gate is active; stub TU body disabled.")
        #else
            #pragma message "XCoreXObject: XInsights strong bridge gate is active; stub TU body disabled."
        #endif
    #endif
#endif
