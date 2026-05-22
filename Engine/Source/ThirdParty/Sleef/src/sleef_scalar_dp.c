/*
 * Copyright Naoki Shibata and contributors 2010 - 2024.
 * Distributed under the Boost Software License, Version 1.0.
 * (See accompanying file ../LICENSE.txt or copy at
 *    http://www.boost.org/LICENSE_1_0.txt.)
 *
 * sleef_scalar_dp.c -- vendored scalar double-precision bodies.
 * Mirror of sleef_scalar_sp.c for the double-precision variants.
 *
 * Per upstream Sleef-3.6 src/libm/sleefdp.c, the double-precision
 * implementations use the same Cody-Waite + Payne-Hanek range
 * reduction + Estrin polynomial evaluation strategy, with higher-order
 * coefficient tables tuned for the 53-bit mantissa.
 *
 * TODO(Phase 1g): drop in upstream Sleef-3.6 src/libm/sleefdp.c bodies.
 * Public ABI is fixed by Sleef.h above.
 */

#include "sleef_internal.h"
#include "Sleef.h"

#ifndef XPACT_SIMPATH_PROVISIONAL
/* Phase 1g (Subagent X fix MIN-1): default is now 0. See sleef_scalar_sp.c
 * for full rationale. The provisional libm-fallback path was a Phase 1e
 * transition state; Phase 1g elevates Sleef to its actual scalar
 * implementation (Subagent Y owns the upstream-tarball swap-in). */
#define XPACT_SIMPATH_PROVISIONAL 0
#endif

_Static_assert(XPACT_SIMPATH_PROVISIONAL == 0,
               "XPACT_SIMPATH_PROVISIONAL must be 0 in Phase 1g+; "
               "libm-fallback was a Phase 1e transition state. See "
               "XCore-4a Section 6.3 Phase 1g errata.");

#if XPACT_SIMPATH_PROVISIONAL
double Sleef_sin_u35  (double x)            { return sin (x);     }
double Sleef_cos_u35  (double x)            { return cos (x);     }
double Sleef_tan_u35  (double x)            { return tan (x);     }
double Sleef_atan2_u10(double y, double x)  { return atan2(y, x); }
double Sleef_sqrt_u10 (double x)            { return sqrt(x);     }
double Sleef_pow_u10  (double x, double y)  { return pow (x, y);  }
double Sleef_exp_u10  (double x)            { return exp (x);     }
double Sleef_log_u10  (double x)            { return log (x);     }
#else
#error "Non-provisional Sleef bodies require the Phase 1g upstream-tarball swap-in. See README.html."
#endif

int32_t xpact_sleef_helper_anchor(void);
static int32_t xpact_sleef_dp_anchor_consumer(void) { return xpact_sleef_helper_anchor(); }
