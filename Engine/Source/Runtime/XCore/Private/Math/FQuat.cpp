// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FQuat.cpp -- Slerp body (numerically sensitive; not inline-friendly).
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1.
//
// Slerp is out-of-line because:
//   1. The body has multiple branches (short-arc selection; small-
//      angle linear-fallback) that don't inline cleanly.
//   2. It calls multiple transcendentals (sin, acos); the inlined
//      form bloats every call site.
//   3. The numerical stability tuning (the 1.0 - eps tolerance for
//      small-angle linear fallback) is the lone correctness-critical
//      logic in the math surface; centralising it in one TU makes
//      future tuning auditable.
//
// =====================================================================

#include "Math/FQuat.h"

#include <cmath>

namespace XCore
{

FQuat FQuat::Slerp(const FQuat& A, const FQuat& B, float Alpha) noexcept
{
    // Compute the cosine of the angle between A and B via the
    // 4-component dot product. If negative, take the short-arc
    // path by negating B (this is the canonical "shortest path"
    // selection).
    float Cos = A.X * B.X + A.Y * B.Y + A.Z * B.Z + A.W * B.W;
    FQuat End = B;
    if (Cos < 0.0f)
    {
        Cos = -Cos;
        End = FQuat{ -B.X, -B.Y, -B.Z, -B.W };
    }

    // If the quaternions are nearly parallel, fall back to linear
    // interpolation; sin(theta) -> 0 and the geodesic form
    // becomes numerically unstable.
    if (Cos > 0.9995f)
    {
        // NLerp.
        FQuat R{
            A.X + Alpha * (End.X - A.X),
            A.Y + Alpha * (End.Y - A.Y),
            A.Z + Alpha * (End.Z - A.Z),
            A.W + Alpha * (End.W - A.W)
        };
        const float Mag = ::std::sqrt(R.X * R.X + R.Y * R.Y + R.Z * R.Z + R.W * R.W);
        if (Mag < 1.0e-8f) { return FQuat::Identity(); }
        const float Inv = 1.0f / Mag;
        return FQuat{ R.X * Inv, R.Y * Inv, R.Z * Inv, R.W * Inv };
    }

    // Standard slerp formula:
    //   q(t) = (sin((1 - t) * theta) * A + sin(t * theta) * B) / sin(theta)
    const float Theta = ::std::acos(Cos);
    const float SinTheta = ::std::sin(Theta);
    const float InvSinTheta = 1.0f / SinTheta;

    const float ScaleA = ::std::sin((1.0f - Alpha) * Theta) * InvSinTheta;
    const float ScaleB = ::std::sin(Alpha * Theta) * InvSinTheta;

    return FQuat{
        ScaleA * A.X + ScaleB * End.X,
        ScaleA * A.Y + ScaleB * End.Y,
        ScaleA * A.Z + ScaleB * End.Z,
        ScaleA * A.W + ScaleB * End.W
    };
}

} // namespace XCore
