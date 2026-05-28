/*
 * Copyright Naoki Shibata and contributors 2010 - 2024.
 * Distributed under the Boost Software License, Version 1.0.
 * (See accompanying file ../LICENSE.txt or copy at
 *    http://www.boost.org/LICENSE_1_0.txt.)
 *
 * sleef_scalar_dp.c -- vendored scalar double-precision bodies.
 *
 * Mirror of sleef_scalar_sp.c for the double-precision Sleef_*_u35 /
 * Sleef_*_u10 ABI.  Bodies copied VERBATIM from upstream Sleef-3.6
 * src/libm/sleefdp.c per BSL-1.0 license terms.  See sleef_scalar_sp.c
 * for the methodology + modification list (the structure is identical).
 *
 * Naming-tier map:
 *   Sleef_sin_u35   <- upstream xsin          (default tier IS u35)
 *   Sleef_cos_u35   <- upstream xcos
 *   Sleef_tan_u35   <- upstream xtan
 *   Sleef_atan2_u10 <- upstream xatan2_u1     (upstream u1 is the closest
 *                                              fit to u10 contract)
 *   Sleef_sqrt_u10  <- upstream xsqrt_u05     (upstream u05 is tighter
 *                                              than u10, satisfies contract)
 *   Sleef_pow_u10   <- upstream xpow          (default tier IS u10)
 *   Sleef_exp_u10   <- upstream xexp
 *   Sleef_log_u10   <- upstream xlog_u1       (upstream u1 fits u10 contract)
 *
 * Cross-arch bit-exactness is maintained by:
 *   * Verbatim coefficient + constants vendoring.
 *   * Build-flag determinism (--ffp-contract=off etc.) enforced by the
 *     module's sim_path = true posture in Sleef.Build.toml.
 *   * Static-INLINE helpers with deterministic IEEE-754 scalar arithmetic.
 */

#include "sleef_internal.h"

/* See sleef_scalar_sp.c for the rationale on using the negative-sized
 * array trick over C11 _Static_assert. */
#ifndef XPACT_SIMPATH_PROVISIONAL
#define XPACT_SIMPATH_PROVISIONAL 0
#endif

typedef char xpact_sleef_dp_provisional_must_be_zero_in_phase_1g[
    (XPACT_SIMPATH_PROVISIONAL == 0) ? 1 : -1];

#if defined(_MSC_VER) && !defined (__clang__)
#pragma fp_contract (off)
#endif
#pragma STDC FP_CONTRACT OFF

#if defined(__clang__) || (defined(__GNUC__) && !defined(_MSC_VER))
#define SQRT __builtin_sqrt
#else
#define SQRT sqrt
#endif

/* ================================================================
 * Estrin's-method polynomial evaluation macros (verbatim upstream
 * estrin.h, restricted to orders DP kernels actually use).
 * ================================================================ */
#define MLA mla
#define C2V(x) (x)

#define POLY2(x, c1, c0) MLA(x, C2V(c1), C2V(c0))
#define POLY3(x, x2, c2, c1, c0) MLA(x2, C2V(c2), MLA(x, C2V(c1), C2V(c0)))
#define POLY4(x, x2, c3, c2, c1, c0) MLA(x2, MLA(x, C2V(c3), C2V(c2)), MLA(x, C2V(c1), C2V(c0)))
#define POLY5(x, x2, x4, c4, c3, c2, c1, c0) MLA(x4, C2V(c4), POLY4(x, x2, c3, c2, c1, c0))
#define POLY6(x, x2, x4, c5, c4, c3, c2, c1, c0) MLA(x4, POLY2(x, c5, c4), POLY4(x, x2, c3, c2, c1, c0))
#define POLY7(x, x2, x4, c6, c5, c4, c3, c2, c1, c0) MLA(x4, POLY3(x, x2, c6, c5, c4), POLY4(x, x2, c3, c2, c1, c0))
#define POLY8(x, x2, x4, c7, c6, c5, c4, c3, c2, c1, c0) MLA(x4, POLY4(x, x2, c7, c6, c5, c4), POLY4(x, x2, c3, c2, c1, c0))
#define POLY9(x, x2, x4, x8, c8, c7, c6, c5, c4, c3, c2, c1, c0)\
  MLA(x8, C2V(c8), POLY8(x, x2, x4, c7, c6, c5, c4, c3, c2, c1, c0))
#define POLY10(x, x2, x4, x8, c9, c8, c7, c6, c5, c4, c3, c2, c1, c0)\
  MLA(x8, POLY2(x, c9, c8), POLY8(x, x2, x4, c7, c6, c5, c4, c3, c2, c1, c0))
