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
 * /Engine/Source/ThirdParty/Sleef/include/Sleef.h.  The bodies are
 * copied VERBATIM from Sleef-3.6 src/libm/sleefsp.c per the BSL-1.0
 * license terms; the only modifications are:
 *
 *   (1) The internal kernels (xsinf, xcosf, ...) are wrapped by
 *       XPact's public ABI symbols (Sleef_sinf_u35 etc.) at the bottom
 *       of the file.  The internal-kernel names are preserved so that
 *       a future drop-in of the upstream tarball (Phase 2-Sleef-tarball-
 *       refresh) does not require editing the polynomial code.
 *
 *   (2) The upstream `#include "misc.h" / "rename.h" / "estrin.h"' are
 *       collapsed into this file's `#include "sleef_internal.h"` which
 *       carries the constants verbatim from misc.h, and the file
 *       reproduces the estrin.h POLY* macros inline below.
 *
 *   (3) Functions outside the 8-entry XPact public ABI (xsincosf,
 *       xsinhf, xcbrtf, etc.) are STRIPPED to keep the TU at a
 *       reasonable size and to make the symbol-table dump CI gate
 *       (XCore-4a Section 17.3 C-extra) easier to inspect.
 *
 * The polynomial coefficients are Remez-derived and mpfr-verified
 * upstream; we do not re-derive them.  Cross-arch bit-exactness
 * follows from the deterministic build flag set (--ffp-contract=off
 * etc., enforced by sim_path = true in Sleef.Build.toml) plus the
 * verbatim coefficient + constants vendoring.
 *
 * Build configuration (mandatory; enforced by Sleef.Build.toml's
 * sim_path = true):
 *   * Clang:  -ffp-contract=off -fno-fast-math -fno-finite-math-only -mno-fma
 *   * MSVC:   /fp:precise
 *   * AArch64 add: -mllvm -enable-fp-contract=false
 *
 * Verification (XCore-4a Section 17.3):
 *   * SleefFMACheck disassembly scan -- no fmla/vfma instructions
 *     remain in the linked object.
 *   * SleefSymbolVariants test -- every Sleef_*_u10 / _u35 entry has
 *     a distinct linked body.
 *   * Cross-arch bit-exactness CI gate -- 1000-step Verlet trajectory
 *     produces byte-identical state on Win64 + Linux + Quest 3 ARM64.
 */

#include "sleef_internal.h"

/* PROVISIONAL=0 enforced by compile-time check -- the .c implementations
 * below are real polynomial bodies, not libm fallback.
 *
 * Use a negative-sized typedef array as the static assertion mechanism;
 * it compiles on every C89+ toolchain without needing the C11
 * <assert.h> static_assert macro or the C23 _Static_assert keyword
 * (MSVC default mode is C89 in non-/std:c11 builds, and the XBT TOML's
 * sim_path module setting does not currently force /std:c11 onto Sleef). */
#ifndef XPACT_SIMPATH_PROVISIONAL
#define XPACT_SIMPATH_PROVISIONAL 0
#endif

typedef char xpact_sleef_sp_provisional_must_be_zero_in_phase_1g[
    (XPACT_SIMPATH_PROVISIONAL == 0) ? 1 : -1];

/* MSVC pragma for fp-contract OFF.  Upstream sleefsp.c emits this for
 * the same reason -- we mirror it verbatim. */
#if defined(_MSC_VER) && !defined (__clang__)
#pragma fp_contract (off)
#endif
#pragma STDC FP_CONTRACT OFF

/* SQRTF is __builtin_sqrtf on Clang/GCC where available; sqrtf
 * otherwise.  Both paths emit a single scalar SQRT instruction on the
 * targets we ship to, no FMA. */
#if defined(__clang__) || (defined(__GNUC__) && !defined(_MSC_VER))
#define SQRTF __builtin_sqrtf
#else
#define SQRTF sqrtf
#endif

/* ================================================================
 * Estrin's-method polynomial-evaluation macros.  Verbatim upstream
 * Sleef-3.6 src/libm/estrin.h, restricted to the orders the SP
 * kernels actually use (POLY6 + POLY8).
 * ================================================================ */
#define MLA mlaf
#define C2V(x) (x)

