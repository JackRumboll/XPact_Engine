// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XSimMathBodies.h -- XCore::SimMath::* inline wrappers for Sleef.
// =====================================================================
//
// Per XCore-4a Rev 3 Section 6.1 (locked decision 4: two math headers
// ship -- XMathFast.h and XSimMath.h -- with a namespace alias resolving
// at TU scope based on XPACT_SIMPATH) + Section 14 Step 11.5.
//
// This header is the IMPLEMENTATION side of the
// XCore::SimMath::{Sin,Cos,Tan,Atan2,Sqrt,Pow,Exp,Log} surface that
// XSimMath.h (Subagent A's authored header) exposes.  Subagent A's
// XSimMath.h declares the namespace; this header provides the inline
// definitions that route to the vendored Sleef-3.6+ scalar entry
// points (Sleef_sinf_u35 etc.).
//
// Header-only by intent.  The wrappers are one-line inlines around the
// extern "C" Sleef calls; there is nothing to put out-of-line in a
// .cpp file.  Inline-in-header avoids a per-call procedure-call
// overhead that would defeat the whole point of selecting the cheap
// Sleef u35 trig variant.
//
// Include relationship:
//   * XSimMath.h includes XSimMathBodies.h after its own type +
//     namespace declarations.
//   * XSimPathMathOverrides.h (the /FI-included header) pulls in the
//     vendored Sleef.h, so by the time XSimMath.h ships these wrappers
//     the Sleef_*_u10 / Sleef_*_u35 entries are already declared.
//
// On non-sim-path TUs this header is included via the XCore::Math
// namespace alias chain anyway, but the bodies are equally valid
// non-sim-path -- the Sleef entries are just one more transcendental
// option.  However the toolchain only links Sleef into modules with
// sim_path = true (Contract Section 4.3); a non-sim-path TU that
// reaches XCore::SimMath::Sin would fail at link time with an
// unresolved Sleef_sinf_u35 symbol.  That's the documented behaviour:
// the XCore::Math alias on non-sim-path TUs resolves to XCore::FastMath,
// NOT XCore::SimMath, so the link error never fires unless someone
// deliberately reaches into the sim namespace from a non-sim TU.
//
// =====================================================================
//
// THREADING + DETERMINISM CONTRACT (XCore-4a Section 6.2):
//   * All functions are pure noexcept; trivially thread-safe.
//   * Sleef's scalar entries have no internal state; reentrant safe.
//   * Bit-exactness across Win64-x86_64, Linux-x86_64, Android-ARM64
//     is the load-bearing contract verified by XCore-4a Section 17.3
//     C2 (1000-step Verlet trajectory) and C-extra (disassembly scan).
//
// =====================================================================

// On sim-path TUs, XSimPathMathOverrides.h is force-included by XBT
// (/FI on MSVC, -include on Clang) and pulls in <Sleef.h>.  However
// downstream consumers of XSimMathBodies.h that compile on NON-sim-
// path TUs (e.g., a unit-test TU that wants to assert XCore::SimMath
// produces specific outputs without forcing sim-path discipline on
// the whole test module) need the Sleef declarations too.  We
// therefore include Sleef.h directly here -- the upstream vendor
// module's public_include_paths makes the path resolvable from any
// dependent module.

#include "Sleef.h"

namespace XCore::SimMath
{

// ---------------------------------------------------------------------
// Single-precision wrappers.
// ---------------------------------------------------------------------
//
// The XCore::Math namespace alias (in XSimMath.h, see locked decision
// 4) re-exports these as XCore::Math::Sin etc. on sim-path TUs.
//
// IMPORTANT: we cannot call ::sinf etc. directly inside these
// wrappers, because XSimPathMathOverrides.h #defines those identifiers
// to the Sleef variants, and the macro substitution would textually
// produce `Sleef_sinf_u35(X)` -- which IS what we want, but for clarity
// and grep-ability we call the Sleef entries by their literal names.

inline float Sin   (float X)        noexcept { return ::Sleef_sinf_u35  (X);    }
inline float Cos   (float X)        noexcept { return ::Sleef_cosf_u35  (X);    }
inline float Tan   (float X)        noexcept { return ::Sleef_tanf_u35  (X);    }
inline float Atan2 (float Y, float X) noexcept { return ::Sleef_atan2f_u10(Y, X); }
inline float Sqrt  (float X)        noexcept { return ::Sleef_sqrtf_u10 (X);    }
inline float Pow   (float X, float Y) noexcept { return ::Sleef_powf_u10  (X, Y); }
inline float Exp   (float X)        noexcept { return ::Sleef_expf_u10  (X);    }
inline float Log   (float X)        noexcept { return ::Sleef_logf_u10  (X);    }

// ---------------------------------------------------------------------
// Double-precision wrappers.
// ---------------------------------------------------------------------
//
// Used by FLargeWorldVector (Section 6.1 locked decision 1) and any
// sim-path code that legitimately needs higher precision than FVector's
// float storage.

inline double Sin   (double X)         noexcept { return ::Sleef_sin_u35  (X);    }
inline double Cos   (double X)         noexcept { return ::Sleef_cos_u35  (X);    }
inline double Tan   (double X)         noexcept { return ::Sleef_tan_u35  (X);    }
inline double Atan2 (double Y, double X) noexcept { return ::Sleef_atan2_u10(Y, X); }
inline double Sqrt  (double X)         noexcept { return ::Sleef_sqrt_u10 (X);    }
inline double Pow   (double X, double Y) noexcept { return ::Sleef_pow_u10  (X, Y); }
inline double Exp   (double X)         noexcept { return ::Sleef_exp_u10  (X);    }
inline double Log   (double X)         noexcept { return ::Sleef_log_u10  (X);    }

} // namespace XCore::SimMath
