// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FVector2D.h -- 2D float vector (Section 6.1).
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1 (Public API -- math types).
//
// LAYOUT (locked at sizeof == 8, alignof == 4):
//   bytes 0-3   X  (float)
//   bytes 4-7   Y  (float)
//
// Used pervasively for UI coordinates, texture UVs, and 2D math
// (screen-space pathfinding, 2D collision, etc.). No Cross product
// (the 2D cross is a scalar, exposed via the free function
// `XCore::Math::Cross2D` rather than a member -- avoids the
// "FVector::Cross is a vector, FVector2D::Cross is a scalar"
// ergonomic landmine).
//
// SIM-PATH SAFETY:
//   All arithmetic is bit-exact; Length() routes through sqrt (same
//   Sleef-routing rules as FVector::Length).
//
// UE REFERENCES:
//   Engine/Source/Runtime/Core/Public/Math/Vector2D.h  -- studied
//   (not copied; same divergences as FVector).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include <cmath>
#include <cstddef>

namespace XCore
{

struct alignas(4) FVector2D
{
    float X;
    float Y;

    XPACT_FORCEINLINE constexpr FVector2D() noexcept : X(0.0f), Y(0.0f) {}
    XPACT_FORCEINLINE constexpr FVector2D(float InX, float InY) noexcept : X(InX), Y(InY) {}
    XPACT_FORCEINLINE explicit constexpr FVector2D(float InScalar) noexcept : X(InScalar), Y(InScalar) {}

    XPACT_FORCEINLINE constexpr FVector2D operator+(const FVector2D& B) const noexcept
    {
        return FVector2D{ X + B.X, Y + B.Y };
    }

    XPACT_FORCEINLINE constexpr FVector2D operator-(const FVector2D& B) const noexcept
    {
        return FVector2D{ X - B.X, Y - B.Y };
    }

    XPACT_FORCEINLINE constexpr FVector2D operator*(float Scalar) const noexcept
    {
        return FVector2D{ X * Scalar, Y * Scalar };
    }

    XPACT_FORCEINLINE constexpr FVector2D operator/(float Scalar) const noexcept
    {
        const float Inv = 1.0f / Scalar;
        return FVector2D{ X * Inv, Y * Inv };
    }

    XPACT_FORCEINLINE constexpr FVector2D operator-() const noexcept
    {
        return FVector2D{ -X, -Y };
    }

    XPACT_FORCEINLINE constexpr FVector2D& operator+=(const FVector2D& B) noexcept
    {
        X += B.X; Y += B.Y; return *this;
    }

    XPACT_FORCEINLINE constexpr FVector2D& operator-=(const FVector2D& B) noexcept
    {
        X -= B.X; Y -= B.Y; return *this;
    }

    XPACT_FORCEINLINE constexpr FVector2D& operator*=(float Scalar) noexcept
    {
        X *= Scalar; Y *= Scalar; return *this;
    }

    XPACT_FORCEINLINE constexpr bool operator==(const FVector2D& B) const noexcept
    {
        return X == B.X && Y == B.Y;
    }

    XPACT_FORCEINLINE constexpr bool operator!=(const FVector2D& B) const noexcept { return !(*this == B); }

    XPACT_FORCEINLINE constexpr float& operator[](::std::size_t Index) noexcept { return (&X)[Index]; }
    XPACT_FORCEINLINE constexpr float operator[](::std::size_t Index) const noexcept { return (&X)[Index]; }

    [[nodiscard]] XPACT_FORCEINLINE constexpr float Dot(const FVector2D& B) const noexcept
    {
        return X * B.X + Y * B.Y;
    }

    // 2D "cross" returns a scalar (the signed area of the parallelogram).
    [[nodiscard]] XPACT_FORCEINLINE constexpr float CrossScalar(const FVector2D& B) const noexcept
    {
        return X * B.Y - Y * B.X;
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr float LengthSq() const noexcept { return X * X + Y * Y; }
    [[nodiscard]] XPACT_FORCEINLINE float Length() const noexcept { return ::std::sqrt(LengthSq()); }

    [[nodiscard]] XPACT_FORCEINLINE FVector2D GetSafeNormal(float Tolerance = 1.0e-8f) const noexcept
    {
        const float SqMag = LengthSq();
        if (SqMag < Tolerance) { return FVector2D{ 0.0f, 0.0f }; }
        const float InvMag = 1.0f / ::std::sqrt(SqMag);
        return FVector2D{ X * InvMag, Y * InvMag };
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr bool IsNormalized(float Tolerance = 1.0e-4f) const noexcept
    {
        const float SqMag = LengthSq();
        const float Diff  = SqMag - 1.0f;
        return (Diff < 0.0f ? -Diff : Diff) < Tolerance;
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr bool IsNearlyZero(float Tolerance = 1.0e-8f) const noexcept
    {
        return LengthSq() < Tolerance;
    }

    [[nodiscard]] static XPACT_FORCEINLINE constexpr FVector2D Lerp(const FVector2D& A, const FVector2D& B, float Alpha) noexcept
    {
        return FVector2D{ A.X + Alpha * (B.X - A.X), A.Y + Alpha * (B.Y - A.Y) };
    }
};

XPACT_FORCEINLINE constexpr FVector2D operator*(float Scalar, const FVector2D& V) noexcept
{
    return FVector2D{ Scalar * V.X, Scalar * V.Y };
}

inline constexpr FVector2D ZeroVector2D { 0.0f, 0.0f };
inline constexpr FVector2D OneVector2D  { 1.0f, 1.0f };

static_assert(sizeof(FVector2D)  == 8, "FVector2D ABI lock: 8 bytes");
static_assert(alignof(FVector2D) == 4, "FVector2D ABI lock: 4-byte aligned");

} // namespace XCore