#define POLY2(x, c1, c0) MLA(x, C2V(c1), C2V(c0))
#define POLY3(x, x2, c2, c1, c0) MLA(x2, C2V(c2), MLA(x, C2V(c1), C2V(c0)))
#define POLY4(x, x2, c3, c2, c1, c0) MLA(x2, MLA(x, C2V(c3), C2V(c2)), MLA(x, C2V(c1), C2V(c0)))
#define POLY5(x, x2, x4, c4, c3, c2, c1, c0) MLA(x4, C2V(c4), POLY4(x, x2, c3, c2, c1, c0))
#define POLY6(x, x2, x4, c5, c4, c3, c2, c1, c0) MLA(x4, POLY2(x, c5, c4), POLY4(x, x2, c3, c2, c1, c0))
#define POLY7(x, x2, x4, c6, c5, c4, c3, c2, c1, c0) MLA(x4, POLY3(x, x2, c6, c5, c4), POLY4(x, x2, c3, c2, c1, c0))
#define POLY8(x, x2, x4, c7, c6, c5, c4, c3, c2, c1, c0) MLA(x4, POLY4(x, x2, c7, c6, c5, c4), POLY4(x, x2, c3, c2, c1, c0))

/* ================================================================
 * Bit-pattern helpers (upstream sleefsp.c lines 39-49).
 * ================================================================ */
static INLINE CONST int32_t floatToRawIntBits(float d) {
  int32_t ret;
  memcpy(&ret, &d, sizeof(ret));
  return ret;
}

static INLINE CONST float intBitsToFloat(int32_t i) {
  float ret;
  memcpy(&ret, &i, sizeof(ret));
  return ret;
}

/* ================================================================
 * Sign / abs / mla / rint / FP-classify helpers (upstream lines 51-77).
 * ================================================================ */
static INLINE CONST float fabsfk(float x) {
  return intBitsToFloat(0x7fffffffL & floatToRawIntBits(x));
}

static INLINE CONST float mulsignf(float x, float y) {
  return intBitsToFloat(floatToRawIntBits(x) ^ (floatToRawIntBits(y) & (1 << 31)));
}

static INLINE CONST float copysignfk(float x, float y) {
  return intBitsToFloat((floatToRawIntBits(x) & ~(1 << 31)) ^ (floatToRawIntBits(y) & (1 << 31)));
}

static INLINE CONST float signf(float d) { return mulsignf(1, d); }
static INLINE CONST float mlaf(float x, float y, float z) { return x * y + z; }
static INLINE CONST float rintfk(float x) { return x < 0 ? (int)(x - 0.5f) : (int)(x + 0.5f); }
static INLINE CONST int ceilfk(float x) { return (int)x + (x < 0 ? 0 : 1); }
static INLINE CONST float fminfk(float x, float y) { return x < y ? x : y; }
static INLINE CONST float fmaxfk(float x, float y) { return x > y ? x : y; }
static INLINE CONST int xisintf(float x) { return (x == (int)x); }

static INLINE CONST int xsignbitf(double d) { return (floatToRawIntBits((float)d) & floatToRawIntBits(-0.0f)) == floatToRawIntBits(-0.0f); }
static INLINE CONST int xisnanf(float x) { return x != x; }
static INLINE CONST int xisinff(float x) { return x == SLEEF_INFINITYf || x == -SLEEF_INFINITYf; }
static INLINE CONST int xisnegzerof(float x) { return floatToRawIntBits(x) == floatToRawIntBits(-0.0f); }

/* ================================================================
 * Exponent extraction + ldexp helpers (upstream lines 79-126).
 * ================================================================ */
static INLINE CONST int ilogbkf(float d) {
  int m = d < 5.421010862427522E-20f;
  d = m ? 1.8446744073709552E19f * d : d;
  int q = (floatToRawIntBits(d) >> 23) & 0xff;
  q = m ? q - (64 + 0x7f) : q - 0x7f;
  return q;
}

static INLINE CONST int ilogb2kf(float d) {
  return ((floatToRawIntBits(d) >> 23) & 0xff) - 0x7f;
}

static INLINE CONST float pow2if(int q) {
  return intBitsToFloat(((int32_t)(q + 0x7f)) << 23);
}

static INLINE CONST float ldexpkf(float x, int q) {
  float u;
  int m;
  m = q >> 31;
  m = (((m + q) >> 6) - m) << 4;
  q = q - (m << 2);
  m += 127;
  m = m <   0 ?   0 : m;
  m = m > 255 ? 255 : m;
  u = intBitsToFloat(((int32_t)m) << 23);
  x = x * u * u * u * u;
  u = intBitsToFloat(((int32_t)(q + 0x7f)) << 23);
  return x * u;
}

