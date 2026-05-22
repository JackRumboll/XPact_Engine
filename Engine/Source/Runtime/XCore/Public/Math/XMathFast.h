// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XMathFast.h -- non-sim-path math namespace.
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1 + locked decision 4 (two-header dispatch).
//
// This header is the "fast" half of the two-header model:
//   * libm is permitted (sin, cos, tan, atan2, sqrt, pow, exp, log
//     all route to <cmath>).
//   * FMA is permitted (compiler may emit fmla / vfmadd*).
//   * /fp:fast is permitted (MSVC) / -ffast-math is permitted (Clang/GCC).
//
// Used by the renderer, UI, audio, and any other non-sim-path TU.
// Cannot be included in a sim-path TU; the sim-path TU must include
// XSimMath.h, which decorates poisoned libm symbols with [[deprecated]].
//
// The header re-exports every math type (so a single
//     #include "Math/XMathFast.h"
// pulls in FVector / FQuat / FMatrix / etc.) and defines the
// XCore::FastMath namespace with the transcendental forwarders.
//
// SCALAR MATH UTILITIES (Section 6.1.5):
//   The scalar utilities (Lerp / Clamp / SmoothStep / RoundHalfToEven /
//   Abs / Min / Max / Saturate / Square / Sign) live in XMathScalar.h
//   and are pulled in via the include below. They are sim-path-safe
//   by construction and are equally usable from XMathFast and
//   XSimMath; no duplication.
//
// FRANDOMSTREAM (Section 6.1.6):
//   FRandomStream lives in FRandomStream.h and is sim-path-safe by
//   construction.
//
// =====================================================================

// Type headers (the math primitive types).
#include "Math/FVector.h"
#include "Math/FVector2D.h"
#include "Math/FVector4.h"
#include "Math/FLargeWorldVector.h"
#include "Math/FQuat.h"
#include "Math/FMatrix.h"
#include "Math/FTransform.h"
#include "Math/FRotator.h"
#include "Math/FBox.h"
#include "Math/FSphere.h"
#include "Math/FPlane.h"
#include "Math/FLinearColor.h"
#include "Math/FColor.h"

// Scalar utilities + FRandomStream (sim-path-safe-by-construction).
#include "Math/XMathScalar.h"
#include "Math/FRandomStream.h"

#include <cmath>

namespace XCore::FastMath
{

// ---------------------------------------------------------------------
// Transcendental forwarders.
//
// These route directly to libm via <cmath>. The compiler is free to
// substitute SSE/AVX-vectorised approximations under /fp:fast or
// -ffast-math.
//
// Used by every non-sim-path TU. Sim-path TUs include XSimMath.h
// (NOT this header) and call the same-named functions in the
// XCore::SimMath namespace, which route through Sleef.
// ---------------------------------------------------------------------

[[nodiscard]] XPACT_FORCEINLINE float Sin(float X)         noexcept { return ::std::sin(X); }
[[nodiscard]] XPACT_FORCEINLINE float Cos(float X)         noexcept { return ::std::cos(X); }
[[nodiscard]] XPACT_FORCEINLINE float Tan(float X)         noexcept { return ::std::tan(X); }
[[nodiscard]] XPACT_FORCEINLINE float Asin(float X)        noexcept { return ::std::asin(X); }
[[nodiscard]] XPACT_FORCEINLINE float Acos(float X)        noexcept { return ::std::acos(X); }
[[nodiscard]] XPACT_FORCEINLINE float Atan(float X)        noexcept { return ::std::atan(X); }
[[nodiscard]] XPACT_FORCEINLINE float Atan2(float Y, float X) noexcept { return ::std::atan2(Y, X); }
[[nodiscard]] XPACT_FORCEINLINE float Sqrt(float X)        noexcept { return ::std::sqrt(X); }
[[nodiscard]] XPACT_FORCEINLINE float Pow(float B, float E) noexcept { return ::std::pow(B, E); }
[[nodiscard]] XPACT_FORCEINLINE float Exp(float X)         noexcept { return ::std::exp(X); }
[[nodiscard]] XPACT_FORCEINLINE float Log(float X)         noexcept { return ::std::log(X); }
[[nodiscard]] XPACT_FORCEINLINE float Log2(float X)        noexcept { return ::std::log2(X); }
[[nodiscard]] XPACT_FORCEINLINE float Floor(float X)       noexcept { return ::std::floor(X); }
[[nodiscard]] XPACT_FORCEINLINE float Ceil(float X)        noexcept { return ::std::ceil(X); }

// Double-precision forwarders for the rare consumer (FLargeWorldVector
// math, double-precision physics integrators).
[[nodiscard]] XPACT_FORCEINLINE double Sin(double X)       noexcept { return ::std::sin(X); }
[[nodiscard]] XPACT_FORCEINLINE double Cos(double X)       noexcept { return ::std::cos(X); }
[[nodiscard]] XPACT_FORCEINLINE double Sqrt(double X)      noexcept { return ::std::sqrt(X); }
[[nodiscard]] XPACT_FORCEINLINE double Pow(double B, double E) noexcept { return ::std::pow(B, E); }

// ---------------------------------------------------------------------
// Scalar utility re-exports.
//
// The scalar utilities live in XCore::Math (the dispatcher namespace).
// Inside XCore::FastMath we re-export them via using-declarations so
// `XCore::Math::Clamp(...)` (in non-sim-path TUs) resolves correctly.
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

// FRandomStream re-export (lives in XCore::Math; the type itself is
// sim-path-safe so it is the same type in both namespaces).
using ::XCore::Math::FRandomStream;

} // namespace XCore::FastMath
