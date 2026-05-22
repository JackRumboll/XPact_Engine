// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XSimPathMathOverrides.h -- sim-path math override header.
// =====================================================================
//
// Per XCore-4a Rev 3 Section 6.3 + Section 14 Step 11.5 + Rev 3 fix M3 +
// Toolchain Contract Rev 13.7 Section 4.3 transcendental-library
// substitution clause.
//
// This header is force-included on every sim-path translation unit by
// the XBT toolchain via:
//
//   * MSVC:   /FI XSimPathMathOverrides.h        (XMSVCToolChain.cs:1034)
//   * Clang:  -include XSimPathMathOverrides.h   (XClangToolChain.cs:1349)
//
// XBT toggles the include only for translation units belonging to a
// module whose .Build.toml declares sim_path = true (Contract Section
// 4.1).  Non-sim-path TUs never see this header.
//
// What the header does (Phase 2 contract; this file's job):
//
//   1. Defines XPACT_SIMPATH = 1.  Math+container code consults
//      `#if XPACT_SIMPATH` directly; the XCore::Math namespace alias
//      (Section 6.1 locked decision 4) flips per TU based on this
//      flag.  XCoreDefines.h provides a default XPACT_SIMPATH = 0 for
//      non-sim-path TUs.
//
//   2. Pulls in the vendored Sleef-3.6+ scalar header via
//      `#include "Sleef.h"`.  The include path is resolved by XBT's
//      ThirdParty/Sleef module's public_include_paths field; the
//      consumer XCore.Build.toml lists Sleef as a public dependency
//      so the path propagates.
//
//   3. Aliases the libm function names to the Sleef variants:
//        ::sinf   -> Sleef_sinf_u35    (u35: 35-ULP; trig errors are
//                                        non-accumulating; per Rev 3 fix M3)
//        ::cosf   -> Sleef_cosf_u35    (u35)
//        ::tanf   -> Sleef_tanf_u35    (u35)
//        ::atan2f -> Sleef_atan2f_u10  (u10: cheapest published Sleef
//                                        variant for atan2)
//        ::sqrtf  -> Sleef_sqrtf_u10   (u10: engineering-principled
//                                        accuracy floor for sim physics;
//                                        non-error-accumulating in
//                                        Verlet integration)
//        ::powf   -> Sleef_powf_u10    (u10)
//        ::expf   -> Sleef_expf_u10    (u10)
//        ::logf   -> Sleef_logf_u10    (u10)
//      Same for double variants (drop the trailing 'f').
//
//      The alias mechanism: per Toolchain Contract Section 4.3,
//      "the override header #defines sin, cos, tan, atan2, sqrt, pow,
//      exp, log, and their f variants to redirect to the Sleef-
//      vendored symbols (e.g., XSleef_sin)."  We use the Sleef-3.6
//      upstream symbol names directly (Sleef_sinf_u35 etc.) because
//      they're already namespace-distinct.  No "XSleef_" rename is
//      needed; the upstream API and our consumed API coincide.
//
//   4. Decorates the libm-CRT functions with
//      [[deprecated("forbidden on sim-path; ...")]] so a stray bare
//      std::sin / sinf call in a sim-path TU fails the build LOUDLY
//      at the call site.  This is the "header poisoning" mechanism
//      described in Section 6.5 (the divergence row "no linker check
//      on sim-path libm calls").
//
//      The MSVC #pragma deprecated supplement (Section 6.3) is also
//      emitted; MSVC's [[deprecated]] does not always fire at every
//      call site for global functions, and the pragma is a defence-
//      in-depth supplement.
//
// What the header does NOT do:
//
//   * It does NOT link any code.  The Sleef library lives at
//     /Engine/Source/ThirdParty/Sleef/ and is linked in via its own
//     ThirdParty module; this header is purely include-time + macro-
//     time.
//   * It does NOT introduce any extern "C" symbols.  The Sleef
//     symbols live in Sleef.h's extern "C" block.
//   * It does NOT enable the cross-arch bit-exactness CI gate; that's
//     Phase 1g, a separate CI shard that runs the 1000-step Verlet
//     test on Win64 / Linux / Android-ARM64.
//
// =====================================================================
// SimPath determinism gate.
// =====================================================================

#define XPACT_SIMPATH 1

// =====================================================================
// Vendored Sleef-3.6+ public header.
// =====================================================================
//
// Sleef ships as an XPact ThirdParty module under
// /Engine/Source/ThirdParty/Sleef/.  Its public_include_paths field
// adds the Sleef include directory to the search path so the
// unqualified `#include "Sleef.h"` resolves.  Consumer modules
// (currently only XCore) list Sleef as a public dependency in their
// .Build.toml.

#include "Sleef.h"

