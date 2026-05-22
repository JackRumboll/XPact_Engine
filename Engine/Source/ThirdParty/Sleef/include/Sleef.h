/*
 * Copyright Naoki Shibata and contributors 2010 - 2024.
 *
 * Distributed under the Boost Software License, Version 1.0.
 * (See accompanying file LICENSE.txt or copy at
 *    http://www.boost.org/LICENSE_1_0.txt.)
 *
 * Extracted scalar-only subset of upstream Sleef-3.6 include/sleef.h.
 * The original header carries SIMD typedefs (Sleef__m128d, Sleef_svfloat32_t,
 * etc.) and SIMD entry points for SSE2/SSE4/AVX2/AVX512F/NEON/SVE; ALL such
 * declarations are removed in this excerpt because XPact's sim-path
 * determinism contract (XCore-4a Rev 3 Section 6.3 + Toolchain Contract
 * Rev 13.7 Section 4) forbids them.
 *
 * Retained surface: scalar functions Sleef_sinf_u35 etc. The ULP-tier
 * suffix is part of the function name; u10 = max 10 ULP error, u35 =
 * max 35 ULP error. The selection rationale is documented in
 * /Engine/Source/ThirdParty/Sleef/README.html.
 */

#ifndef SLEEF_H
#define SLEEF_H

#if !defined(SLEEF_GENHEADER)
#include <stddef.h>
#include <stdint.h>
#endif

/* Sleef's upstream header sets SLEEF_CONST etc. attributes. We retain
 * the macros for ABI compatibility with a future drop-in of the upstream
 * tarball; in the vendored scalar subset they collapse to empty. */
#if !defined(SLEEF_ALWAYS_INLINE)
#define SLEEF_ALWAYS_INLINE
#endif
#if !defined(SLEEF_INLINE)
#define SLEEF_INLINE
#endif
#if !defined(SLEEF_CONST)
#if defined(__GNUC__) || defined(__clang__)
#define SLEEF_CONST __attribute__((const))
#else
#define SLEEF_CONST
#endif
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* ===================================================================
 * Single-precision scalar entries.
 * The 'u<N>' suffix is the worst-case ULP error bound.
 * Names + signatures track upstream Sleef-3.6 exactly so the upstream
 * tarball can be dropped in by Phase 1g without changing consumers.
 * =================================================================== */
SLEEF_CONST float Sleef_sinf_u35  (float x);
SLEEF_CONST float Sleef_cosf_u35  (float x);
SLEEF_CONST float Sleef_tanf_u35  (float x);
SLEEF_CONST float Sleef_atan2f_u10(float y, float x);
SLEEF_CONST float Sleef_sqrtf_u10 (float x);
SLEEF_CONST float Sleef_powf_u10  (float x, float y);
SLEEF_CONST float Sleef_expf_u10  (float x);
SLEEF_CONST float Sleef_logf_u10  (float x);

/* ===================================================================
 * Double-precision scalar entries.
 * =================================================================== */
SLEEF_CONST double Sleef_sin_u35  (double x);
SLEEF_CONST double Sleef_cos_u35  (double x);
SLEEF_CONST double Sleef_tan_u35  (double x);
SLEEF_CONST double Sleef_atan2_u10(double y, double x);
SLEEF_CONST double Sleef_sqrt_u10 (double x);
SLEEF_CONST double Sleef_pow_u10  (double x, double y);
SLEEF_CONST double Sleef_exp_u10  (double x);
SLEEF_CONST double Sleef_log_u10  (double x);

#ifdef __cplusplus
}   /* extern "C" */
#endif

#endif /* SLEEF_H */
