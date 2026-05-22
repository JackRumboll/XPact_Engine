// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FSphere.h -- center + radius bounding sphere.
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1.
//
// LAYOUT:
//   FVector Center  (12 bytes; 4-byte aligned)
//   float   Radius  ( 4 bytes; 4-byte aligned)
//   ----
//   16 bytes total; 4-byte aligned.
//
// The sphere is "degenerate" if Radius < 0 (zero-radius sphere is
// valid and contains exactly the center point).
//
// SIM-PATH SAFETY:
//   Pure arithmetic except for Intersect's sqrt-free distance test;
//   sim-path-safe.
//
// UE REFERENCES:
//   Engine/Source/Runtime/Core/Public/Math/Sphere.h -- studied.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Math/FVector.h"

namespace XCore
{

struct FSphere
{
    FVector Center;
    float Radius;

    XPACT_FORCEINLINE constexpr FSphere() noexcept : Center(0.0f, 0.0f, 0.0f), Radius(0.0f) {}
    XPACT_FORCEINLINE constexpr FSphere(const FVector& InCenter, float InRadius) noexcept
        : Center(InCenter), Radius(InRadius) {}

    // -----------------------------------------------------------------
    // Contains -- is point inside the sphere?
    //
    // Uses sqrt-free distance comparison (LengthSq vs Radius^2) so the
    // operation is fully sim-path-safe.
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr bool Contains(const FVector& P) const noexcept
    {
        return (P - Center).LengthSq() <= Radius * Radius;
    }

    // -----------------------------------------------------------------
    // Intersect -- do two spheres overlap?
    //
    // (LengthSq(A.Center - B.Center) <= (A.Radius + B.Radius)^2)
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr bool Intersect(const FSphere& B) const noexcept
    {
        const float SumRadius = Radius + B.Radius;
        return (Center - B.Center).LengthSq() <= SumRadius * SumRadius;
    }
};

} // namespace XCore
