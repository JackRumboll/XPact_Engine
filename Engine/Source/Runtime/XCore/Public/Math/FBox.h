// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FBox.h -- axis-aligned bounding box (AABB).
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1.
//
// LAYOUT:
//   FVector Min   (12 bytes; 4-byte aligned)
//   FVector Max   (12 bytes; 4-byte aligned)
//   ----
//   24 bytes total; 4-byte aligned.
//
// An AABB is "empty" if Min > Max on any axis; an empty box contains
// no points and intersects no other box. The default constructor
// produces an empty box (Min = +inf, Max = -inf) so that Expand on
// the first point produces a degenerate-but-valid single-point box
// (Min == Max).
//
// SIM-PATH SAFETY:
//   Pure arithmetic; sim-path-safe.
//
// UE REFERENCES:
//   Engine/Source/Runtime/Core/Public/Math/Box.h -- studied.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Math/FVector.h"

#include <limits>

namespace XCore
{

struct FBox
{
    FVector Min;
    FVector Max;

    // -----------------------------------------------------------------
    // Default constructor: empty box (Min = +inf, Max = -inf). The
    // first Expand call sets Min = Max = first point, the second
    // Expand call grows the box, etc.
    // -----------------------------------------------------------------

    XPACT_FORCEINLINE FBox() noexcept
        : Min(::std::numeric_limits<float>::infinity(),
              ::std::numeric_limits<float>::infinity(),
              ::std::numeric_limits<float>::infinity())
        , Max(-::std::numeric_limits<float>::infinity(),
              -::std::numeric_limits<float>::infinity(),
              -::std::numeric_limits<float>::infinity())
    {}

    XPACT_FORCEINLINE constexpr FBox(const FVector& InMin, const FVector& InMax) noexcept
        : Min(InMin), Max(InMax)
    {}

    // -----------------------------------------------------------------
    // IsValid -- box is non-empty (Min <= Max on every axis).
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr bool IsValid() const noexcept
    {
        return Min.X <= Max.X && Min.Y <= Max.Y && Min.Z <= Max.Z;
    }

    // -----------------------------------------------------------------
    // Center / Extent / Size.
    //
    //   Extent  -- half-the-size; "radius" of the box along each axis.
    //   Size    -- full size; Max - Min.
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr FVector GetCenter() const noexcept
    {
        return FVector{
            (Min.X + Max.X) * 0.5f,
            (Min.Y + Max.Y) * 0.5f,
            (Min.Z + Max.Z) * 0.5f
        };
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr FVector GetExtent() const noexcept
    {
        return FVector{
            (Max.X - Min.X) * 0.5f,
            (Max.Y - Min.Y) * 0.5f,
            (Max.Z - Min.Z) * 0.5f
        };
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr FVector GetSize() const noexcept
    {
        return Max - Min;
    }

    // -----------------------------------------------------------------
    // Contains -- is point inside? Inclusive at Min, inclusive at Max.
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr bool Contains(const FVector& P) const noexcept
    {
        return P.X >= Min.X && P.X <= Max.X
            && P.Y >= Min.Y && P.Y <= Max.Y
            && P.Z >= Min.Z && P.Z <= Max.Z;
    }

    // -----------------------------------------------------------------
    // Intersect -- do two boxes overlap? Returns true if the
    // intersection has nonzero volume (or is touching at a face / edge
    // / corner; the inclusive boundary matches Contains).
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr bool Intersect(const FBox& B) const noexcept
    {
        return Min.X <= B.Max.X && Max.X >= B.Min.X
            && Min.Y <= B.Max.Y && Max.Y >= B.Min.Y
            && Min.Z <= B.Max.Z && Max.Z >= B.Min.Z;
    }

    // -----------------------------------------------------------------
    // Expand to include a point or another box.
    // -----------------------------------------------------------------

    XPACT_FORCEINLINE constexpr FBox& Expand(const FVector& P) noexcept
    {
        if (P.X < Min.X) Min.X = P.X;
        if (P.Y < Min.Y) Min.Y = P.Y;
        if (P.Z < Min.Z) Min.Z = P.Z;
        if (P.X > Max.X) Max.X = P.X;
        if (P.Y > Max.Y) Max.Y = P.Y;
        if (P.Z > Max.Z) Max.Z = P.Z;
        return *this;
    }

    XPACT_FORCEINLINE constexpr FBox& Expand(const FBox& Other) noexcept
    {
        Expand(Other.Min);
        Expand(Other.Max);
        return *this;
    }
};

} // namespace XCore
