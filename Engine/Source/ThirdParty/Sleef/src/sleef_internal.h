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
 * src/common/misc.h + src/libm/sleefsp.c + src/libm/sleefdp.c helpers.
 * Per XCore-4a Section 14 Step 11.5 sub-item "Build-config flags" + Rev 3
 * fix M3, the file carries NO SIMD-specific code paths -- pure scalar
 * bit-exact transcendentals only.
 *
 * Build-time discipline:
 *   * Compile with --ffp-contract=off (Clang) / /fp:precise (MSVC).
 *   * On AArch64 additionally pass -mllvm -enable-fp-contract=false
 *     (suppresses NEON FMA contraction inside scalar arithmetic).
 *   * Compile with -fno-fast-math / -fno-finite-math-only -mno-fma.
 *
 * Phase 1g: PROVISIONAL=0 is enforced by static_assert in the .c files;
 * the .c files implement actual Sleef-3.6 polynomial bodies (Remez-derived;
 * mpfr-verified) for the 8 single-precision + 8 double-precision API
 * entries declared in Sleef.h.  The polynomial bodies, helper math, and
 * Cody-Waite / Payne-Hanek range reduction are copied verbatim from
 * upstream Sleef-3.6 src/libm/sleefsp.c and src/libm/sleefdp.c per the
 * BSL-1.0 license terms (LICENSE.txt in this directory).
 *
 * Naming convention: the vendored Sleef body code uses the UPSTREAM
 * symbol names (floatToRawIntBits, fabsfk, mlaf, PI_A2f, ...) verbatim
 * for ABI parity with future upstream-tarball drop-ins.  These names are
 * file-scope statics on the .c side (visible only within the Sleef
 * TU); they do not leak as external symbols.  The PUBLIC ABI exposed by
 * Sleef.h is the Sleef_*_u35 / Sleef_*_u10 namespace and is unchanged.
 */

#ifndef XPACT_SLEEF_INTERNAL_H
#define XPACT_SLEEF_INTERNAL_H

/* Sleef.h provides the public ABI declarations.  We include it FIRST so
 * SLEEF_INLINE / SLEEF_CONST are defined before any consumer of the
 * internal helpers uses them (the static-inline helpers below carry
 * SLEEF_INLINE so the macro must be visible). */
#include "Sleef.h"

#include <stdint.h>
#include <string.h>   /* memcpy for bit-pattern punning */
#include <math.h>     /* sqrtf / sqrt -- used by xsqrtf_u05 / xsqrt_u05 inverse-Newton refinement */
#include <float.h>    /* FLT_MIN / DBL_MIN */
#include <limits.h>   /* INT_MAX */

/* ==================================================================
 * Upstream Sleef misc.h -- constants used by the SP + DP bodies.
 * Reproduced verbatim from Sleef-3.6 src/common/misc.h per BSL-1.0.
 * ================================================================== */

#ifndef M_PI
#define M_PI 3.141592653589793238462643383279502884
#endif

#ifndef M_1_PI
#define M_1_PI 0.318309886183790671537767526745028724
#endif

#ifndef M_2_PI
#define M_2_PI 0.636619772367581343075535053490057448
#endif

#ifndef SLEEF_FP_ILOGB0
#define SLEEF_FP_ILOGB0 ((int)0x80000000)
#endif

#ifndef SLEEF_FP_ILOGBNAN
#define SLEEF_FP_ILOGBNAN ((int)2147483647)
#endif

/* PI_A..PI_D are Cody-Waite range reduction constants for double.
 * PI_A, PI_B, PI_C have their last 28 bits zero so the subtraction is
 * exact; PI_A+PI_B+PI_C+PI_D approximates pi.
 */
#define PI_A 3.1415926218032836914
#define PI_B 3.1786509424591713469e-08
#define PI_C 1.2246467864107188502e-16
#define PI_D 1.2736634327021899816e-24
#define TRIGRANGEMAX 1e+14

#define PI_A2 3.141592653589793116
#define PI_B2 1.2246467991473532072e-16
#define TRIGRANGEMAX2 15

#define M_2_PI_H 0.63661977236758138243
#define M_2_PI_L -3.9357353350364971764e-17

#define SQRT_DBL_MAX 1.3407807929942596355e+154
#define TRIGRANGEMAX3 1e+9
#define M_4_PI 1.273239544735162542821171882678754627704620361328125

#define L2U .69314718055966295651160180568695068359375
#define L2L .28235290563031577122588448175013436025525412068e-12
#define R_LN2 1.442695040888963407359924681001892137426645954152985934135449406931

#define L10U 0.30102999566383914498
#define L10L 1.4205023227266099418e-13
#define LOG10_2 3.3219280948873623478703194294893901758648313930

#define L10Uf 0.3010253906f
#define L10Lf 4.605038981e-06f

/* Float Cody-Waite trig constants. */
#define PI_Af 3.140625f
#define PI_Bf 0.0009670257568359375f
#define PI_Cf 6.2771141529083251953e-07f
#define PI_Df 1.2154201256553420762e-10f
#define TRIGRANGEMAXf 39000