#define POLY11(x, x2, x4, x8, ca, c9, c8, c7, c6, c5, c4, c3, c2, c1, c0)\
  MLA(x8, POLY3(x, x2, ca, c9, c8), POLY8(x, x2, x4, c7, c6, c5, c4, c3, c2, c1, c0))
#define POLY12(x, x2, x4, x8, cb, ca, c9, c8, c7, c6, c5, c4, c3, c2, c1, c0)\
  MLA(x8, POLY4(x, x2, cb, ca, c9, c8), POLY8(x, x2, x4, c7, c6, c5, c4, c3, c2, c1, c0))
#define POLY16(x, x2, x4, x8, cf, ce, cd, cc, cb, ca, c9, c8, c7, c6, c5, c4, c3, c2, c1, c0)\
  MLA(x8, POLY8(x, x2, x4, cf, ce, cd, cc, cb, ca, c9, c8), POLY8(x, x2, x4, c7, c6, c5, c4, c3, c2, c1, c0))
#define POLY19(x, x2, x4, x8, x16, d2, d1, d0, cf, ce, cd, cc, cb, ca, c9, c8, c7, c6, c5, c4, c3, c2, c1, c0)\
  MLA(x16, POLY3(x, x2, d2, d1, d0), POLY16(x, x2, x4, x8, cf, ce, cd, cc, cb, ca, c9, c8, c7, c6, c5, c4, c3, c2, c1, c0))

/* ================================================================
 * Bit-pattern + sign/abs helpers (upstream lines 39-77).
 * ================================================================ */
static INLINE CONST int64_t doubleToRawLongBits(double d) {
  int64_t ret;
  memcpy(&ret, &d, sizeof(ret));
  return ret;
}

static INLINE CONST double longBitsToDouble(int64_t i) {
  double ret;
  memcpy(&ret, &i, sizeof(ret));
  return ret;
}

static INLINE CONST double fabsk(double x) {
  return longBitsToDouble(INT64_C(0x7fffffffffffffff) & doubleToRawLongBits(x));
}

static INLINE CONST double mulsign(double x, double y) {
  return longBitsToDouble(doubleToRawLongBits(x) ^ (doubleToRawLongBits(y) & (INT64_C(1) << 63)));
}

static INLINE CONST double copysignk(double x, double y) {
  return longBitsToDouble((doubleToRawLongBits(x) & ~(INT64_C(1) << 63)) ^ (doubleToRawLongBits(y) & (INT64_C(1) << 63)));
}

static INLINE CONST double sign(double d) { return mulsign(1, d); }
static INLINE CONST double mla(double x, double y, double z) { return x * y + z; }
static INLINE CONST double rintk(double x) { return x < 0 ? (int)(x - 0.5) : (int)(x + 0.5); }
static INLINE CONST double trunck(double x) { return (double)(int)x; }
static INLINE CONST double fmink(double x, double y) { return x < y ? x : y; }
static INLINE CONST double fmaxk(double x, double y) { return x > y ? x : y; }

static INLINE CONST int xsignbit(double d) { return (doubleToRawLongBits(d) & doubleToRawLongBits(-0.0)) == doubleToRawLongBits(-0.0); }
static INLINE CONST int xisnan(double x) { return x != x; }
static INLINE CONST int xisinf(double x) { return x == SLEEF_INFINITY || x == -SLEEF_INFINITY; }
static INLINE CONST int xisnegzero(double x) { return doubleToRawLongBits(x) == doubleToRawLongBits(-0.0); }

static INLINE CONST int xisint(double d) {
  double x = d - (double)(INT64_C(1) << 31) * (int)(d * (1.0 / (INT64_C(1) << 31)));
  return (x == (int)x) || (fabsk(d) >= (double)(INT64_C(1) << 53));
}

static INLINE CONST int xisodd(double d) {
  double x = d - (double)(INT64_C(1) << 31) * (int)(d * (1.0 / (INT64_C(1) << 31)));
  return (1 & (int)x) != 0 && fabsk(d) < (double)(INT64_C(1) << 53);
}

/* ================================================================
 * Exponent extraction + ldexp helpers (upstream lines 89-143).
 * ================================================================ */
static INLINE CONST double pow2i(int q) {
  return longBitsToDouble(((int64_t)(q + 0x3ff)) << 52);
}

static INLINE CONST double ldexpk(double x, int q) {
  double u;
  int m;
  m = q >> 31;
  m = (((m + q) >> 9) - m) << 7;
  q = q - (m << 2);
  m += 0x3ff;
  m = m < 0     ? 0     : m;
  m = m > 0x7ff ? 0x7ff : m;
  u = longBitsToDouble(((int64_t)m) << 52);
  x = x * u * u * u * u;
  u = longBitsToDouble(((int64_t)(q + 0x3ff)) << 52);
  return x * u;
}