static INLINE CONST float ldexp2kf(float d, int e) { /* faster than ldexpkf, short reach */
  return d * pow2if(e >> 1) * pow2if(e - (e >> 1));
}

static INLINE CONST float ldexp3kf(float d, int e) { /* very fast, no denormal */
  return intBitsToFloat(floatToRawIntBits(d) + (e << 23));
}

/* ================================================================
 * Double-float (df) arithmetic helpers (upstream lines 137-412).
 * Sleef_float2 is defined in sleef_internal.h.
 * ================================================================ */
static INLINE CONST float upperf(float d) {
  return intBitsToFloat(floatToRawIntBits(d) & 0xfffff000);
}

static INLINE CONST Sleef_float2 df(float h, float l) {
  Sleef_float2 ret;
  ret.x = h; ret.y = l;
  return ret;
}

static INLINE CONST Sleef_float2 dfnormalize_f2_f2(Sleef_float2 t) {
  Sleef_float2 s;
  s.x = t.x + t.y;
  s.y = t.x - s.x + t.y;
  return s;
}

static INLINE CONST Sleef_float2 dfscale_f2_f2_f(Sleef_float2 d, float s) {
  Sleef_float2 r;
  r.x = d.x * s;
  r.y = d.y * s;
  return r;
}

static INLINE CONST Sleef_float2 dfneg_f2_f2(Sleef_float2 d) {
  Sleef_float2 r;
  r.x = -d.x;
  r.y = -d.y;
  return r;
}

static INLINE CONST Sleef_float2 dfadd_f2_f_f(float x, float y) {
  /* |x| >= |y| */
  Sleef_float2 r;
  r.x = x + y;
  r.y = x - r.x + y;
  return r;
}

static INLINE CONST Sleef_float2 dfadd2_f2_f_f(float x, float y) {
  Sleef_float2 r;
  r.x = x + y;
  float v = r.x - x;
  r.y = (x - (r.x - v)) + (y - v);
  return r;
}

static INLINE CONST Sleef_float2 dfadd_f2_f2_f(Sleef_float2 x, float y) {
  /* |x| >= |y| */
  Sleef_float2 r;
  r.x = x.x + y;
  r.y = x.x - r.x + y + x.y;
  return r;
}

static INLINE CONST Sleef_float2 dfadd_f2_f_f2(float x, Sleef_float2 y) {
  /* |x| >= |y| */
  Sleef_float2 r;
  r.x = x + y.x;
  r.y = x - r.x + y.x + y.y;
  return r;
}

static INLINE CONST Sleef_float2 dfadd2_f2_f2_f(Sleef_float2 x, float y) {
  Sleef_float2 r;
  r.x  = x.x + y;
  float v = r.x - x.x;
  r.y = (x.x - (r.x - v)) + (y - v);
  r.y += x.y;
  return r;
}

static INLINE CONST Sleef_float2 dfadd2_f2_f_f2(float x, Sleef_float2 y) {
  Sleef_float2 r;
  r.x  = x + y.x;
  float v = r.x - x;
  r.y = (x - (r.x - v)) + (y.x - v) + y.y;
  return r;
}

static INLINE CONST Sleef_float2 dfadd_f2_f2_f2(Sleef_float2 x, Sleef_float2 y) {
  /* |x| >= |y| */
  Sleef_float2 r;
  r.x = x.x + y.x;
  r.y = x.x - r.x + y.x + x.y + y.y;
  return r;
}

static INLINE CONST Sleef_float2 dfadd2_f2_f2_f2(Sleef_float2 x, Sleef_float2 y) {
  Sleef_float2 r;
  r.x  = x.x + y.x;
  float v = r.x - x.x;
  r.y = (x.x - (r.x - v)) + (y.x - v);
  r.y += x.y + y.y;
  return r;
}

static INLINE CONST Sleef_float2 dfsub_f2_f2_f2(Sleef_float2 x, Sleef_float2 y) {
  /* |x| >= |y| */
  Sleef_float2 r;
  r.x = x.x - y.x;
  r.y = x.x - r.x - y.x + x.y - y.y;
  return r;
}

static INLINE CONST Sleef_float2 dfdiv_f2_f2_f2(Sleef_float2 n, Sleef_float2 d) {
  float t = 1.0f / d.x;
  float dh  = upperf(d.x), dl  = d.x - dh;
  float th  = upperf(t  ), tl  = t   - th;
  float nhh = upperf(n.x), nhl = n.x - nhh;

  Sleef_float2 q;
  q.x = n.x * t;
  float u = -q.x + nhh * th + nhh * tl + nhl * th + nhl * tl +
    q.x * (1 - dh * th - dh * tl - dl * th - dl * tl);
  q.y = t * (n.y - q.x * d.y) + u;
  return q;
}

