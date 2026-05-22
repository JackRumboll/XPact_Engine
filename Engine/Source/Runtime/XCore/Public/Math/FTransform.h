// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FTransform.h -- TRS (rotation/translation/scale) transform.
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1.
//
// LAYOUT:
//   FQuat   Rotation     (16 bytes; 16-byte aligned)
//   FVector Translation  (12 bytes; 4-byte aligned)
//   padding (4 bytes for alignment)
//   FVector Scale3D      (12 bytes; 4-byte aligned)
//   padding (4 bytes for alignment to 16)
//   ----
//   48 bytes total; 16-byte aligned (carries FQuat's SIMD alignment).
//
// FTransform is the standard scene-graph transform; rotation, then
// scale, then translation applied in the canonical TRS composition.
// The struct is intentionally NOT 64 bytes (we don't ABI-lock the
// total size; the FQuat / FVector members carry their own ABI locks).
//
// SIM-PATH SAFETY:
//   All members are sim-path-safe. The TransformVector /
//   TransformPosition operations are pure arithmetic. ToMatrix /
//   FromMatrix involve a FromQuat (pure arithmetic) so are sim-path-
//   safe.
//
// UE REFERENCES:
//   Engine/Source/Runtime/Core/Public/Math/Transform.h -- studied.
//   UE's vector-aligned-FTransform variant (TransformVectorized.h)
//   is NOT adopted at this header; the SIMD speedup is paid for
//   in non-sim-path code via a separate fast-path overload, which
//   is Phase 2 work (TODO marker below).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Math/FMatrix.h"
#include "Math/FQuat.h"
#include "Math/FVector.h"

namespace XCore
{

struct alignas(16) FTransform
{
    FQuat   Rotation;
    FVector Translation;
    FVector Scale3D;

    XPACT_FORCEINLINE constexpr FTransform() noexcept
        : Rotation(FQuat::Identity())
        , Translation(0.0f, 0.0f, 0.0f)
        , Scale3D(1.0f, 1.0f, 1.0f)
    {}

    XPACT_FORCEINLINE constexpr FTransform(const FQuat& InRotation, const FVector& InTranslation, const FVector& InScale) noexcept
        : Rotation(InRotation)
        , Translation(InTranslation)
        , Scale3D(InScale)
    {}

    [[nodiscard]] static XPACT_FORCEINLINE constexpr FTransform Identity() noexcept
    {
        return FTransform{};
    }

    // -----------------------------------------------------------------
    // Accessors (Section 6.1 spec body uses GetLocation/GetRotation/
    // GetScale3D as accessor names; direct field access is also fine).
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr const FVector& GetLocation() const noexcept { return Translation; }
    [[nodiscard]] XPACT_FORCEINLINE constexpr const FQuat&   GetRotation() const noexcept { return Rotation; }
    [[nodiscard]] XPACT_FORCEINLINE constexpr const FVector& GetScale3D()  const noexcept { return Scale3D; }

    // -----------------------------------------------------------------
    // ToMatrix / FromMatrix.
    //
    // Composition order (TRS):
    //   M = scale * rotation * translation
    // i.e., a point in local space is first scaled, then rotated,
    // then translated to world space.
    //
    // FromMatrix decomposes an arbitrary 4x4 into TRS; the
    // decomposition assumes the matrix is well-formed (no shear).
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE FMatrix ToMatrix() const noexcept
    {
        return FMatrix::FromQuatPositionScale(Rotation, Translation, Scale3D);
    }