static INLINE CONST double ldexp2k(double d, int e) {
  return d * pow2i(e >> 1) * pow2i(e - (e >> 1));
}

static INLINE CONST double ldexp3k(double d, int e) {
  return longBitsToDouble(doubleToRawLongBits(d) + (((int64_t)e) << 52));
}

static INLINE CONST int ilogb2k(double d) {
  return ((doubleToRawLongBits(d) >> 52) & 0x7ff) - 0x3ff;
}

/* ================================================================
 * Double-double (dd) arithmetic helpers (upstream lines 162-461).
 * Sleef_double2 is defined in sleef_internal.h.
 * ================================================================ */
static INLINE CONST double upper(double d) {
  return longBitsToDouble(doubleToRawLongBits(d) & INT64_C(0xfffffffff8000000));
}

static INLINE CONST Sleef_double2 dd(double h, double l) {
  Sleef_double2 ret;
  ret.x = h; ret.y = l;
  return ret;
}

static INLINE CONST Sleef_double2 ddnormalize_d2_d2(Sleef_double2 t) {
  Sleef_double2 s;
  s.x = t.x + t.y;
  s.y = t.x - s.x + t.y;
  return s;
}

static INLINE CONST Sleef_double2 ddscale_d2_d2_d(Sleef_double2 d, double s) {
  Sleef_double2 r;
  r.x = d.x * s;
  r.y = d.y * s;
  return r;
}

static INLINE CONST Sleef_double2 ddneg_d2_d2(Sleef_double2 d) {
  Sleef_double2 r;
  r.x = -d.x;
  r.y = -d.y;
  return r;
}

static INLINE CONST Sleef_double2 ddadd_d2_d_d(double x, double y) {
  /* |x| >= |y| */
  Sleef_double2 r;
  r.x = x + y;
  r.y = x - r.x + y;
  return r;
}

static INLINE CONST Sleef_double2 ddadd2_d2_d_d(double x, double y) {
  Sleef_double2 r;
  r.x = x + y;
  double v = r.x - x;
  r.y = (x - (r.x - v)) + (y - v);
  return r;
}

static INLINE CONST Sleef_double2 ddadd_d2_d2_d(Sleef_double2 x, double y) {
  /* |x| >= |y| */
  Sleef_double2 r;
  r.x = x.x + y;
  r.y = x.x - r.x + y + x.y;
  return r;
}

static INLINE CONST Sleef_double2 ddadd2_d2_d2_d(Sleef_double2 x, double y) {
  Sleef_double2 r;
  r.x  = x.x + y;
  double v = r.x - x.x;
  r.y = (x.x - (r.x - v)) + (y - v);
  r.y += x.y;
  return r;
}

static INLINE CONST Sleef_double2 ddadd_d2_d_d2(double x, Sleef_double2 y) {
  /* |x| >= |y| */
  Sleef_double2 r;
  r.x = x + y.x;
  r.y = x - r.x + y.x + y.y;
  return r;
}

static INLINE CONST Sleef_double2 ddadd2_d2_d_d2(double x, Sleef_double2 y) {
  Sleef_double2 r;
  r.x  = x + y.x;
  double v = r.x - x;
  r.y = (x - (r.x - v)) + (y.x - v) + y.y;
  return r;
}

static INLINE CONST Sleef_double2 ddadd_d2_d2_d2(Sleef_double2 x, Sleef_double2 y) {
  /* |x| >= |y| */
  Sleef_double2 r;
  r.x = x.x + y.x;
  r.y = x.x - r.x + y.x + x.y + y.y;
  return r;
}

static INLINE CONST Sleef_double2 ddadd2_d2_d2_d2(Sleef_double2 x, Sleef_double2 y) {
  Sleef_double2 r;
  r.x  = x.x + y.x;
  double v = r.x - x.x;
  r.y = (x.x - (r.x - v)) + (y.x - v);
  r.y += x.y + y.y;
  return r;
}

static INLINE CONST Sleef_double2 ddsub_d2_d2_d2(Sleef_double2 x, Sleef_double2 y) {
  /* |x| >= |y| */
  Sleef_double2 r;
  r.x = x.x - y.x;
  r.y = x.x - r.x - y.x + x.y - y.y;
  return r;
}

static INLINE CONST Sleef_double2 dddiv_d2_d2_d2(Sleef_double2 n, Sleef_double2 d) {
  double t = 1.0 / d.x;
  double dh  = upper(d.x), dl  = d.x - dh;
  double th  = upper(t  ), tl  = t   - th;
  double nhh = upper(n.x), nhl = n.x - nhh;

  Sleef_double2 q;
  q.x = n.x * t;
  double u = -q.x + nhh * th + nhh * tl + nhl * th + nhl * tl +
    q.x * (1 - dh * th - dh * tl - dl * th - dl * tl);
  q.y = t * (n.y - q.x * d.y) + u;
  return q;
}

