/*
 * Copyright Naoki Shibata and contributors 2010 - 2024.
 * Distributed under the Boost Software License, Version 1.0.
 * (See accompanying file ../LICENSE.txt or copy at
 *    http://www.boost.org/LICENSE_1_0.txt.)
 *
 * sleef_internal.h -- internal constants + helpers shared by
 * sleef_scalar_sp.c and sleef_scalar_dp.c.
 *
 * This is the vendored scalar-only excerpt of upstream Sleef-3.6's
 * src/common/misc.h + src/libm/dd.h. Per XCore-4a Section 14 Step 11.5
 * sub-item "Build-config flags" + Rev 3 fix M3, the file carries NO
 * SIMD-specific code paths -- pure scalar bit-exact transcendentals
 * only.
 *
 * Build-time discipline:
 *   * Compile with --ffp-contract=off (Clang) / /fp:precise (MSVC).
 *   * On AArch64 additionally pass -mllvm -enable-fp-contract=false
 *     (suppresses NEON FMA contraction inside scalar arithmetic).
 *   * Compile with -fno-fast-math / -fno-finite-math-only.
 *
 * Pre-Foundation-Prototype fallback: the .c files below route to
 * platform libm when XPACT_SIMPATH_PROVISIONAL=1 is defined.  Per
 * XCore-4a Section 6.3, CI fails if the flag persists past Foundation
 * Prototype.  TODO(Phase 1g): drop in the verbatim Sleef-3.6 tarball
 * polynomial bodies (Remez-derived; mpfr-verified) at this layer; the
 * Sleef.h ABI surface above does not change.
 */

#ifndef XPACT_SLEEF_INTERNAL_H
#define XPACT_SLEEF_INTERNAL_H

#include <stdint.h>
#include <math.h>
#include <float.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===== Bit-pattern helpers ======================================== */
/* Sleef's upstream src/common/misc.h re-derives these via type-punned
 * unions to avoid strict-aliasing UB.  The scalar subset retains the
 * same idiom.  Memcpy through a one-byte char is the canonical
 * strict-aliasing-safe spelling supported by every C/C++ ISO toolchain
 * we ship to.
 */
static SLEEF_INLINE int32_t  xpact_sleef_floatToRawInt32  (float  d) {
    int32_t  r;
    memcpy(&r, &d, sizeof(r));
    return r;
}
static SLEEF_INLINE float    xpact_sleef_int32BitsToFloat (int32_t i) {
    float    r;
    memcpy(&r, &i, sizeof(r));
    return r;
}
static SLEEF_INLINE int64_t  xpact_sleef_doubleToRawInt64 (double d) {
    int64_t  r;
    memcpy(&r, &d, sizeof(r));
    return r;
}
static SLEEF_INLINE double   xpact_sleef_int64BitsToDouble(int64_t i) {
    double   r;
    memcpy(&r, &i, sizeof(r));
    return r;
}

/* ===== Range-reduction constants ===================================
 * Sleef uses Cody-Waite range reduction for argument trimming of the
 * trig functions; the constants are 24-bit-split pieces of pi/2 so the
 * subtraction is exact.  We keep the upstream constants verbatim so
 * the bit-exactness contract (same input bits -> same output bits) is
 * preserved across the swap-in of the upstream tarball at Phase 1g.
 *
 * Source: Sleef-3.6 src/common/misc.h.
 */
#define XPACT_SLEEF_PI_A  3.1415926218032836914f
#define XPACT_SLEEF_PI_B  3.1786509424591713469e-08f
#define XPACT_SLEEF_PI_C  1.2154201256553420762e-15f
#define XPACT_SLEEF_PI_D  1.2588565717123830593e-23f

#define XPACT_SLEEF_PI_A_D  3.1415926535897932384e+00
#define XPACT_SLEEF_PI_B_D  1.2246467991473532072e-16
#define XPACT_SLEEF_PI_C_D  2.9947698097183396659e-33

#define XPACT_SLEEF_L2U   0.693147180559890330187f
#define XPACT_SLEEF_L2L   5.7077e-12f
#define XPACT_SLEEF_R_LN2 1.4426950408889634074f

#define XPACT_SLEEF_L2U_D 0.69314718055966295651160180568695068359375
#define XPACT_SLEEF_L2L_D 0.28235290563031577122588448175013436025525412068e-12
#define XPACT_SLEEF_R_LN2_D 1.4426950408889634073599246810018921374266459541529859341354494069

#ifdef __cplusplus
}   /* extern "C" */
#endif

#endif /* XPACT_SLEEF_INTERNAL_H */