#define PI_A2f 3.1414794921875f
#define PI_B2f 0.00011315941810607910156f
#define PI_C2f 1.9841872589410058936e-09f
#define TRIGRANGEMAX2f 125.0f

#define TRIGRANGEMAX4f 8e+6f

#define SQRT_FLT_MAX 18446743523953729536.0

#define L2Uf 0.693145751953125f
#define L2Lf 1.428606765330187045e-06f
#define R_LN2f 1.442695040888963407359924681001892137426645954152985934135449406931f

#ifndef M_PIf
# define M_PIf ((float)M_PI)
#endif

/* ==================================================================
 * Compatibility macros so the verbatim upstream bodies compile.
 * Upstream uses INLINE / CONST / EXPORT / NOEXPORT attributes via
 * compiler-specific qualifiers.  XPact builds with deterministic flags
 * and no DLL export concerns within Sleef.lib, so all four collapse:
 *   - INLINE: SLEEF_INLINE from Sleef.h (force-inline where supported)
 *   - CONST:  SLEEF_CONST from Sleef.h (__attribute__((const)) on GCC/Clang)
 *   - EXPORT: empty (the Sleef_*_u10 / _u35 entry points use no decoration
 *             so they participate in the static library normally)
 *   - NOEXPORT: empty (the only NOEXPORT symbol is the rempitab tables,
 *             whose visibility is implicitly static-private within
 *             Sleef.lib via the static-library link model)
 * ================================================================== */
#ifndef INLINE
#define INLINE SLEEF_INLINE
#endif
#ifndef CONST
#define CONST SLEEF_CONST
#endif
#ifndef EXPORT
#define EXPORT
#endif
#ifndef NOEXPORT
#define NOEXPORT
#endif
#ifndef ALIGNED
#define ALIGNED(x)
#endif
#ifndef LIKELY
#define LIKELY(condition) (condition)
#endif
#ifndef UNLIKELY
#define UNLIKELY(condition) (condition)
#endif
#ifndef RESTRICT
#if defined(__GNUC__) || defined(__clang__)
#define RESTRICT __restrict__
#else
#define RESTRICT
#endif
#endif

/* SLEEF_NAN / SLEEF_INFINITY -- compile-time constants reflecting the
 * upstream misc.h emission for each compiler family.  Distinguish
 * MSVC vs Clang/GCC because __builtin_nan is not available on MSVC.
 */
#if defined(_MSC_VER) && !defined(__clang__)
#define SLEEF_INFINITY (1e+300 * 1e+300)
#define SLEEF_NAN (SLEEF_INFINITY - SLEEF_INFINITY)
#define SLEEF_INFINITYf ((float)SLEEF_INFINITY)
#define SLEEF_NANf ((float)SLEEF_NAN)
#else
#define SLEEF_NAN __builtin_nan("")
#define SLEEF_NANf __builtin_nanf("")
#define SLEEF_INFINITY __builtin_inf()
#define SLEEF_INFINITYf __builtin_inff()
#endif

/* ==================================================================
 * Sleef_float2 / Sleef_double2 -- double-double precision pairs used
 * by the dd/df arithmetic helpers.  These are file-scope types in the
 * upstream Sleef sources; we duplicate the typedefs here so both the
 * SP and DP TUs see the same layout.
 * ================================================================== */
#if !defined(Sleef_double2_DEFINED)
#define Sleef_double2_DEFINED
typedef struct {
  double x, y;
} Sleef_double2;
#endif

#if !defined(Sleef_float2_DEFINED)
#define Sleef_float2_DEFINED
typedef struct {
  float x, y;
} Sleef_float2;
#endif

/* ==================================================================
 * Sleef_rempitabsp / Sleef_rempitabdp -- Payne-Hanek argument
 * reduction tables, defined in sleef_rempitab.c.  Used by rempif /
 * rempi for trig arguments > TRIGRANGEMAX{f}.
 * ================================================================== */
#ifdef __cplusplus
extern "C" {
#endif

extern const float  Sleef_rempitabsp[];
extern const double Sleef_rempitabdp[];

#ifdef __cplusplus
}   /* extern "C" */
#endif

/* ==================================================================
 * Backward-compat XPact-namespaced bit-pattern helpers.  Kept so any
 * external Sleef-adjacent code that took a dependency on these names
 * in earlier phases continues to compile.  The internal Sleef bodies
 * use the upstream names (floatToRawIntBits etc.) defined inline in
 * each .c TU.
 * ================================================================== */
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

/* ==================================================================
 * Pre-existing XPact-namespaced range-reduction constants.  These
 * mirror the upstream PI_A/PI_B/PI_C/PI_D, L2U/L2L/R_LN2, and float
 * counterparts and are retained for source-level compatibility with
 * any code that took an inline dependency on the names.  The
 * Sleef-internal code uses the UPSTREAM names (defined above).
 * ================================================================== */
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

#endif /* XPACT_SLEEF_INTERNAL_H */