static INLINE CONST Sleef_double2 ddmul_d2_d_d(double x, double y) {
  double xh = upper(x), xl = x - xh;
  double yh = upper(y), yl = y - yh;
  Sleef_double2 r;
  r.x = x * y;
  r.y = xh * yh - r.x + xl * yh + xh * yl + xl * yl;
  return r;
}

static INLINE CONST Sleef_double2 ddmul_d2_d2_d(Sleef_double2 x, double y) {
  double xh = upper(x.x), xl = x.x - xh;
  double yh = upper(y  ), yl = y   - yh;
  Sleef_double2 r;
  r.x = x.x * y;
  r.y = xh * yh - r.x + xl * yh + xh * yl + xl * yl + x.y * y;
  return r;
}

static INLINE CONST Sleef_double2 ddmul_d2_d2_d2(Sleef_double2 x, Sleef_double2 y) {
  double xh = upper(x.x), xl = x.x - xh;
  double yh = upper(y.x), yl = y.x - yh;
  Sleef_double2 r;
  r.x = x.x * y.x;
  r.y = xh * yh - r.x + xl * yh + xh * yl + xl * yl + x.x * y.y + x.y * y.x;
  return r;
}

static INLINE CONST double ddmul_d_d2_d2(Sleef_double2 x, Sleef_double2 y) {
  double xh = upper(x.x), xl = x.x - xh;
  double yh = upper(y.x), yl = y.x - yh;
  return x.y * yh + xh * y.y + xl * yl + xh * yl + xl * yh + xh * yh;
}

static INLINE CONST Sleef_double2 ddsqu_d2_d2(Sleef_double2 x) {
  double xh = upper(x.x), xl = x.x - xh;
  Sleef_double2 r;
  r.x = x.x * x.x;
  r.y = xh * xh - r.x + (xh + xh) * xl + xl * xl + x.x * (x.y + x.y);
  return r;
}

static INLINE CONST Sleef_double2 ddrec_d2_d(double d) {
  double t = 1.0 / d;
  double dh = upper(d), dl = d - dh;
  double th = upper(t), tl = t - th;
  Sleef_double2 q;
  q.x = t;
  q.y = t * (1 - dh * th - dh * tl - dl * th - dl * tl);
  return q;
}

/* ================================================================
 * Payne-Hanek-like argument reduction (upstream lines 736-787).
 * ================================================================ */
typedef struct {
  double d;
  int32_t i;
} di_t;

typedef struct {
  Sleef_double2 dd;
  int32_t i;
} ddi_t;

static INLINE CONST double orsign(double x, double y) {
  return longBitsToDouble(doubleToRawLongBits(x) | (doubleToRawLongBits(y) & (INT64_C(1) << 63)));
}

static CONST di_t rempisub(double x) {
  di_t ret;
  double c = mulsign(INT64_C(1) << 52, x);
  double rint4x = fabsk(4*x) > (double)(INT64_C(1) << 52) ? (4*x) : orsign(mla(4, x, c) - c, x);
  double rintx  = fabsk(  x) > (double)(INT64_C(1) << 52) ?   x   : orsign(x + c - c        , x);
  ret.d = mla(-0.25, rint4x,      x);
  ret.i = (int32_t)mla(-4, rintx, rint4x);
  return ret;
}

static CONST ddi_t rempi(double a) {
  Sleef_double2 x, y;
  di_t di;
  int ex = ilogb2k(a) - 55, q = ex > (700-55) ? -64 : 0;
  a = ldexp3k(a, q);
  if (ex < 0) ex = 0;
  ex *= 4;
  x = ddmul_d2_d_d(a, Sleef_rempitabdp[ex]);
  di = rempisub(x.x);
  q = di.i;
  x.x = di.d;
  x = ddnormalize_d2_d2(x);
  y = ddmul_d2_d_d(a, Sleef_rempitabdp[ex+1]);
  x = ddadd2_d2_d2_d2(x, y);
  di = rempisub(x.x);
  q += di.i;
  x.x = di.d;
  x = ddnormalize_d2_d2(x);
  y = ddmul_d2_d2_d(dd(Sleef_rempitabdp[ex+2], Sleef_rempitabdp[ex+3]), a);
  x = ddadd2_d2_d2_d2(x, y);
  x = ddnormalize_d2_d2(x);
  x = ddmul_d2_d2_d2(x, dd(3.141592653589793116*2, 1.2246467991473532072e-16*2));
  ddi_t ret;
  ret.dd = fabsk(a) < 0.7 ? dd(a, 0) : x;
  ret.i = q;
  return ret;
}