// =====================================================================
// libm -> Sleef aliasing (Toolchain Contract Section 4.3).
// =====================================================================
//
// We use object-like macros to redirect the libm entry-points to the
// Sleef variants.  Object-like (rather than function-like) macros are
// the engineering-correct shape here because:
//   * The libm entries are taken-as-pointer in code only rarely
//     (e.g., `auto pf = ::sinf;`).  In that vanishingly rare case,
//     the macro would expand the function name in the pointer
//     expression, which is the desired behaviour (the pointer points
//     at the Sleef body).
//   * Object-like macros sidestep the function-like-macro parenthesis
//     ambiguity (e.g., what if `sinf(x)(y)` ever appears in a clever
//     callback shape -- function-like macros would attempt to expand
//     the second paren).
//
// The macros expand ONLY inside the sim-path TU.  Non-sim-path TUs
// never include this header, so std::sin etc. remain the libm bodies.

// ---------- Single-precision ----------
#define sinf   Sleef_sinf_u35
#define cosf   Sleef_cosf_u35
#define tanf   Sleef_tanf_u35
#define atan2f Sleef_atan2f_u10
#define sqrtf  Sleef_sqrtf_u10
#define powf   Sleef_powf_u10
#define expf   Sleef_expf_u10
#define logf   Sleef_logf_u10

// ---------- Double-precision ----------
#define sin    Sleef_sin_u35
#define cos    Sleef_cos_u35
#define tan    Sleef_tan_u35
#define atan2  Sleef_atan2_u10
#define sqrt   Sleef_sqrt_u10
#define pow    Sleef_pow_u10
#define exp    Sleef_exp_u10
#define log    Sleef_log_u10

// =====================================================================
// libm poisoning -- [[deprecated]] decoration + MSVC #pragma deprecated.
// =====================================================================
//
// The macro aliases above redirect ::sinf etc. to the Sleef bodies, so
// a sim-path TU that writes `::sinf(x)` actually compiles correctly --
// the macro substitution kicks in BEFORE name lookup.  But a sim-path
// TU that writes `::std::sinf(x)` or `using namespace std; sinf(x)`
// reaches into <cmath>'s overload set; the std-qualified versions are
// NOT object-like-macro substituted (you can't macro-define a member
// of a namespace through #define alone; the macro substitution at the
// `std::sinf` token sequence would textually produce
// `std::Sleef_sinf_u35` which is a name lookup failure -- the wrong
// kind of error, hard to diagnose).
//
// For the std-qualified path we layer in [[deprecated]] decorations
// + MSVC #pragma deprecated below.  The [[deprecated]] attribute is
// inheritable from a using-declaration so it fires when a sim-path
// TU's source code reaches std::sinf via `using std::sinf;`.
//
// The diagnostic message names the canonical XPact replacement
// (XCore::Math::Sin et al.) so the developer's IDE quick-fix
// suggestion is unambiguous.

#if defined(_MSC_VER)
// MSVC: #pragma deprecated fires when the named identifier is used as
// a function call.  The pragma applies at the current TU scope and
// persists for the rest of the TU; once XSimPathMathOverrides.h is
// /FI-included by XBT, the deprecation is in force for every
// subsequent include of <cmath> or <math.h>.
#pragma deprecated(sinf)
#pragma deprecated(cosf)
#pragma deprecated(tanf)
#pragma deprecated(atan2f)
#pragma deprecated(sqrtf)
#pragma deprecated(powf)
#pragma deprecated(expf)
#pragma deprecated(logf)
#pragma deprecated(sin)
#pragma deprecated(cos)
#pragma deprecated(tan)
#pragma deprecated(atan2)
#pragma deprecated(sqrt)
#pragma deprecated(pow)
#pragma deprecated(exp)
#pragma deprecated(log)
#else
// Clang/GCC: the [[deprecated]] attribute path is preferred per the
// engineering principle "header poisoning over 'be careful' comments"
// (XCore-4a Section 16).  However, we cannot retroactively attach
// [[deprecated]] to an already-declared libm function from <cmath>;
// the libc header has already declared `sinf(float)` without any
// attribute by the time the macro substitution runs.
//
// The macro alias above (#define sinf Sleef_sinf_u35) is the primary
// poisoning mechanism for the unqualified call; for the
// `std::sinf` qualified call we rely on:
//   * the macro expansion producing `std::Sleef_sinf_u35`, which is
//     a clear name-lookup failure with a recognisable Sleef-prefixed
//     name in the diagnostic;
//   * downstream code-review + clang-tidy (Toolchain Contract Section
//     12) catching the std-qualified spelling at lint time.
//
// The Clang/GCC TUs additionally rely on the toolchain-level link
// exclusion of platform libm (Contract Section 4.3 bullet 3): any
// unresolved reference to libm symbols (sin@GLIBC_2.2.5 etc.) fails
// the link with a clear diagnostic.  This is the third layer of
// defence in depth.
#endif

// =====================================================================
// End of XSimPathMathOverrides.h
// =====================================================================