static INLINE CONST Sleef_float2 dfmul_f2_f_f(float x, float y) {
  float xh = upperf(x), xl = x - xh;
  float yh = upperf(y), yl = y - yh;
  Sleef_float2 r;
  r.x = x * y;
  r.y = xh * yh - r.x + xl * yh + xh * yl + xl * yl;
  return r;
}

static INLINE CONST Sleef_float2 dfmul_f2_f2_f(Sleef_float2 x, float y) {
  float xh = upperf(x.x), xl = x.x - xh;
  float yh = upperf(y  ), yl = y   - yh;
  Sleef_float2 r;
  r.x = x.x * y;
  r.y = xh * yh - r.x + xl * yh + xh * yl + xl * yl + x.y * y;
  return r;
}

static INLINE CONST Sleef_float2 dfmul_f2_f2_f2(Sleef_float2 x, Sleef_float2 y) {
  float xh = upperf(x.x), xl = x.x - xh;
  float yh = upperf(y.x), yl = y.x - yh;
  Sleef_float2 r;
  r.x = x.x * y.x;
  r.y = xh * yh - r.x + xl * yh + xh * yl + xl * yl + x.x * y.y + x.y * y.x;
  return r;
}

static INLINE CONST float dfmul_f_f2_f2(Sleef_float2 x, Sleef_float2 y) {
  float xh = upperf(x.x), xl = x.x - xh;
  float yh = upperf(y.x), yl = y.x - yh;
  return x.y * yh + xh * y.y + xl * yl + xh * yl + xl * yh + xh * yh;
}

static INLINE CONST Sleef_float2 dfsqu_f2_f2(Sleef_float2 x) {
  float xh = upperf(x.x), xl = x.x - xh;
  Sleef_float2 r;
  r.x = x.x * x.x;
  r.y = xh * xh - r.x + (xh + xh) * xl + xl * xl + x.x * (x.y + x.y);
  return r;
}

static INLINE CONST Sleef_float2 dfrec_f2_f(float d) {
  float t = 1.0f / d;
  float dh = upperf(d), dl = d - dh;
  float th = upperf(t), tl = t - th;
  Sleef_float2 q;
  q.x = t;
  q.y = t * (1 - dh * th - dh * tl - dl * th - dl * tl);
  return q;
}

/* ================================================================
 * Payne-Hanek style range reduction (upstream lines 416-462).
 * ================================================================ */
typedef struct {
  float d;
  int32_t i;
} fi_t;

typedef struct {
  Sleef_float2 df;
  int32_t i;
} dfi_t;

static CONST fi_t rempisubf(float x) {
  fi_t ret;
  float fr = x - (float)(INT64_C(1) << 10) * (int32_t)(x * (1.0f / (INT64_C(1) << 10)));
  ret.i = ((7 & ((x > 0 ? 4 : 3) + (int32_t)(fr * 8))) - 3) >> 1;
  fr = fr - 0.25f * (int32_t)(fr * 4 + mulsignf(0.5f, x));
  fr = fabsfk(fr) > 0.125f ? (fr - mulsignf(0.5f, x)) : fr;
  fr = fabsfk(fr) > 1e+10f ? 0 : fr;
  if (fabsfk(x) == 0.12499999254941940308f) { fr = x; ret.i = 0; }
  ret.d = fr;
  return ret;
}

static CONST dfi_t rempif(float a) {
  Sleef_float2 x, y;
  fi_t di;
  int ex = ilogb2kf(a) - 25, q = ex > (90 - 25) ? -64 : 0;
  a = ldexp3kf(a, q);
  if (ex < 0) ex = 0;
  ex *= 4;
  x = dfmul_f2_f_f(a, Sleef_rempitabsp[ex]);
  di = rempisubf(x.x);
  q = di.i;
  x.x = di.d;
  x = dfnormalize_f2_f2(x);
  y = dfmul_f2_f_f(a, Sleef_rempitabsp[ex+1]);
  x = dfadd2_f2_f2_f2(x, y);
  di = rempisubf(x.x);
  q += di.i;
  x.x = di.d;
  x = dfnormalize_f2_f2(x);
  y = dfmul_f2_f2_f(df(Sleef_rempitabsp[ex+2], Sleef_rempitabsp[ex+3]), a);
  x = dfadd2_f2_f2_f2(x, y);
  x = dfnormalize_f2_f2(x);
  x = dfmul_f2_f2_f2(x, df(3.1415927410125732422f*2, -8.7422776573475857731e-08f*2));
  dfi_t ret;
  ret.df = fabsfk(a) < 0.7f ? df(a, 0) : x;
  ret.i = q;
  return ret;
}