/* ================================================================
 * xsin -- u35 sine (upstream lines 789-840).
 * ================================================================ */
static CONST double xsin(double d) {
  double u, s, t = d;
  int ql;

  if (fabsk(d) < TRIGRANGEMAX2) {
    ql = (int)rintk(d * M_1_PI);
    d = mla(ql, -PI_A2, d);
    d = mla(ql, -PI_B2, d);
  } else if (fabsk(d) < TRIGRANGEMAX) {
    double dqh = trunck(d * (M_1_PI / (1 << 24))) * (double)(1 << 24);
    ql = (int)rintk(mla(d, M_1_PI, -dqh));

    d = mla(dqh, -PI_A, d);
    d = mla( ql, -PI_A, d);
    d = mla(dqh, -PI_B, d);
    d = mla( ql, -PI_B, d);
    d = mla(dqh, -PI_C, d);
    d = mla( ql, -PI_C, d);
    d = mla(dqh + ql, -PI_D, d);
  } else {
    ddi_t ddi = rempi(t);
    ql = ((ddi.i & 3) * 2 + (ddi.dd.x > 0) + 1) >> 2;
    if ((ddi.i & 1) != 0) {
      ddi.dd = ddadd2_d2_d2_d2(ddi.dd, dd(mulsign(3.141592653589793116*-0.5, ddi.dd.x),
                                          mulsign(1.2246467991473532072e-16*-0.5, ddi.dd.x)));
    }
    d = ddi.dd.x + ddi.dd.y;
    if (xisinf(t) || xisnan(t)) d = SLEEF_NAN;
  }

  s = d * d;

  if ((ql & 1) != 0) d = -d;

  double s2 = s * s, s4 = s2 * s2;
  u = POLY8(s, s2, s4,
            -7.97255955009037868891952e-18,
            2.81009972710863200091251e-15,
            -7.64712219118158833288484e-13,
            1.60590430605664501629054e-10,
            -2.50521083763502045810755e-08,
            2.75573192239198747630416e-06,
            -0.000198412698412696162806809,
            0.00833333333333332974823815);
  u = mla(u, s, -0.166666666666666657414808);

  u = mla(s, u * d, d);

  if (xisnegzero(t)) u = t;

  return u;
}

/* ================================================================
 * xcos -- u35 cosine (upstream lines 895-945).
 * ================================================================ */
static CONST double xcos(double d) {
  double u, s, t = d;
  int ql;

  if (fabsk(d) < TRIGRANGEMAX2) {
    ql = (int)mla(2, rintk(d * M_1_PI - 0.5), 1);
    d = mla(ql, -PI_A2*0.5, d);
    d = mla(ql, -PI_B2*0.5, d);
  } else if (fabsk(d) < TRIGRANGEMAX) {
    double dqh = trunck(d * (M_1_PI / (INT64_C(1) << 23)) - 0.5 * (M_1_PI / (INT64_C(1) << 23)));
    ql = 2*(int)rintk(d * M_1_PI - 0.5 - dqh * (double)(INT64_C(1) << 23))+1;
    dqh *= 1 << 24;

    d = mla(dqh, -PI_A*0.5, d);
    d = mla( ql, -PI_A*0.5, d);
    d = mla(dqh, -PI_B*0.5, d);
    d = mla( ql, -PI_B*0.5, d);
    d = mla(dqh, -PI_C*0.5, d);
    d = mla( ql, -PI_C*0.5, d);
    d = mla(dqh + ql , -PI_D*0.5, d);
  } else {
    ddi_t ddi = rempi(t);
    ql = ((ddi.i & 3) * 2 + (ddi.dd.x > 0) + 7) >> 1;
    if ((ddi.i & 1) == 0) {
      ddi.dd = ddadd2_d2_d2_d2(ddi.dd, dd(mulsign(3.141592653589793116*-0.5, ddi.dd.x > 0 ? 1.0 : -1.0),
                                          mulsign(1.2246467991473532072e-16*-0.5, ddi.dd.x > 0 ? 1.0 : -1.0)));
    }
    d = ddi.dd.x + ddi.dd.y;
    if (xisinf(t) || xisnan(t)) d = SLEEF_NAN;
  }

  s = d * d;

  if ((ql & 2) == 0) d = -d;

  double s2 = s * s, s4 = s2 * s2;
  u = POLY8(s, s2, s4,
            -7.97255955009037868891952e-18,
            2.81009972710863200091251e-15,
            -7.64712219118158833288484e-13,
            1.60590430605664501629054e-10,
            -2.50521083763502045810755e-08,
            2.75573192239198747630416e-06,
            -0.000198412698412696162806809,
            0.00833333333333332974823815);
  u = mla(u, s, -0.166666666666666657414808);

  u = mla(s, u * d, d);

  return u;
}

