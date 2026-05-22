// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FVector4.h -- 4D float vector with SIMD alignment (Section 6.1).
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1 (Public API -- math types).
//
// LAYOUT (locked at sizeof == 16, alignof == 16):
//   bytes 0-3    X  (float)
//   bytes 4-7    Y  (float)
//   bytes 8-11   Z  (float)
//   bytes 12-15  W  (float)
//
// 16-byte alignment is the SIMD-load-friendly layout (one
// _mm_load_ps on x86; vld1q_f32 on ARM). The C# mirror is
// [StructLayout(LayoutKind.Sequential, Pack=16)] (fix M-17 C# binding).
//
// Used for homogeneous coordinates (positions with w=1 in projective
// transforms; directions with w=0), generic 4-tuples (RGBA when not
// using FLinearColor), and as the canonical "single SIMD register"
// type for hot-loop math.
//
// SIM-PATH SAFETY:
//   All arithmetic is bit-exact; Length() routes through sqrt (same
//   Sleef-routing rules as FVector::Length).
//
// UE REFERENCES:
//   Engine/Source/Runtime/Core/Public/Math/Vector4.h -- studied.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include <cmath>
#include <cstddef>

namespace XCore
{

struct alignas(16) FVector4
{
    float X;
    float Y;
    float Z;
    float W;

    XPACT_FORCEINLINE constexpr FVector4() noexcept : X(0.0f), Y(0.0f), Z(0.0f), W(0.0f) {}
    XPACT_FORCEINLINE constexpr FVector4(float InX, float InY, float InZ, float InW) noexcept
        : X(InX), Y(InY), Z(InZ), W(InW) {}
    XPACT_FORCEINLINE explicit constexpr FVector4(float InScalar) noexcept
        : X(InScalar), Y(InScalar), Z(InScalar), W(InScalar) {}

    XPACT_FORCEINLINE constexpr FVector4 operator+(const FVector4& B) const noexcept
    {
        return FVector4{ X + B.X, Y + B.Y, Z + B.Z, W + B.W };
    }

    XPACT_FORCEINLINE constexpr FVector4 operator-(const FVector4& B) const noexcept
    {
        return FVector4{ X - B.X, Y - B.Y, Z - B.Z, W - B.W };
    }

    XPACT_FORCEINLINE constexpr FVector4 operator*(float Scalar) const noexcept
    {
        return FVector4{ X * Scalar, Y * Scalar, Z * Scalar, W * Scalar };
    }

    XPACT_FORCEINLINE constexpr FVector4 operator/(float Scalar) const noexcept
    {
        const float Inv = 1.0f / Scalar;
        return FVector4{ X * Inv, Y * Inv, Z * Inv, W * Inv };
    }

    XPACT_FORCEINLINE constexpr FVector4 operator-() const noexcept
    {
        return FVector4{ -X, -Y, -Z, -W };
    }

    XPACT_FORCEINLINE constexpr FVector4& operator+=(const FVector4& B) noexcept
    {
        X += B.X; Y += B.Y; Z += B.Z; W += B.W; return *this;
    }

    XPACT_FORCEINLINE constexpr FVector4& operator-=(const FVector4& B) noexcept
    {
        X -= B.X; Y -= B.Y; Z -= B.Z; W -= B.W; return *this;
    }

    XPACT_FORCEINLINE constexpr FVector4& operator*=(float Scalar) noexcept
    {
        X *= Scalar; Y *= Scalar; Z *= Scalar; W *= Scalar; return *this;
    }

    XPACT_FORCEINLINE constexpr bool operator==(const FVector4& B) const noexcept
    {
        return X == B.X && Y == B.Y && Z == B.Z && W == B.W;
    }

    XPACT_FORCEINLINE constexpr bool operator!=(const FVector4& B) const noexcept { return !(*this == B); }

    XPACT_FORCEINLINE constexpr float& operator[](::std::size_t Index) noexcept { return (&X)[Index]; }
    XPACT_FORCEINLINE constexpr float operator[](::std::size_t Index) const noexcept { return (&X)[Index]; }

    [[nodiscard]] XPACT_FORCEINLINE constexpr float Dot(const FVector4& B) const noexcept
    {
        return X * B.X + Y * B.Y + Z * B.Z + W * B.W;
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr float LengthSq() const noexcept
    {
        return X * X + Y * Y + Z * Z + W * W;
    }

    [[nodiscard]] XPACT_FORCEINLINE float Length() const noexcept { return ::std::sqrt(LengthSq()); }

    [[nodiscard]] XPACT_FORCEINLINE FVector4 GetSafeNormal(float Tolerance = 1.0e-8f) const noexcept
    {
        const float SqMag = LengthSq();
        if (SqMag < Tolerance) { return FVector4{ 0.0f, 0.0f, 0.0f, 0.0f }; }
        const float InvMag = 1.0f / ::std::sqrt(SqMag);
        return FVector4{ X * InvMag, Y * InvMag, Z * InvMag, W * InvMag };
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

    [[nodiscard]] static XPACT_FORCEINLINE constexpr FVector4 Lerp(const FVector4& A, const FVector4& B, float Alpha) noexcept
    {
        return FVector4{
            A.X + Alpha * (B.X - A.X),
            A.Y + Alpha * (B.Y - A.Y),
            A.Z + Alpha * (B.Z - A.Z),
            A.W + Alpha * (B.W - A.W)
        };
    }
};

XPACT_FORCEINLINE constexpr FVector4 operator*(float Scalar, const FVector4& V) noexcept
{
    return FVector4{ Scalar * V.X, Scalar * V.Y, Scalar * V.Z, Scalar * V.W };
}

inline constexpr FVector4 ZeroVector4 { 0.0f, 0.0f, 0.0f, 0.0f };

static_assert(sizeof(FVector4)  == 16, "FVector4 ABI lock: 16 bytes (SIMD-aligned 4-float)");
static_assert(alignof(FVector4) == 16, "FVector4 ABI lock: 16-byte aligned");

} // namespace XCore