/* ================================================================
 * xsinf -- u35 sine (upstream lines 464-504).  Wrapped at bottom of
 * file as Sleef_sinf_u35.
 * ================================================================ */
static CONST float xsinf(float d) {
  int q;
  float u, s, t = d;

  if (fabsfk(d) < TRIGRANGEMAX2f) {
    q = (int)rintfk(d * (float)M_1_PI);
    d = mlaf(q, -PI_A2f, d);
    d = mlaf(q, -PI_B2f, d);
    d = mlaf(q, -PI_C2f, d);
  } else if (fabsfk(d) < TRIGRANGEMAXf) {
    q = (int)rintfk(d * (float)M_1_PI);
    d = mlaf(q, -PI_Af, d);
    d = mlaf(q, -PI_Bf, d);
    d = mlaf(q, -PI_Cf, d);
    d = mlaf(q, -PI_Df, d);
  } else {
    dfi_t dfi = rempif(t);
    q = ((dfi.i & 3) * 2 + (dfi.df.x > 0) + 1) >> 2;
    if ((dfi.i & 1) != 0) {
      dfi.df = dfadd2_f2_f2_f2(dfi.df, df(mulsignf(3.1415927410125732422f*-0.5f, dfi.df.x),
                                          mulsignf(-8.7422776573475857731e-08f*-0.5f, dfi.df.x)));
    }
    d = dfi.df.x + dfi.df.y;
    if (xisinff(t) || xisnanf(t)) d = SLEEF_NANf;
  }

  s = d * d;

  if ((q & 1) != 0) d = -d;

  u = 2.6083159809786593541503e-06f;
  u = mlaf(u, s, -0.0001981069071916863322258f);
  u = mlaf(u, s, 0.00833307858556509017944336f);
  u = mlaf(u, s, -0.166666597127914428710938f);

  u = mlaf(s, u * d, d);

  if (xisnegzerof(t)) u = -0.0f;

  return u;
}

/* ================================================================
 * xcosf -- u35 cosine (upstream lines 544-582).
 * ================================================================ */
static CONST float xcosf(float d) {
  int q;
  float u, s, t = d;

  if (fabsfk(d) < TRIGRANGEMAX2f) {
    q = 1 + 2*(int)rintfk(d * (float)M_1_PI - 0.5f);
    d = mlaf(q, -PI_A2f*0.5f, d);
    d = mlaf(q, -PI_B2f*0.5f, d);
    d = mlaf(q, -PI_C2f*0.5f, d);
  } else if (fabsfk(d) < TRIGRANGEMAXf) {
    q = 1 + 2*(int)rintfk(d * (float)M_1_PI - 0.5f);
    d = mlaf(q, -PI_Af*0.5f, d);
    d = mlaf(q, -PI_Bf*0.5f, d);
    d = mlaf(q, -PI_Cf*0.5f, d);
    d = mlaf(q, -PI_Df*0.5f, d);
  } else {
    dfi_t dfi = rempif(t);
    q = ((dfi.i & 3) * 2 + (dfi.df.x > 0) + 7) >> 1;
    if ((dfi.i & 1) == 0) {
      dfi.df = dfadd2_f2_f2_f2(dfi.df, df(mulsignf(3.1415927410125732422f*-0.5f, dfi.df.x > 0 ? 1.0f : -1.0f),
                                          mulsignf(-8.7422776573475857731e-08f*-0.5f, dfi.df.x > 0 ? 1.0f : -1.0f)));
    }
    d = dfi.df.x + dfi.df.y;
    if (xisinff(t) || xisnanf(t)) d = SLEEF_NANf;
  }

  s = d * d;

  if ((q & 2) == 0) d = -d;

  u = 2.6083159809786593541503e-06f;
  u = mlaf(u, s, -0.0001981069071916863322258f);
  u = mlaf(u, s, 0.00833307858556509017944336f);
  u = mlaf(u, s, -0.166666597127914428710938f);

  u = mlaf(s, u * d, d);

  return u;
}

/* ================================================================
 * xtanf -- u35 tangent (upstream lines 845-887).
 * ================================================================ */