/* ================================================================
 * xtan -- u35 tangent (upstream lines 1322-1373).
 * ================================================================ */
static CONST double xtan(double d) {
  double u, s, x, y;
  int ql;

  if (fabsk(d) < TRIGRANGEMAX2) {
    ql = (int)rintk(d * (2 * M_1_PI));
    x = mla(ql, -PI_A2*0.5, d);
    x = mla(ql, -PI_B2*0.5, x);
  } else if (fabsk(d) < 1e+6) {
    double dqh = trunck(d * ((2 * M_1_PI) / (1 << 24))) * (double)(1 << 24);
    ql = (int)rintk(d * (2 * M_1_PI) - dqh);

    x = mla(dqh, -PI_A * 0.5, d);
    x = mla( ql, -PI_A * 0.5, x);
    x = mla(dqh, -PI_B * 0.5, x);
    x = mla( ql, -PI_B * 0.5, x);
    x = mla(dqh, -PI_C * 0.5, x);
    x = mla( ql, -PI_C * 0.5, x);
    x = mla(dqh + ql, -PI_D * 0.5, x);
  } else {
    ddi_t ddi = rempi(d);
    ql = ddi.i;
    x = ddi.dd.x + ddi.dd.y;
    if (xisinf(d) || xisnan(d)) x = SLEEF_NAN;
  }

  x *= 0.5;
  s = x * x;

  double s2 = s * s, s4 = s2 * s2;
  u = POLY8(s, s2, s4,
            +0.3245098826639276316e-3,
            +0.5619219738114323735e-3,
            +0.1460781502402784494e-2,
            +0.3591611540792499519e-2,
            +0.8863268409563113126e-2,
            +0.2186948728185535498e-1,
            +0.5396825399517272970e-1,
            +0.1333333333330500581e+0);

  u = mla(u, s, +0.3333333333333343695e+0);
  u = mla(s, u * x, x);

  y = mla(u, u, -1);
  x = -2 * u;

  if ((ql & 1) != 0) { double t = x; x = y; y = -t; }

  u = x / y;

  return u;
}

/* ================================================================
 * atan2k_u1 + xatan2_u1 -- u10 atan2 (upstream lines 611-665).
 * ================================================================ */
static Sleef_double2 atan2k_u1(Sleef_double2 y, Sleef_double2 x) {
  double u;
  Sleef_double2 s, t;
  int q = 0;

  if (x.x < 0) { x.x = -x.x; x.y = -x.y; q = -2; }
  if (y.x > x.x) { t = x; x = y; y.x = -t.x; y.y = -t.y; q += 1; }

  s = dddiv_d2_d2_d2(y, x);
  t = ddsqu_d2_d2(s);
  t = ddnormalize_d2_d2(t);

  double t2 = t.x * t.x, t4 = t2 * t2, t8 = t4 * t4;
  u = POLY16(t.x, t2, t4, t8,
             1.06298484191448746607415e-05,
             -0.000125620649967286867384336,
             0.00070557664296393412389774,
             -0.00251865614498713360352999,
             0.00646262899036991172313504,
             -0.0128281333663399031014274,
             0.0208024799924145797902497,
             -0.0289002344784740315686289,
             0.0359785005035104590853656,
             -0.041848579703592507506027,
             0.0470843011653283988193763,
             -0.0524914210588448421068719,
             0.0587946590969581003860434,
             -0.0666620884778795497194182,
             0.0769225330296203768654095,
             -0.0909090442773387574781907);
  u = mla(u, t.x, 0.111111108376896236538123);
  u = mla(u, t.x, -0.142857142756268568062339);
  u = mla(u, t.x, 0.199999999997977351284817);
  u = mla(u, t.x, -0.333333333333317605173818);

  t = ddadd_d2_d2_d2(s, ddmul_d2_d2_d(ddmul_d2_d2_d2(s, t), u));

  if (fabsk(s.x) < 1e-200) t = s;
  t = ddadd2_d2_d2_d2(ddmul_d2_d2_d(dd(1.570796326794896557998982, 6.12323399573676603586882e-17), q), t);

  return t;
}

static CONST double xatan2_u1(double y, double x) {
  if (fabsk(x) < 5.5626846462680083984e-309) { y *= (UINT64_C(1) << 53); x *= (UINT64_C(1) << 53); }
  Sleef_double2 d = atan2k_u1(dd(fabsk(y), 0), dd(x, 0));
  double r = d.x + d.y;

  r = mulsign(r, x);
  if (xisinf(x) || x == 0) r = M_PI/2 - (xisinf(x) ? (sign(x) * (M_PI  /2)) : 0);
  if (xisinf(y)          ) r = M_PI/2 - (xisinf(x) ? (sign(x) * (M_PI*1/4)) : 0);
  if (             y == 0) r = (sign(x) == -1 ? M_PI : 0);

  return xisnan(x) || xisnan(y) ? SLEEF_NAN : mulsign(r, y);
}

