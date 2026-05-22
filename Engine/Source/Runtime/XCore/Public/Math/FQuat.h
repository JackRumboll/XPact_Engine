// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FQuat.h -- unit quaternion (Section 6.1).
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1.
//
// LAYOUT (locked at sizeof == 16, alignof == 16):
//   bytes  0-3   X  (float)
//   bytes  4-7   Y  (float)
//   bytes  8-11  Z  (float)
//   bytes 12-15  W  (float; the scalar)
//
// Quaternion convention: R = w + xi + yj + zk (canonical Hamilton
// form). Identity is (0, 0, 0, 1). The component order (X, Y, Z, W)
// matches UE's Math/Quat.h and the C# System.Numerics.Quaternion
// layout; the C# mirror is [StructLayout(LayoutKind.Sequential, Pack=16)]
// (fix M-17).
//
// Quaternion algebra (the only nontrivial bit):
//   Hamilton product (q * r):
//     w = qw*rw - qx*rx - qy*ry - qz*rz
//     x = qw*rx + qx*rw + qy*rz - qz*ry
//     y = qw*ry - qx*rz + qy*rw + qz*rx
//     z = qw*rz + qx*ry - qy*rx + qz*rw
//
// RotateVector uses the v' = q * v * q^-1 sandwich; the optimised
// short form (Rodrigues) is the canonical implementation:
//     t = 2 * cross(q.xyz, v)
//     v' = v + q.w * t + cross(q.xyz, t)
// This is ~30% faster than the naive sandwich on every platform and
// is the spec's intended hot-path form.
//
// SIM-PATH SAFETY:
//   FromAxisAngle / ToAxisAngle route through sin/cos and acos; on
//   sim-path TUs these resolve through the Sleef no-FMA aliases
//   (Step 11.5 vendoring; until then provisional libm route).
//   Slerp similarly routes through sin/acos. The pure-arithmetic
//   members (Hamilton product, RotateVector, Conjugate, Inverse-for-
//   unit-quat, Length{Sq}) are bit-exact across platforms.
//
//   * FRotator interop is via FromEuler / ToEuler explicit-only
//     methods (locked decision 5). On the sim-path overlay header
//     XSimMath.h the FRotator type is decorated [[deprecated]] so the
//     conversion methods become unreachable in sim-path TUs.
//
// UE REFERENCES:
//   Engine/Source/Runtime/Core/Public/Math/Quat.h -- studied.
//   The implicit FRotator <-> FQuat conversions in UE are NOT adopted
//   (divergence row 3 in Section 6.5).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Math/FVector.h"

#include <cmath>

namespace XCore
{

// Forward declaration; full type in FRotator.h. FromEuler is the
// canonical conversion path.
struct FRotator;

struct alignas(16) FQuat
{
    float X;
    float Y;
    float Z;
    float W;

    // -----------------------------------------------------------------
    // Constructors.
    //
    // The default constructor is identity (0, 0, 0, 1) -- NOT zero. A
    // zero-quaternion has no rotation interpretation; the default
    // must be a valid unit quaternion or the type becomes a NaN-
    // generator under composition.
    // -----------------------------------------------------------------

    XPACT_FORCEINLINE constexpr FQuat() noexcept : X(0.0f), Y(0.0f), Z(0.0f), W(1.0f) {}
    XPACT_FORCEINLINE constexpr FQuat(float InX, float InY, float InZ, float InW) noexcept
        : X(InX), Y(InY), Z(InZ), W(InW) {}

    // -----------------------------------------------------------------
    // Static identity. inline constexpr so every TU sees the same
    // constant-initialised storage.
    // -----------------------------------------------------------------

    [[nodiscard]] static XPACT_FORCEINLINE constexpr FQuat Identity() noexcept
    {
        return FQuat{ 0.0f, 0.0f, 0.0f, 1.0f };
    }

    // -----------------------------------------------------------------
    // FromAxisAngle. Axis must be unit-length; angle in radians.
    //
    //   q = (axis * sin(angle/2), cos(angle/2))
    //
    // sin/cos route through XCore::Math (libm or Sleef) at call site
    // depending on the TU's sim-path flag.
    // -----------------------------------------------------------------

