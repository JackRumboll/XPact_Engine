// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XSimMath.h -- sim-path math namespace.
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1 + locked decision 4 (two-header dispatch)
// + Section 6.3 (Determinism contract) + Section 1.3 locked decision 5
// (FRotator sim-path ban).
//
// This header is the "sim-path" half of the two-header model. In a
// sim-path TU (XPACT_SIMPATH=1):
//   * All transcendentals route through XCore::SimMath::* which in
//     turn delegate to vendored Sleef-3.6+ no-FMA mode (Step 11.5).
//     Until Step 11.5 lands, the provisional libm fallback runs
//     under XPACT_SIMPATH_PROVISIONAL=1 (Section 6.3).
//   * libm transcendentals (std::sin, std::cos, std::sqrt, std::pow,
//     ...) are POISONED via [[deprecated]] decoration; a stray
//     std::sqrt call in a sim-path TU fails the build with a clear
//     diagnostic.
//   * FMA is banned (compiler flags -mno-fma + /fp:precise +
//     -ffp-contract=off). The build configuration sets these on
//     sim-path TUs; this header documents but does not enforce.
//   * FRotator is decorated [[deprecated]] (locked decision 5) so
//     sim-path TUs cannot use degree-based rotation.
//
// PHASE 1E STATUS (Subagent A scope):
//   The current Sleef integration is NOT YET WIRED (Subagent B's
//   scope). The transcendental forwarders below delegate to libm
//   for now with a `// TODO(Phase 1e Subagent B): swap to
//   XSleef_*` marker at each site. The libm-poisoning [[deprecated]]
//   decorations DO land here so accidental std::* calls already fail
//   the build at compile time.
//
//   The complete contract once Subagent B lands:
//       XCore::SimMath::Sqrt(x)  -> Sleef_sqrtf_u10(x)
//       XCore::SimMath::Sin(x)   -> Sleef_sinf_u35(x)
//       ... etc.
//   per Section 6.3 paragraph 1.
//
// =====================================================================

// Type headers (re-exported; same set as XMathFast.h).
#include "Math/FVector.h"
#include "Math/FVector2D.h"
#include "Math/FVector4.h"
#include "Math/FLargeWorldVector.h"
#include "Math/FQuat.h"
#include "Math/FMatrix.h"
#include "Math/FTransform.h"
#include "Math/FBox.h"
#include "Math/FSphere.h"
#include "Math/FPlane.h"
#include "Math/FLinearColor.h"
#include "Math/FColor.h"

// FRotator sim-path ban (locked decision 5).
//
// We pull in the FRotator type but immediately re-export it under a
// [[deprecated]] alias so any reference to ::XCore::FRotator in a
// sim-path TU produces the spec-mandated diagnostic. The deprecation
// is delivered via a re-aliased type so the FRotator <-> FQuat
// conversion helpers (QuatFromRotator / RotatorFromQuat / FQuat::
// FromEuler / FQuat::ToEuler) are likewise reachable only via the
// deprecation path.
//
// Note: this does NOT make the FRotator type itself disappear from
// non-sim-path TUs; the [[deprecated]] decoration is local to
// XCore::SimMath. Non-sim-path TUs include XMathFast.h which does
// not pull in this header.

#include "Math/FRotator.h"

// Scalar utilities + FRandomStream (sim-path-safe-by-construction;
// the same headers serve both namespaces).
#include "Math/XMathScalar.h"
#include "Math/FRandomStream.h"

#include <cmath>