/* ================================================================
 * xsqrt_u05 -- u10 sqrt (upstream lines 2212-2242).
 * ================================================================ */
static CONST double xsqrt_u05(double d) {
  double q = 0.5;

  d = d < 0 ? SLEEF_NAN : d;

  if (d < 8.636168555094445E-78) {
    d *= 1.157920892373162E77;
    q = 2.9387358770557188E-39 * 0.5;
  }

  if (d > 1.3407807929942597e+154) {
    d *= 7.4583407312002070e-155;
    q = 1.1579208923731620e+77 * 0.5;
  }

  /* http://en.wikipedia.org/wiki/Fast_inverse_square_root */
  double x = longBitsToDouble(INT64_C(0x5fe6ec85e7de30da) - (doubleToRawLongBits(d + 1e-320) >> 1));

  x = x * (1.5 - 0.5 * d * x * x);
  x = x * (1.5 - 0.5 * d * x * x);
  x = x * (1.5 - 0.5 * d * x * x) * d;

  Sleef_double2 d2 = ddmul_d2_d2_d2(ddadd2_d2_d_d2(d, ddmul_d2_d_d(x, x)), ddrec_d2_d(x));

  double ret = (d2.x + d2.y) * q;

  ret = d == SLEEF_INFINITY ? SLEEF_INFINITY : ret;
  ret = d == 0 ? d : ret;

  return ret;
}

/* ================================================================
 * xexp -- u10 exp (upstream lines 1469-1497).
 * ================================================================ */
static CONST double xexp(double d) {
  int q = (int)rintk(d * R_LN2);
  double s, u;

  s = mla(q, -L2U, d);
  s = mla(q, -L2L, s);

  double s2 = s * s, s4 = s2 * s2, s8 = s4 * s4;
  u = POLY10(s, s2, s4, s8,
             2.08860621107283687536341e-09,
             2.51112930892876518610661e-08,
             2.75573911234900471893338e-07,
             2.75572362911928827629423e-06,
             2.4801587159235472998791e-05,
             0.000198412698960509205564975,
             0.00138888888889774492207962,
             0.00833333333331652721664984,
             0.0416666666666665047591422,
             0.166666666666666851703837);
  u = mla(u, s, +0.5);

  u = s * s * u + s + 1;
  u = ldexp2k(u, q);

  if (d > 709.78271114955742909217217426) u = SLEEF_INFINITY;
  if (d < -1000) u = 0;

  return u;
}

/* ================================================================
 * logk + xlog_u1 -- u10 log (upstream lines 1526-1602).
 * ================================================================ */
static INLINE CONST Sleef_double2 logk(double d) {
  Sleef_double2 x, x2, s;
  double m, t;
  int e;

  int o = d < DBL_MIN;
  if (o) d *= (double)(INT64_C(1) << 32) * (double)(INT64_C(1) << 32);

  e = ilogb2k(d * (1.0/0.75));
  m = ldexp3k(d, -e);

  if (o) e -= 64;

  x = dddiv_d2_d2_d2(ddadd2_d2_d_d(-1, m), ddadd2_d2_d_d(1, m));
  x2 = ddsqu_d2_d2(x);

  double x4 = x2.x * x2.x, x8 = x4 * x4, x16 = x8 * x8;
  t = POLY9(x2.x, x4, x8, x16,
            0.116255524079935043668677,
            0.103239680901072952701192,
            0.117754809412463995466069,
            0.13332981086846273921509,
            0.153846227114512262845736,
            0.181818180850050775676507,
            0.222222222230083560345903,
            0.285714285714249172087875,
            0.400000000000000077715612);

  Sleef_double2 c = dd(0.666666666666666629659233, 3.80554962542412056336616e-17);
  s = ddmul_d2_d2_d(dd(0.693147180559945286226764, 2.319046813846299558417771e-17), e);
  s = ddadd_d2_d2_d2(s, ddscale_d2_d2_d(x, 2));
  x = ddmul_d2_d2_d2(x2, x);
  s = ddadd_d2_d2_d2(s, ddmul_d2_d2_d2(x, c));
  x = ddmul_d2_d2_d2(x2, x);
  s = ddadd_d2_d2_d2(s, ddmul_d2_d2_d(x, t));

  return s;
}

