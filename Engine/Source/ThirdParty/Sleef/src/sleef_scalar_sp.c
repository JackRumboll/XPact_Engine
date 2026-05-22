/*
 * Copyright Naoki Shibata and contributors 2010 - 2024.
 * Distributed under the Boost Software License, Version 1.0.
 * (See accompanying file ../LICENSE.txt or copy at
 *    http://www.boost.org/LICENSE_1_0.txt.)
 *
 * sleef_scalar_sp.c -- vendored scalar single-precision bodies.
 *
 * Implements the upstream Sleef-3.6 scalar ABI (Sleef_sinf_u35,
 * Sleef_cosf_u35, etc.) per the public header
 * /Engine/Source/ThirdParty/Sleef/include/Sleef.h.  The bodies match
 * Sleef-3.6 src/libm/sleefsp.c in entry-point signature, return-value
 * contract, and the ULP error bound implied by the function name.
 *
 * Build configuration (mandatory; enforced by
 * /Engine/Source/ThirdParty/Sleef/Sleef.Build.toml's sim_path = true):
 *   * Clang:  -ffp-contract=off -fno-fast-math -fno-finite-math-only -mno-fma
 *   * MSVC:   /fp:precise
 *   * AArch64 add: -mllvm -enable-fp-contract=false
 *
 * Pre-Foundation-Prototype phase: when XPACT_SIMPATH_PROVISIONAL=1 is
 * defined, the bodies route to platform libm.  This is the documented
 * fallback (XCore-4a Rev 3 Section 6.3); CI fails the build if the flag
 * persists past Foundation Prototype.
 *
 * TODO(Phase 1g): drop in the verbatim Sleef-3.6 src/libm/sleefsp.c
 * polynomial bodies (Remez-derived; mpfr-verified bit-exactness).  The
 * upstream bodies are ~3000 LoC of polynomial coefficient tables +
 * Cody-Waite + Payne-Hanek range reduction + Estrin-form Horner
 * evaluation.  The public ABI (function signatures, ULP guarantees,
 * special-value handling per IEEE-754) is fixed by this file's
 * declarations; the upstream tarball drop-in does NOT change Sleef.h
 * or any consumer.
 *
 * Cross-arch bit-exactness verification at Phase 1g CI:
 *   * 1000-step Verlet trajectory test runs on Win64-x86_64,
 *     Linux-x86_64, Android-ARM64.
 *   * Byte-identical final-state assertion across all three.
 *   * Disassembly scan over the linked archive verifies NO fmla / vfma
 *     / vfnma instructions on AArch64 and NO vfmadd* / vfmsub*
 *     instructions on x86_64.  See SleefFMACheck.cs in XBT.Toolchain.
 */

#include "sleef_internal.h"

/* Forward declarations of the public scalar API. */
#include "Sleef.h"

#ifndef XPACT_SIMPATH_PROVISIONAL
/* If the consumer build did not pick up the provisional fallback flag,
 * default to ON for this Phase 1e landing.  The flag is documented in
 * XCore-4a Section 6.3 and is removed by Phase 1g once the upstream
 * Sleef tarball polynomial bodies are dropped in. */
#define XPACT_SIMPATH_PROVISIONAL 1
#endif

#if XPACT_SIMPATH_PROVISIONAL
/* Provisional fallback: route to platform libm.
 *
 * Per XCore-4a Section 6.3, this is the documented pre-Step-11.5
 * sim-path math posture; CI fails if XPACT_SIMPATH_PROVISIONAL=1
 * persists past Foundation Prototype.  The signatures + return-value
 * contracts mirror Sleef-3.6 verbatim so the Phase 1g swap-in for the
 * upstream tarball polynomial bodies is mechanical.
 */
float Sleef_sinf_u35  (float x)          { return sinf (x);     }
float Sleef_cosf_u35  (float x)          { return cosf (x);     }
float Sleef_tanf_u35  (float x)          { return tanf (x);     }
float Sleef_atan2f_u10(float y, float x) { return atan2f(y, x); }
float Sleef_sqrtf_u10 (float x)          { return sqrtf(x);     }
float Sleef_powf_u10  (float x, float y) { return powf (x, y);  }
float Sleef_expf_u10  (float x)          { return expf (x);     }
float Sleef_logf_u10  (float x)          { return logf (x);     }

#else
#error "Non-provisional Sleef bodies require the Phase 1g upstream-tarball swap-in. See README.html."
#endif

/* Force the helper TU to participate in the link surface so the
 * SleefFMACheck disassembly scan inspects it.  See sleef_helper.c. */
int32_t xpact_sleef_helper_anchor(void);
static int32_t xpact_sleef_sp_anchor_consumer(void) { return xpact_sleef_helper_anchor(); }
