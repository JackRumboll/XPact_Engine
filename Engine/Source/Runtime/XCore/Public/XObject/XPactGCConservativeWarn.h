// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XPactGCConservativeWarn.h -- sim-path Conservative-span build warning
// (XCoreXObject Rev 4 §5.3 + Master Plan §2a Memory/GC; Phase 5.e).
// =====================================================================
//
// XCoreXObject Rev 4 Section 5.3 trailing prose: "XIL2CPP emits a
// build-time warning when a Conservative span appears on a sim-path
// TU. This is the explicit per-mutation root-register/unregister cost
// the Master Plan acknowledges."
//
// PURPOSE: emit a compile-time #pragma message (visible in the build
// log) when an XGCRootSpan of EXGCRootSpanKind::kConservative is
// registered from a sim-path TU. The macro expands to a no-op on
// non-sim-path TUs (the warning is only meaningful on sim-path code
// where the conservative scan's non-determinism + cost is a sim-tick
// boundary concern).
//
// USE PATTERN:
//
//   XPactGCConservativeWarn-emitting site (XIL2CPP-generated):
//
//     // C# source: List<object> backing scan registration
//     XPACT_GC_CONSERVATIVE_WARN("List<object> backing array");
//     ::XCore::XGCRootSpan Span{...};
//     Span.Kind = ::XCore::EXGCRootSpanKind::kConservative;
//     const ::int32 Handle = ::XCore::XGCRootSpanRegistry::Get()
//                                .AddSpan(Span);
//
//   On a sim-path TU the macro expands to a #pragma message that
//   includes the supplied reason; on non-sim-path TUs the macro
//   expands to ((void)0) -- zero footprint.
//
// =====================================================================
// XPACT_SIMPATH detection
// =====================================================================
//
// The XPACT_SIMPATH macro is defined to 1 by XSimPathMathOverrides.h
// when included by a sim-path TU (per XCore-4a §6.3 / §13 + Macros/
// XPactMacros.h §9). Phase 5.e ships the XPACT_GC_CONSERVATIVE_WARN
// macro keyed off the same XPACT_SIMPATH flag.
//
// The Phase 5.e dispatch specifies an `XPACT_SIMPATH_TU` macro; we
// reuse the existing `XPACT_SIMPATH` flag (already wired) rather than
// introducing a parallel symbol. The flag's semantic ("this TU is
// sim-path") is identical between the two names.
//
// =====================================================================
// COMPILER PRAGMA SHIM
// =====================================================================
//
// `_Pragma("message(...)")` is the C99/C++11 portable way to emit a
// build-log message from inside a macro. MSVC, Clang, and GCC all
// support it; MSVC additionally supports `__pragma(message(...))` for
// non-string-form syntax but `_Pragma` is the cross-compiler form.
//
// The XPACT_STRINGIFY helper wraps the reason in quotes so the
// #pragma message body is a valid string literal regardless of the
// caller's reason text (the caller writes
// `XPACT_GC_CONSERVATIVE_WARN(List<object>)` without quotes; the
// expansion adds them).
//
// =====================================================================

#include "Macros/XPactMacros.h"      // XPACT_SIMPATH

// ---------------------------------------------------------------------
// XPACT_GC_CONSERVATIVE_WARN_STRINGIFY -- internal helper that turns
// an unquoted token sequence into a string literal via the standard
// two-level expansion idiom (stringify-the-expansion-of-x).
//
// The "double-expansion" trick is required because the # operator
// stringifies its argument WITHOUT expansion; we need the macro arg
// to expand first (so a caller passing a macro identifier as the
// reason gets the expanded text in the warning) and THEN stringify.
// ---------------------------------------------------------------------
#define XPACT_GC_CONSERVATIVE_WARN_STRINGIFY_(x) #x
#define XPACT_GC_CONSERVATIVE_WARN_STRINGIFY(x)  XPACT_GC_CONSERVATIVE_WARN_STRINGIFY_(x)

// ---------------------------------------------------------------------
// XPACT_GC_CONSERVATIVE_WARN(Reason) -- emit a build-log warning on
// sim-path TUs; no-op on non-sim-path TUs.
//
// The Reason is a free-form token sequence (no quotes required; the
// macro stringifies). It IS visible in the compiler's diagnostics
// output verbatim. Example:
//
//   XPACT_GC_CONSERVATIVE_WARN(List<object> backing array)
//
// On a sim-path TU compiles to:
//
//   _Pragma("message(\"WARNING: Conservative XGCRootSpan registered on
//                    sim-path TU. Reason: List<object> backing array.
//                    Per Prime Directive: prefer typed kObject span
//                    via concrete generic instantiation.\")")
//
// On a non-sim-path TU compiles to ((void)0).
//
// The warning text deliberately points the user at the principled
// alternative (concrete-generic instantiation that XIL2CPP can
// statically type) per Prime Directive ("question every UE-inherited
// decision"; Conservative scanning is a UE-style fallback that
// XPact's static-typing-first discipline should minimise).
// ---------------------------------------------------------------------
#if defined(XPACT_SIMPATH) && (XPACT_SIMPATH != 0)
    #define XPACT_GC_CONSERVATIVE_WARN(Reason)                                                    \
        _Pragma(XPACT_GC_CONSERVATIVE_WARN_STRINGIFY(                                             \
            message("WARNING: Conservative XGCRootSpan registered on sim-path TU. Reason: "       \
                    XPACT_GC_CONSERVATIVE_WARN_STRINGIFY(Reason)                                   \
                    ". Per Prime Directive: prefer typed kObject span via concrete generic "     \
                    "instantiation.")                                                              \
        ))
#else
    #define XPACT_GC_CONSERVATIVE_WARN(Reason) ((void)0)
#endif
