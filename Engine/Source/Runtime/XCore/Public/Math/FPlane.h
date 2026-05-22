// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FPlane.h -- 4-element plane (normal + W).
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1.
//
// REPRESENTATION:
//   The plane equation is  ax + by + cz - d = 0
//   stored as  (Normal.X, Normal.Y, Normal.Z, W)  where
//   W is the SIGNED DISTANCE from the origin to the plane (along the
//   positive normal). The plane equation in component form is then
//       Normal . P = W
//   for any point P on the plane.
//
//   Note: this is the +W (signed-distance) form, NOT the -d form. The
//   sign convention matches DirectXMath; UE uses the same convention.
//   The prompt's "ax + by + cz + d = 0 where d = -W" wording also
//   resolves to the same algebra (d == -W means N.P + d == 0  ==
//   N.P - W == 0  ==  N.P == W).
//
// LAYOUT:
//   FVector Normal  (12 bytes; 4-byte aligned)
//   float   W       ( 4 bytes; 4-byte aligned)
//   ----
//   16 bytes total; 4-byte aligned.
//
// SIM-PATH SAFETY:
//   Pure arithmetic; sim-path-safe. The plane assumes a unit normal
//   (the caller is responsible; the type does not normalise
//   automatically).
//
// UE REFERENCES:
//   Engine/Source/Runtime/Core/Public/Math/Plane.h -- studied.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Math/FVector.h"

namespace XCore
{

struct FPlane
{
    FVector Normal;
    float W;

    XPACT_FORCEINLINE constexpr FPlane() noexcept : Normal(0.0f, 0.0f, 1.0f), W(0.0f) {}
    XPACT_FORCEINLINE constexpr FPlane(const FVector& InNormal, float InW) noexcept
        : Normal(InNormal), W(InW) {}
    XPACT_FORCEINLINE constexpr FPlane(float A, float B, float C, float InW) noexcept
        : Normal(A, B, C), W(InW) {}

    // -----------------------------------------------------------------
    // FromPointNormal -- build a plane from a point and a unit normal.
    //
    //   W = Normal . PointOnPlane
    // -----------------------------------------------------------------

    [[nodiscard]] static XPACT_FORCEINLINE constexpr FPlane FromPointNormal(const FVector& P, const FVector& N) noexcept
    {
        return FPlane{ N, N.Dot(P) };
    }

    // -----------------------------------------------------------------
    // PlaneDot -- signed distance from a point to the plane along the
    // positive normal. Negative if P is on the back side of the plane.
    //
    //   PlaneDot(P) = Normal . P - W
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr float PlaneDot(const FVector& P) const noexcept
    {
        return Normal.Dot(P) - W;
    }
};

} // namespace XCore