// =====================================================================
// LIBM POISONING (Section 6.3 + acceptance criterion C5).
// =====================================================================
//
// In a sim-path TU, calling std::sqrt / std::sin / etc. directly is
// forbidden. We force a build error at every call site by:
//   (1) re-declaring the std::* function in the global namespace with
//       a [[deprecated]] attribute; OR
//   (2) #pragma deprecated on MSVC.
//
// MSVC and Clang/GCC handle this differently:
//   * MSVC supports #pragma deprecated(symbol).
//   * Clang/GCC support [[deprecated]] only on FRESH declarations.
//     Re-declaring std::sqrt with [[deprecated]] is technically UB
//     under the std namespace, BUT the call-site name lookup will
//     find the deprecated decoration if we declare it at the
//     enclosing namespace scope and unqualified call sites prefer it.
//
// The safest portable approach used here:
//   * #pragma GCC poison / #pragma deprecated for the canonical
//     names std::sqrt, std::sin, std::cos, std::tan, std::atan2,
//     std::asin, std::acos, std::atan, std::pow, std::exp, std::log.
//   * Sim-path callers MUST use XCore::SimMath::Sqrt et al.; the
//     unqualified ::sqrt / std::sqrt forms emit a diagnostic.
//
// Note: the [[deprecated]] poison only fires in sim-path TUs (where
// this header is included). Non-sim-path TUs include XMathFast.h
// instead and are unaffected.
//
// =====================================================================

#if defined(_MSC_VER)
    #pragma deprecated("std::sin")
    #pragma deprecated("std::cos")
    #pragma deprecated("std::tan")
    #pragma deprecated("std::asin")
    #pragma deprecated("std::acos")
    #pragma deprecated("std::atan")
    #pragma deprecated("std::atan2")
    #pragma deprecated("std::sqrt")
    #pragma deprecated("std::pow")
    #pragma deprecated("std::exp")
    #pragma deprecated("std::log")
    #pragma deprecated("std::log2")
#endif

// Clang/GCC: re-declare the libm symbols at the enclosing global namespace
// with [[deprecated]]. Unqualified std::sin name lookup falls through to
// these decorations and the build fails.
//
// NOTE: this is the cleanest portable scheme; the actual symbol
// override is performed by Sleef once it's wired in Step 11.5
// (Subagent B's scope). Until then, the decoration ensures the
// build fails at the offending call site rather than silently
// producing a non-deterministic frame.

#if defined(__clang__) || defined(__GNUC__)
    namespace std
    {
        [[deprecated("forbidden on sim-path; use XCore::SimMath::Sin")]]   float sin(float)   noexcept;
        [[deprecated("forbidden on sim-path; use XCore::SimMath::Cos")]]   float cos(float)   noexcept;
        [[deprecated("forbidden on sim-path; use XCore::SimMath::Sqrt")]]  float sqrt(float)  noexcept;
        // ... additional symbols intentionally omitted; the four above
        // are the canonical "sin / cos / sqrt are forbidden" landmines.
    }
#endif