static CONST double xlog_u1(double d) {
  Sleef_double2 x, s;
  double m, t, x2;
  int e;

  int o = d < DBL_MIN;
  if (o) d *= (double)(INT64_C(1) << 32) * (double)(INT64_C(1) << 32);

  e = ilogb2k(d * (1.0/0.75));
  m = ldexp3k(d, -e);

  if (o) e -= 64;

  x = dddiv_d2_d2_d2(ddadd2_d2_d_d(-1, m), ddadd2_d2_d_d(1, m));
  x2 = x.x * x.x;

  double x4 = x2 * x2, x8 = x4 * x4;
  t = POLY7(x2, x4, x8,
            0.1532076988502701353e+0,
            0.1525629051003428716e+0,
            0.1818605932937785996e+0,
            0.2222214519839380009e+0,
            0.2857142932794299317e+0,
            0.3999999999635251990e+0,
            0.6666666666667333541e+0);

  s = ddmul_d2_d2_d(dd(0.693147180559945286226764, 2.319046813846299558417771e-17), (double)e);
  s = ddadd_d2_d2_d2(s, ddscale_d2_d2_d(x, 2));
  s = ddadd_d2_d2_d(s, x2 * x.x * t);

  double r = s.x + s.y;

  if (xisinf(d)) r = SLEEF_INFINITY;
  if (d < 0 || xisnan(d)) r = SLEEF_NAN;
  if (d == 0) r = -SLEEF_INFINITY;

  return r;
}

/* ================================================================
 * expk -- helper for xpow (upstream lines 1604-1635).
 * ================================================================ */
static INLINE CONST double expk(Sleef_double2 d) {
  int q = (int)rintk((d.x + d.y) * R_LN2);
  Sleef_double2 s, t;
  double u;

  s = ddadd2_d2_d2_d(d, q * -L2U);
  s = ddadd2_d2_d2_d(s, q * -L2L);

  s = ddnormalize_d2_d2(s);

  double s2 = s.x * s.x, s4 = s2 * s2, s8 = s4 * s4;
  u = POLY10(s.x, s2, s4, s8,
             2.51069683420950419527139e-08,
             2.76286166770270649116855e-07,
             2.75572496725023574143864e-06,
             2.48014973989819794114153e-05,
             0.000198412698809069797676111,
             0.0013888888939977128960529,
             0.00833333333332371417601081,
             0.0416666666665409524128449,
             0.166666666666666740681535,
             0.500000000000000999200722);

  t = ddadd_d2_d_d2(1, s);
  t = ddadd_d2_d2_d2(t, ddmul_d2_d2_d(ddsqu_d2_d2(s), u));

  u = ldexpk(t.x + t.y, q);

  if (d.x < -1000) u = 0;

  return u;
}

/* ================================================================
 * xpow -- u10 pow (upstream lines 1637-1654).
 * ================================================================ */
static CONST double xpow(double x, double y) {
  int yisint = xisint(y);
  int yisodd = yisint && xisodd(y);

  Sleef_double2 d = ddmul_d2_d2_d(logk(fabsk(x)), y);
  double result = expk(d);

  result = (d.x > 709.78271114955742909217217426 || xisnan(result)) ? SLEEF_INFINITY : result;
  result *= (x > 0 ? 1 : (yisint ? (yisodd ? -1 : 1) : SLEEF_NAN));

  double efx = mulsign(fabsk(x) - 1, y);
  if (xisinf(y)) result = efx < 0 ? 0.0 : (efx == 0 ? 1.0 : SLEEF_INFINITY);
  if (xisinf(x) || x == 0) result = mulsign((xsignbit(y) ^ (x == 0)) ? 0 : SLEEF_INFINITY, yisodd ? x : 1);
  if (xisnan(x) || xisnan(y)) result = SLEEF_NAN;
  if (y == 0 || x == 1) result = 1;

  return result;
}

/* ================================================================
 * XPact public ABI -- forward to the upstream-named kernels above.
 * ================================================================ */
double Sleef_sin_u35  (double x)            { return xsin(x);          }
double Sleef_cos_u35  (double x)            { return xcos(x);          }
double Sleef_tan_u35  (double x)            { return xtan(x);          }
double Sleef_atan2_u10(double y, double x)  { return xatan2_u1(y, x);  }
double Sleef_sqrt_u10 (double x)            { return xsqrt_u05(x);     }
double Sleef_pow_u10  (double x, double y)  { return xpow(x, y);       }
double Sleef_exp_u10  (double x)            { return xexp(x);          }
double Sleef_log_u10  (double x)            { return xlog_u1(x);       }

int32_t xpact_sleef_helper_anchor(void);
static int32_t xpact_sleef_dp_anchor_consumer(void) { return xpact_sleef_helper_anchor(); }