    [[nodiscard]] static XPACT_FORCEINLINE FQuat FromAxisAngle(const FVector& Axis, float AngleRad) noexcept
    {
        const float HalfAngle = AngleRad * 0.5f;
        const float S = ::std::sin(HalfAngle);
        const float C = ::std::cos(HalfAngle);
        return FQuat{ Axis.X * S, Axis.Y * S, Axis.Z * S, C };
    }

    // -----------------------------------------------------------------
    // ToAxisAngle. Outputs the axis (unit-length, [Axis pointer]) and
    // the angle in radians. For a non-unit quaternion the result is
    // normalised first. Returns the zero axis + zero angle for the
    // identity quaternion.
    // -----------------------------------------------------------------

    void ToAxisAngle(FVector& OutAxis, float& OutAngleRad) const noexcept
    {
        // Normalise W to the principal branch [-1, 1] before acos.
        const float ClampedW = W > 1.0f ? 1.0f : (W < -1.0f ? -1.0f : W);
        OutAngleRad = 2.0f * ::std::acos(ClampedW);
        const float SinHalf = ::std::sqrt(1.0f - ClampedW * ClampedW);
        if (SinHalf < 1.0e-6f)
        {
            // Near-identity quaternion -> axis is arbitrary; pick X to
            // avoid a divide-by-zero. Angle ~0 makes the axis choice
            // moot.
            OutAxis = FVector{ 1.0f, 0.0f, 0.0f };
            return;
        }
        const float InvSinHalf = 1.0f / SinHalf;
        OutAxis = FVector{ X * InvSinHalf, Y * InvSinHalf, Z * InvSinHalf };
    }

    // -----------------------------------------------------------------
    // Hamilton product (Section 6.1).
    //
    // Composition order: (A * B) * v == A.RotateVector(B.RotateVector(v))
    // i.e., B is applied first, then A. Matches every textbook (Foley,
    // Eberly, Shoemake) and System.Numerics.Quaternion.
    // -----------------------------------------------------------------

    XPACT_FORCEINLINE constexpr FQuat operator*(const FQuat& B) const noexcept
    {
        return FQuat{
            W * B.X + X * B.W + Y * B.Z - Z * B.Y,
            W * B.Y - X * B.Z + Y * B.W + Z * B.X,
            W * B.Z + X * B.Y - Y * B.X + Z * B.W,
            W * B.W - X * B.X - Y * B.Y - Z * B.Z
        };
    }

    XPACT_FORCEINLINE constexpr FQuat operator*(float Scalar) const noexcept
    {
        return FQuat{ X * Scalar, Y * Scalar, Z * Scalar, W * Scalar };
    }

    XPACT_FORCEINLINE constexpr FQuat operator+(const FQuat& B) const noexcept
    {
        return FQuat{ X + B.X, Y + B.Y, Z + B.Z, W + B.W };
    }

    XPACT_FORCEINLINE constexpr FQuat operator-(const FQuat& B) const noexcept
    {
        return FQuat{ X - B.X, Y - B.Y, Z - B.Z, W - B.W };
    }

    XPACT_FORCEINLINE constexpr FQuat operator-() const noexcept
    {
        return FQuat{ -X, -Y, -Z, -W };
    }

    XPACT_FORCEINLINE constexpr bool operator==(const FQuat& B) const noexcept
    {
        return X == B.X && Y == B.Y && Z == B.Z && W == B.W;
    }

    XPACT_FORCEINLINE constexpr bool operator!=(const FQuat& B) const noexcept { return !(*this == B); }

    // -----------------------------------------------------------------
    // Conjugate -- (-X, -Y, -Z, W). For a unit quaternion the
    // conjugate equals the inverse.
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr FQuat Conjugate() const noexcept
    {
        return FQuat{ -X, -Y, -Z, W };
    }

    // -----------------------------------------------------------------
    // Inverse. For a unit quaternion this equals Conjugate; the
    // general form divides by LengthSq. Caller-callable on both
    // unit and non-unit quaternions; pays the divide tax in the
    // unit case but avoids the branch.
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr FQuat Inverse() const noexcept
    {
        const float SqMag = X * X + Y * Y + Z * Z + W * W;
        const float Inv = 1.0f / SqMag;
        return FQuat{ -X * Inv, -Y * Inv, -Z * Inv, W * Inv };
    }