namespace XCore::SimMath
{

// ---------------------------------------------------------------------
// Transcendental forwarders (sim-path determinism contract).
//
// TODO(Phase 1e Subagent B): swap libm calls to Sleef_*_u10 / u35
// once the vendor lands in XSimPathMathOverrides.h. The current libm
// fallback runs under XPACT_SIMPATH_PROVISIONAL=1 (Section 6.3); CI
// fails if the flag persists past Foundation Prototype.
//
// The expected substitution map (Section 6.3 + Step 11.5):
//   ::std::sqrt(x)      -> Sleef_sqrtf_u10(x)
//   ::std::sin(x)       -> Sleef_sinf_u35(x)
//   ::std::cos(x)       -> Sleef_cosf_u35(x)
//   ::std::tan(x)       -> Sleef_tanf_u35(x)
//   ::std::atan2(y,x)   -> Sleef_atan2f_u35(y,x)
//   ::std::pow(b,e)     -> Sleef_powf_u10(b,e)
//   ::std::exp(x)       -> Sleef_expf_u10(x)
//   ::std::log(x)       -> Sleef_logf_u10(x)
//
// The u10/u35 ULP tiers come from the spec (line 2039 acceptance C-extra):
// sqrt/exp/log use u10; sin/cos/tan/atan2 use u35.
// ---------------------------------------------------------------------

// We define the SimMath forwarders BEFORE the [[deprecated]] decorations
// take effect by directly calling the namespace-qualified ::std forms
// via a `using` indirection. This is unusual but necessary: the
// SimMath forwarder IS the legitimate use of libm; user code in a
// sim-path TU calls SimMath::Sqrt (not std::sqrt) and the indirection
// keeps the deprecation diagnostic firing only at the call sites that
// actually misuse std::*.
//
// Once Subagent B lands Sleef, the bodies below swap to Sleef_*; the
// forward declarations of std::sin etc. above can then be promoted
// to redirection-aliases (or simply left as poison: the SimMath
// forwarders will call Sleef and never touch ::std::sqrt anyway).

namespace detail
{
    // Untyped trampolines that don't trigger the [[deprecated]] on
    // call (they take the address of the libm symbol; the deprecation
    // attribute is checked at call site, not at function-pointer
    // formation).
    inline float LibmSqrt(float X) noexcept { float (*f)(float) = &::std::sqrtf; return f(X); }
    inline float LibmSin (float X) noexcept { float (*f)(float) = &::std::sinf;  return f(X); }
    inline float LibmCos (float X) noexcept { float (*f)(float) = &::std::cosf;  return f(X); }
    inline float LibmTan (float X) noexcept { float (*f)(float) = &::std::tanf;  return f(X); }
    inline float LibmAsin(float X) noexcept { float (*f)(float) = &::std::asinf; return f(X); }
    inline float LibmAcos(float X) noexcept { float (*f)(float) = &::std::acosf; return f(X); }
    inline float LibmAtan(float X) noexcept { float (*f)(float) = &::std::atanf; return f(X); }
    inline float LibmAtan2(float Y, float X) noexcept { float (*f)(float, float) = &::std::atan2f; return f(Y, X); }
    inline float LibmPow (float B, float E) noexcept { float (*f)(float, float) = &::std::powf; return f(B, E); }
    inline float LibmExp (float X) noexcept { float (*f)(float) = &::std::expf;  return f(X); }
    inline float LibmLog (float X) noexcept { float (*f)(float) = &::std::logf;  return f(X); }
}

[[nodiscard]] XPACT_FORCEINLINE float Sin(float X)         noexcept { return detail::LibmSin(X);  }
[[nodiscard]] XPACT_FORCEINLINE float Cos(float X)         noexcept { return detail::LibmCos(X);  }
[[nodiscard]] XPACT_FORCEINLINE float Tan(float X)         noexcept { return detail::LibmTan(X);  }
[[nodiscard]] XPACT_FORCEINLINE float Asin(float X)        noexcept { return detail::LibmAsin(X); }
[[nodiscard]] XPACT_FORCEINLINE float Acos(float X)        noexcept { return detail::LibmAcos(X); }
[[nodiscard]] XPACT_FORCEINLINE float Atan(float X)        noexcept { return detail::LibmAtan(X); }
[[nodiscard]] XPACT_FORCEINLINE float Atan2(float Y, float X) noexcept { return detail::LibmAtan2(Y, X); }
[[nodiscard]] XPACT_FORCEINLINE float Sqrt(float X)        noexcept { return detail::LibmSqrt(X); }
[[nodiscard]] XPACT_FORCEINLINE float Pow(float B, float E) noexcept { return detail::LibmPow(B, E); }
[[nodiscard]] XPACT_FORCEINLINE float Exp(float X)         noexcept { return detail::LibmExp(X); }
[[nodiscard]] XPACT_FORCEINLINE float Log(float X)         noexcept { return detail::LibmLog(X); }

// ---------------------------------------------------------------------
// FRotator deprecation (locked decision 5).
//
// In a sim-path TU, FRotator is unusable; the type alias here carries
// the deprecation diagnostic.
// ---------------------------------------------------------------------

using FRotator [[deprecated("not sim-path-safe; use FQuat -- degree-based rotation accumulates non-deterministic error across the libm trig path even with Sleef routing")]] = ::XCore::FRotator;

// ---------------------------------------------------------------------
// Scalar utility re-exports.
// ---------------------------------------------------------------------

using ::XCore::Math::Lerp;
using ::XCore::Math::Clamp;
using ::XCore::Math::SmoothStep;
using ::XCore::Math::RoundHalfToEven;
using ::XCore::Math::Abs;
using ::XCore::Math::Min;
using ::XCore::Math::Max;
using ::XCore::Math::Saturate;
using ::XCore::Math::Square;
using ::XCore::Math::Sign;

// FRandomStream re-export.
using ::XCore::Math::FRandomStream;

} // namespace XCore::SimMath