static CONST float xtanf(float d) {
  int q;
  float u, s, x;

  x = d;

  if (fabsfk(d) < TRIGRANGEMAX2f*0.5f) {
    q = (int)rintfk(d * (float)(2 * M_1_PI));
    x = mlaf(q, -PI_A2f*0.5f, x);
    x = mlaf(q, -PI_B2f*0.5f, x);
    x = mlaf(q, -PI_C2f*0.5f, x);
  } else if (fabsfk(d) < TRIGRANGEMAXf) {
    q = (int)rintfk(d * (float)(2 * M_1_PI));
    x = mlaf(q, -PI_Af*0.5f, x);
    x = mlaf(q, -PI_Bf*0.5f, x);
    x = mlaf(q, -PI_Cf*0.5f, x);
    x = mlaf(q, -PI_Df*0.5f, x);
  } else {
    dfi_t dfi = rempif(d);
    q = dfi.i;
    x = dfi.df.x + dfi.df.y;
    if (xisinff(d) || xisnanf(d)) x = SLEEF_NANf;
  }

  s = x * x;

  if ((q & 1) != 0) x = -x;

  float s2 = s * s, s4 = s2 * s2;
  u = POLY6(s, s2, s4,
            0.00927245803177356719970703f,
            0.00331984995864331722259521f,
            0.0242998078465461730957031f,
            0.0534495301544666290283203f,
            0.133383005857467651367188f,
            0.333331853151321411132812f);

  u = mlaf(s, u * x, x);

  if ((q & 1) != 0) u = 1.0f / u;

  return u;
}

/* ================================================================
 * atan2kf_u1 + xatan2f_u1 -- u10 atan2 (upstream lines 1036-1075).
 * XPact's u10 tier maps to upstream's u1 variant.
 * ================================================================ */
static Sleef_float2 atan2kf_u1(Sleef_float2 y, Sleef_float2 x) {
  float u;
  Sleef_float2 s, t;
  int q = 0;

  if (x.x < 0) { x.x = -x.x; x.y = -x.y; q = -2; }
  if (y.x > x.x) { t = x; x = y; y.x = -t.x; y.y = -t.y; q += 1; }

  s = dfdiv_f2_f2_f2(y, x);
  t = dfsqu_f2_f2(s);
  t = dfnormalize_f2_f2(t);

  u = -0.00176397908944636583328247f;
  u = mlaf(u, t.x, 0.0107900900766253471374512f);
  u = mlaf(u, t.x, -0.0309564601629972457885742f);
  u = mlaf(u, t.x, 0.0577365085482597351074219f);
  u = mlaf(u, t.x, -0.0838950723409652709960938f);
  u = mlaf(u, t.x, 0.109463557600975036621094f);
  u = mlaf(u, t.x, -0.142626821994781494140625f);
  u = mlaf(u, t.x, 0.199983194470405578613281f);

  t = dfmul_f2_f2_f2(t, dfadd_f2_f_f(-0.333332866430282592773438f, u * t.x));
  t = dfmul_f2_f2_f2(s, dfadd_f2_f_f2(1, t));
  t = dfadd2_f2_f2_f2(dfmul_f2_f2_f(df(1.5707963705062866211f, -4.3711388286737928865e-08f), (float)q), t);

  return t;
}

static CONST float xatan2f_u1(float y, float x) {
  if (fabsfk(x) < 2.9387372783541830947e-39f) { y *= (UINT64_C(1) << 24); x *= (UINT64_C(1) << 24); } /* nexttowardf((1.0 / FLT_MAX), 1) */
  Sleef_float2 d = atan2kf_u1(df(fabsfk(y), 0), df(x, 0));
  float r = d.x + d.y;

  r = mulsignf(r, x);
  if (xisinff(x) || x == 0) r = (float)M_PI/2 - (xisinff(x) ? (signf(x) * (float)(M_PI  /2)) : 0.0f);
  if (xisinff(y)          ) r = (float)M_PI/2 - (xisinff(x) ? (signf(x) * (float)(M_PI*1/4)) : 0.0f);
  if (              y == 0) r = (signf(x) == -1 ? (float)M_PI : 0.0f);

  return xisnanf(x) || xisnanf(y) ? SLEEF_NANf : mulsignf(r, y);
}

