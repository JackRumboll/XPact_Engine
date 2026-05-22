// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XMathScalar.h -- scalar math utilities (Section 6.1.5).
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1.5 (fix B-MIN2). Sim-path-safe-by-
// construction: every utility is a pure arithmetic composition, no
// transcendentals, no platform-libm calls. Sim-path TUs use these
// freely.
//
// All operations are constexpr where the C++20 standard permits.
// RoundHalfToEven is the lone exception: std::nearbyint is constexpr
// only since C++23 + only on certain toolchains, so the function is
// runtime by default; a constexpr-friendly bit-level rebuild is left
// as future work (the only constexpr-evaluated use case is FString
// construction at compile time, which doesn't currently invoke
// RoundHalfToEven).
//
// Note: these utilities live in XCore::Math (the dispatched namespace
// alias to either FastMath or SimMath). They are NOT duplicated in
// each math header; they are defined once here and re-exported by the
// dispatcher (XMath.h).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include <cmath>      // std::nearbyint (RoundHalfToEven)
#include <cfenv>      // FE_TONEAREST (default IEEE-754 round mode)

namespace XCore::Math
{

// ---------------------------------------------------------------------
// Lerp -- linear interpolation. Lerp(A, B, 0) == A; Lerp(A, B, 1) == B.
//
// The Alpha parameter is float (not T) per spec body line 565; the
// vector-overload form ("Lerp(FVector, FVector, float)") lives on the
// vector types themselves. This template handles the scalar case.
// ---------------------------------------------------------------------

template<typename T>
[[nodiscard]] XPACT_FORCEINLINE constexpr T Lerp(T A, T B, float Alpha) noexcept
{
    return static_cast<T>(A + Alpha * (B - A));
}

// ---------------------------------------------------------------------
// Clamp -- pin X to the [Lo, Hi] interval. Half-open boundaries are
// possible by adjusting Lo / Hi but the default is fully closed.
// ---------------------------------------------------------------------

template<typename T>
[[nodiscard]] XPACT_FORCEINLINE constexpr T Clamp(T X, T Lo, T Hi) noexcept
{
    return X < Lo ? Lo : (X > Hi ? Hi : X);
}

// ---------------------------------------------------------------------
// SmoothStep -- Hermite cubic interpolation. Maps X in [Edge0, Edge1]
// to a value in [0, 1] with zero derivative at the endpoints.
// SmoothStep(0.5, between Edge0 and Edge1) -> 0.5.
// Outside the input range: clamped to 0 or 1.
//
//   t = Saturate((X - Edge0) / (Edge1 - Edge0))
//   y = 3t^2 - 2t^3
// ---------------------------------------------------------------------

[[nodiscard]] XPACT_FORCEINLINE constexpr float SmoothStep(float Edge0, float Edge1, float X) noexcept
{
    const float Width = Edge1 - Edge0;
    if (Width == 0.0f) { return X < Edge0 ? 0.0f : 1.0f; }
    const float T0 = (X - Edge0) / Width;
    const float T = T0 < 0.0f ? 0.0f : (T0 > 1.0f ? 1.0f : T0);
    return T * T * (3.0f - 2.0f * T);
}

// ---------------------------------------------------------------------
// RoundHalfToEven -- IEEE-754 round-half-to-even (banker's rounding).
//
// The recommended round mode for sim-path code because it has zero
// bias on uniformly-distributed inputs (vs round-half-up which biases
// toward the upper half).
//
// Implementation: std::nearbyint with FE_TONEAREST (the IEEE-754
// default). The function is NOT constexpr because std::nearbyint is
// not constexpr in C++20 (only C++23). For compile-time round-half-
// to-even use a bit-level rebuild (deferred to Phase 2).
// ---------------------------------------------------------------------

[[nodiscard]] XPACT_FORCEINLINE float RoundHalfToEven(float X) noexcept
{
    return ::std::nearbyint(X);
}

[[nodiscard]] XPACT_FORCEINLINE double RoundHalfToEven(double X) noexcept
{
    return ::std::nearbyint(X);
}

// ---------------------------------------------------------------------
// Abs -- magnitude.
//
// constexpr-compatible (the std::abs constexpr-ization is post-C++20
// in many implementations; we build our own constexpr branch).
// ---------------------------------------------------------------------

template<typename T>
[[nodiscard]] XPACT_FORCEINLINE constexpr T Abs(T X) noexcept
{
    return X < T(0) ? -X : X;
}

// ---------------------------------------------------------------------
// Min / Max.
// ---------------------------------------------------------------------

template<typename T>
[[nodiscard]] XPACT_FORCEINLINE constexpr T Min(T A, T B) noexcept
{
    return A < B ? A : B;
}

template<typename T>
[[nodiscard]] XPACT_FORCEINLINE constexpr T Max(T A, T B) noexcept
{
    return A > B ? A : B;
}

// ---------------------------------------------------------------------
// Saturate -- Clamp(X, 0, 1) (float-specific shortcut).
// ---------------------------------------------------------------------

[[nodiscard]] XPACT_FORCEINLINE constexpr float Saturate(float X) noexcept
{
    return X < 0.0f ? 0.0f : (X > 1.0f ? 1.0f : X);
}

[[nodiscard]] XPACT_FORCEINLINE constexpr double Saturate(double X) noexcept
{
    return X < 0.0 ? 0.0 : (X > 1.0 ? 1.0 : X);
}

// ---------------------------------------------------------------------
// Square -- X * X.
// ---------------------------------------------------------------------

template<typename T>
[[nodiscard]] XPACT_FORCEINLINE constexpr T Square(T X) noexcept
{
    return X * X;
}

// ---------------------------------------------------------------------
// Sign -- -1 / 0 / +1 indicator. Returns int (not T) per the
// canonical signum-function convention; zero returns 0 (not NaN).
// ---------------------------------------------------------------------

template<typename T>
[[nodiscard]] XPACT_FORCEINLINE constexpr int Sign(T X) noexcept
{
    return (T(0) < X) - (X < T(0));
}

} // namespace XCore::Math