    [[nodiscard]] static FTransform FromMatrix(const FMatrix& M) noexcept
    {
        // Translation is the last row.
        const FVector T{ M.M[3][0], M.M[3][1], M.M[3][2] };

        // Scale is the magnitude of each basis row (rows 0/1/2 are
        // the rotated + scaled basis axes).
        const FVector AxisX{ M.M[0][0], M.M[0][1], M.M[0][2] };
        const FVector AxisY{ M.M[1][0], M.M[1][1], M.M[1][2] };
        const FVector AxisZ{ M.M[2][0], M.M[2][1], M.M[2][2] };
        const FVector S{ AxisX.Length(), AxisY.Length(), AxisZ.Length() };

        // Remove scale from the basis vectors to recover the rotation.
        const float InvSX = S.X != 0.0f ? 1.0f / S.X : 0.0f;
        const float InvSY = S.Y != 0.0f ? 1.0f / S.Y : 0.0f;
        const float InvSZ = S.Z != 0.0f ? 1.0f / S.Z : 0.0f;

        // Recover the rotation quaternion from the unscaled basis.
        // Algorithm: Shepperd's method (numerically stable; picks the
        // largest diagonal element to avoid the sqrt(0)/0 trap).
        const float R00 = M.M[0][0] * InvSX;
        const float R01 = M.M[0][1] * InvSX;
        const float R02 = M.M[0][2] * InvSX;
        const float R10 = M.M[1][0] * InvSY;
        const float R11 = M.M[1][1] * InvSY;
        const float R12 = M.M[1][2] * InvSY;
        const float R20 = M.M[2][0] * InvSZ;
        const float R21 = M.M[2][1] * InvSZ;
        const float R22 = M.M[2][2] * InvSZ;

        const float Trace = R00 + R11 + R22;
        FQuat Q;
        if (Trace > 0.0f)
        {
            const float S0 = ::std::sqrt(Trace + 1.0f) * 2.0f;
            Q.W = 0.25f * S0;
            Q.X = (R12 - R21) / S0;
            Q.Y = (R20 - R02) / S0;
            Q.Z = (R01 - R10) / S0;
        }
        else if (R00 > R11 && R00 > R22)
        {
            const float S0 = ::std::sqrt(1.0f + R00 - R11 - R22) * 2.0f;
            Q.W = (R12 - R21) / S0;
            Q.X = 0.25f * S0;
            Q.Y = (R10 + R01) / S0;
            Q.Z = (R20 + R02) / S0;
        }
        else if (R11 > R22)
        {
            const float S0 = ::std::sqrt(1.0f + R11 - R00 - R22) * 2.0f;
            Q.W = (R20 - R02) / S0;
            Q.X = (R10 + R01) / S0;
            Q.Y = 0.25f * S0;
            Q.Z = (R21 + R12) / S0;
        }
        else
        {
            const float S0 = ::std::sqrt(1.0f + R22 - R00 - R11) * 2.0f;
            Q.W = (R01 - R10) / S0;
            Q.X = (R20 + R02) / S0;
            Q.Y = (R21 + R12) / S0;
            Q.Z = 0.25f * S0;
        }
        return FTransform{ Q, T, S };
    }

    // -----------------------------------------------------------------
    // TransformVector  -- direction transform (rotation + scale only).
    // TransformPosition -- position transform (full TRS).
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr FVector TransformVector(const FVector& V) const noexcept
    {
        // (rotation * (scale o v))  -- "o" is Hadamard product.
        const FVector Scaled{ V.X * Scale3D.X, V.Y * Scale3D.Y, V.Z * Scale3D.Z };
        return Rotation.RotateVector(Scaled);
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr FVector TransformPosition(const FVector& V) const noexcept
    {
        return TransformVector(V) + Translation;
    }

    // -----------------------------------------------------------------
    // Inverse.
    //
    // For a unit-scale rotation-only transform: Inverse = (Rot^-1, -Rot^-1 * T, 1)
    // For a general TRS transform we invert each component independently
    // and re-assemble.
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr FTransform Inverse() const noexcept
    {
        const FQuat InvRot = Rotation.Conjugate();  // unit-quat assumption
        const FVector InvScale{
            Scale3D.X != 0.0f ? 1.0f / Scale3D.X : 0.0f,
            Scale3D.Y != 0.0f ? 1.0f / Scale3D.Y : 0.0f,
            Scale3D.Z != 0.0f ? 1.0f / Scale3D.Z : 0.0f
        };
        // T' = -InvRot * (Translation o InvScale)
        const FVector ScaledT{
            Translation.X * InvScale.X,
            Translation.Y * InvScale.Y,
            Translation.Z * InvScale.Z
        };
        const FVector InvT = InvRot.RotateVector(-ScaledT);
        return FTransform{ InvRot, InvT, InvScale };
    }
};

// TODO(Phase 2): A SIMD-vectorised FTransformVectorized variant matching
// UE's TransformVectorized.h is reserved for future perf work. The
// scalar form here is the correctness baseline; the SIMD variant adds
// __m128-backed members and a friend conversion. Tracking via
// XPactPerformanceBacklog.

} // namespace XCore