/* ================================================================
 * xsqrtf_u05 -- u10 sqrt (upstream lines 2068-2098).  XPact's u10
 * tier maps to upstream's u05 variant -- tighter than the contract
 * requires, the upstream u35 path is just a 4-iter Newton refinement
 * that does not give the inverse-sqrt correction-double-precision
 * cleanup; the u05 path is also faster on x86_64 because it skips
 * one Newton iteration in favor of the dfmul correction.
 * ================================================================ */
static CONST float xsqrtf_u05(float d) {
  float q = 0.5f;

  d = d < 0 ? SLEEF_NANf : d;

  if (d < 5.2939559203393770e-23f) {
    d *= 1.8889465931478580e+22f;
    q = 7.2759576141834260e-12f * 0.5f;
  }

  if (d > 1.8446744073709552e+19f) {
    d *= 5.4210108624275220e-20f;
    q = 4294967296.0f * 0.5f;
  }

  /* http://en.wikipedia.org/wiki/Fast_inverse_square_root */
  float x = intBitsToFloat(0x5f375a86 - (floatToRawIntBits(d + 1e-45f) >> 1));

  x = x * (1.5f - 0.5f * d * x * x);
  x = x * (1.5f - 0.5f * d * x * x);
  x = x * (1.5f - 0.5f * d * x * x) * d;

  Sleef_float2 d2 = dfmul_f2_f2_f2(dfadd2_f2_f_f2(d, dfmul_f2_f_f(x, x)), dfrec_f2_f(x));

  float ret = (d2.x + d2.y) * q;

  ret = d == SLEEF_INFINITYf ? SLEEF_INFINITYf : ret;
  ret = d == 0 ? d : ret;

  return ret;
}

/* ================================================================
 * xlogf_u1 / xexpf -- u10 log / exp (upstream lines 1258-1289 + 1157-1178).
 * ================================================================ */
static CONST float xlogf_u1(float d) {
  Sleef_float2 x, s;
  float m, t, x2;
  int e;

  int o = d < FLT_MIN;
  if (o) d *= (float)(INT64_C(1) << 32) * (float)(INT64_C(1) << 32);

  e = ilogb2kf(d * (1.0f/0.75f));
  m = ldexp3kf(d, -e);

  if (o) e -= 64;

  x = dfdiv_f2_f2_f2(dfadd2_f2_f_f(-1, m), dfadd2_f2_f_f(1, m));
  x2 = x.x * x.x;

  t = +0.3027294874e+0f;
  t = mlaf(t, x2, +0.3996108174e+0f);
  t = mlaf(t, x2, +0.6666694880e+0f);

  s = dfmul_f2_f2_f(df(0.69314718246459960938f, -1.904654323148236017e-09f), (float)e);
  s = dfadd_f2_f2_f2(s, dfscale_f2_f2_f(x, 2));
  s = dfadd_f2_f2_f(s, x2 * x.x * t);

  float r = s.x + s.y;

  if (xisinff(d)) r = SLEEF_INFINITYf;
  if (d < 0 || xisnanf(d)) r = SLEEF_NANf;
  if (d == 0) r = -SLEEF_INFINITYf;

  return r;
}

static CONST float xexpf(float d) {
  int q = (int)rintfk(d * R_LN2f);
  float s, u;

  s = mlaf(q, -L2Uf, d);
  s = mlaf(q, -L2Lf, s);

  u = 0.000198527617612853646278381f;
  u = mlaf(u, s, 0.00139304355252534151077271f);
  u = mlaf(u, s, 0.00833336077630519866943359f);
  u = mlaf(u, s, 0.0416664853692054748535156f);
  u = mlaf(u, s, 0.166666671633720397949219f);
  u = mlaf(u, s, 0.5f);

  u = s * s * u + s + 1.0f;
  u = ldexp2kf(u, q);

  if (d < -104) u = 0;
  if (d >  104) u = SLEEF_INFINITYf;

  return u;
}

/* ================================================================
 * expkf + logkf -- helper math kernels for xpowf (upstream lines
 * 1180-1256).
 * ================================================================ */
static INLINE CONST float expkf(Sleef_float2 d) {
  int q = (int)rintfk((d.x + d.y) * R_LN2f);
  Sleef_float2 s, t;
  float u;

  s = dfadd2_f2_f2_f(d, q * -L2Uf);
  s = dfadd2_f2_f2_f(s, q * -L2Lf);

  s = dfnormalize_f2_f2(s);

  u = 0.00136324646882712841033936f;
  u = mlaf(u, s.x, 0.00836596917361021041870117f);
  u = mlaf(u, s.x, 0.0416710823774337768554688f);
  u = mlaf(u, s.x, 0.166665524244308471679688f);
  u = mlaf(u, s.x, 0.499999850988388061523438f);

  t = dfadd_f2_f2_f2(s, dfmul_f2_f2_f(dfsqu_f2_f2(s), u));

  t = dfadd_f2_f_f2(1, t);

  u = ldexpkf(t.x + t.y, q);

  if (d.x < -104) u = 0;

  return u;
}