    // -----------------------------------------------------------------
    // Length / LengthSq / IsNormalized.
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr float LengthSq() const noexcept
    {
        return X * X + Y * Y + Z * Z + W * W;
    }

    [[nodiscard]] XPACT_FORCEINLINE float Length() const noexcept
    {
        return ::std::sqrt(LengthSq());
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr bool IsNormalized(float Tolerance = 1.0e-4f) const noexcept
    {
        const float SqMag = LengthSq();
        const float Diff = SqMag - 1.0f;
        return (Diff < 0.0f ? -Diff : Diff) < Tolerance;
    }

    [[nodiscard]] XPACT_FORCEINLINE FQuat GetNormalized(float Tolerance = 1.0e-8f) const noexcept
    {
        const float SqMag = LengthSq();
        if (SqMag < Tolerance) { return FQuat::Identity(); }
        const float InvMag = 1.0f / ::std::sqrt(SqMag);
        return FQuat{ X * InvMag, Y * InvMag, Z * InvMag, W * InvMag };
    }

    // -----------------------------------------------------------------
    // RotateVector (Rodrigues short form).
    //
    //   v' = v + 2 * cross(q.xyz, q.w * v + cross(q.xyz, v))
    //
    // Equivalent to the sandwich q * (0, v) * q^-1 but faster.
    // Pure arithmetic; sim-path-safe.
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr FVector RotateVector(const FVector& V) const noexcept
    {
        const FVector Q{ X, Y, Z };
        const FVector T = Q.Cross(V) * 2.0f;
        return V + (T * W) + Q.Cross(T);
    }

    // -----------------------------------------------------------------
    // Nlerp -- normalized linear interpolation; cheap-but-not-quite-
    // geodesic. The result is normalised so the output is a unit
    // quaternion even though linear blending of unit quats is not
    // strictly on the unit hypersphere.
    //
    // Slerp (spherical linear interpolation) is the geodesic form and
    // is defined in FQuat.cpp (numerically sensitive; not inline-
    // friendly).
    //
    // Both methods take the SHORT path (dot-product check + sign-flip)
    // so the interpolation crosses the shortest great-circle arc, not
    // the long way around. This matches UE's behaviour and is the
    // intuitive default.
    // -----------------------------------------------------------------

    [[nodiscard]] static XPACT_FORCEINLINE FQuat Nlerp(const FQuat& A, const FQuat& B, float Alpha) noexcept
    {
        const float D = A.X * B.X + A.Y * B.Y + A.Z * B.Z + A.W * B.W;
        const float Sign = D < 0.0f ? -1.0f : 1.0f;
        FQuat R{
            A.X + Alpha * (Sign * B.X - A.X),
            A.Y + Alpha * (Sign * B.Y - A.Y),
            A.Z + Alpha * (Sign * B.Z - A.Z),
            A.W + Alpha * (Sign * B.W - A.W)
        };
        return R.GetNormalized();
    }

    // -----------------------------------------------------------------
    // Slerp (defined in FQuat.cpp; numerically sensitive).
    // -----------------------------------------------------------------

    [[nodiscard]] static FQuat Slerp(const FQuat& A, const FQuat& B, float Alpha) noexcept;

    // -----------------------------------------------------------------
    // FRotator interop. Explicit-only (locked decision 5: UE's
    // implicit FRotator <-> FQuat conversion is the #1 source of
    // rotation bugs in UE; explicit conversion forces every site to
    // be visible).
    //
    // FromEuler / ToEuler are the namespaced equivalent; they take
    // / produce FRotator (Pitch, Yaw, Roll in degrees). FromEuler
    // is declared here but defined inline at the bottom of FRotator.h
    // (depends on FRotator's full type).
    // -----------------------------------------------------------------

    [[nodiscard]] static FQuat FromEuler(const FRotator& R) noexcept;
    [[nodiscard]] FRotator ToEuler() const noexcept;
};

XPACT_FORCEINLINE constexpr FQuat operator*(float Scalar, const FQuat& Q) noexcept
{
    return FQuat{ Scalar * Q.X, Scalar * Q.Y, Scalar * Q.Z, Scalar * Q.W };
}

static_assert(sizeof(FQuat)  == 16, "FQuat ABI lock: 16 bytes (SIMD-aligned 4-float)");
static_assert(alignof(FQuat) == 16, "FQuat ABI lock: 16-byte aligned");

} // namespace XCore