static INLINE CONST Sleef_float2 logkf(float d) {
  Sleef_float2 x, x2, s;
  float m, t;
  int e;

  int o = d < FLT_MIN;
  if (o) d *= (float)(INT64_C(1) << 32) * (float)(INT64_C(1) << 32);

  e = ilogb2kf(d * (1.0f/0.75f));
  m = ldexp3kf(d, -e);

  if (o) e -= 64;

  x = dfdiv_f2_f2_f2(dfadd2_f2_f_f(-1, m), dfadd2_f2_f_f(1, m));
  x2 = dfsqu_f2_f2(x);

  t = 0.240320354700088500976562f;
  t = mlaf(t, x2.x, 0.285112679004669189453125f);
  t = mlaf(t, x2.x, 0.400007992982864379882812f);
  Sleef_float2 c = df(0.66666662693023681640625f, 3.69183861259614332084311e-09f);

  s = dfmul_f2_f2_f(df(0.69314718246459960938f, -1.904654323148236017e-09f), (float)e);
  s = dfadd_f2_f2_f2(s, dfscale_f2_f2_f(x, 2));
  s = dfadd_f2_f2_f2(s, dfmul_f2_f2_f2(dfmul_f2_f2_f2(x2, x),
                                       dfadd2_f2_f2_f2(dfmul_f2_f2_f(x2, t), c)));
  return s;
}

/* ================================================================
 * xpowf -- u10 pow (upstream lines 1316-1332).
 * ================================================================ */
static CONST float xpowf(float x, float y) {
  int yisint = (y == (int)y) || (fabsfk(y) >= (float)(INT64_C(1) << 24));
  int yisodd = (1 & (int)y) != 0 && yisint && fabsfk(y) < (float)(INT64_C(1) << 24);

  float result = expkf(dfmul_f2_f2_f(logkf(fabsfk(x)), y));

  result = xisnanf(result) ? SLEEF_INFINITYf : result;
  result *= (x >= 0 ? 1 : (yisint ? (yisodd ? -1 : 1) : SLEEF_NANf));

  float efx = mulsignf(fabsfk(x) - 1, y);
  if (xisinff(y)) result = efx < 0 ? 0.0f : (efx == 0 ? 1.0f : SLEEF_INFINITYf);
  if (xisinff(x) || x == 0) result = mulsignf((xsignbitf(y) ^ (x == 0)) ? 0 : SLEEF_INFINITYf, yisodd ? x : 1);
  if (xisnanf(x) || xisnanf(y)) result = SLEEF_NANf;
  if (y == 0 || x == 1) result = 1;

  return result;
}

/* ================================================================
 * XPact public ABI -- forward to the upstream-named kernels above.
 * Each kernel has its own distinct body so the SleefSymbolVariants
 * pairwise-address comparison test passes.
 *
 * Distinct-symbol discipline: every public entry point compiles to
 * its OWN function body (no aliasing -- the C standard allows the
 * compiler to fold two functions with identical bodies via COMDAT,
 * but each public function below has a distinct body because each
 * internal kernel has a distinct body).
 * ================================================================ */
float Sleef_sinf_u35  (float x)          { return xsinf(x);            }
float Sleef_cosf_u35  (float x)          { return xcosf(x);            }
float Sleef_tanf_u35  (float x)          { return xtanf(x);            }
float Sleef_atan2f_u10(float y, float x) { return xatan2f_u1(y, x);    }
float Sleef_sqrtf_u10 (float x)          { return xsqrtf_u05(x);       }
float Sleef_powf_u10  (float x, float y) { return xpowf(x, y);         }
float Sleef_expf_u10  (float x)          { return xexpf(x);            }
float Sleef_logf_u10  (float x)          { return xlogf_u1(x);         }

/* Force the helper TU to participate in the link surface so the
 * SleefFMACheck disassembly scan inspects it.  See sleef_helper.c. */
int32_t xpact_sleef_helper_anchor(void);
static int32_t xpact_sleef_sp_anchor_consumer(void) { return xpact_sleef_helper_anchor(); }
