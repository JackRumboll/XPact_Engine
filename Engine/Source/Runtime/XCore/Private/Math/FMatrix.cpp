// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FMatrix.cpp -- Inverse / Determinant / FromQuat[PositionScale] bodies.
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1 (Public API) + Section 17.3 C4
// (sim-path Inverse must emit NO FMA instructions).
//
// These bodies are out-of-line because the 4x4 expansion runs >60
// lines per function; inline-cost-model penalty exceeds the call-
// overhead savings.
//
// All bodies are pure arithmetic (no transcendentals, no platform-
// libm) so are equally sim-path-safe in both XMathFast and XSimMath
// TUs. The no-FMA contract is enforced by the build configuration
// (-mno-fma + /fp:precise + -ffp-contract=off on sim-path TUs).
//
// =====================================================================

#include "Math/FMatrix.h"

#include "Math/FQuat.h"
#include "Math/FVector.h"

namespace XCore
{

// =====================================================================
// FMatrix::Determinant.
//
// 4x4 determinant via Laplace expansion along the first row. The
// formula is the standard one; intermediate 3x3 sub-determinants are
// expanded inline to avoid the function-call overhead.
// =====================================================================

float FMatrix::Determinant() const noexcept
{
    const float m00 = M[0][0]; const float m01 = M[0][1]; const float m02 = M[0][2]; const float m03 = M[0][3];
    const float m10 = M[1][0]; const float m11 = M[1][1]; const float m12 = M[1][2]; const float m13 = M[1][3];
    const float m20 = M[2][0]; const float m21 = M[2][1]; const float m22 = M[2][2]; const float m23 = M[2][3];
    const float m30 = M[3][0]; const float m31 = M[3][1]; const float m32 = M[3][2]; const float m33 = M[3][3];

    // 2x2 sub-determinants from rows 2 and 3.
    const float s0 = m20 * m31 - m21 * m30;
    const float s1 = m20 * m32 - m22 * m30;
    const float s2 = m20 * m33 - m23 * m30;
    const float s3 = m21 * m32 - m22 * m31;
    const float s4 = m21 * m33 - m23 * m31;
    const float s5 = m22 * m33 - m23 * m32;

    // 3x3 cofactors from rows 0, 1, and the 2x2 sub-dets above.
    const float c00 =  m11 * s5 - m12 * s4 + m13 * s3;
    const float c01 = -m10 * s5 + m12 * s2 - m13 * s1;
    const float c02 =  m10 * s4 - m11 * s2 + m13 * s0;
    const float c03 = -m10 * s3 + m11 * s1 - m12 * s0;

    return m00 * c00 + m01 * c01 + m02 * c02 + m03 * c03;
}

// =====================================================================
// FMatrix::Inverse.
//
// 4x4 inverse via the adjugate / det form. The adjugate (transposed
// cofactor matrix) divided by the determinant is the inverse for any
// non-singular matrix. Returns the IDENTITY matrix for a singular input
// (this is the spec-mandated "no NaN propagation; saturate gracefully"
// behaviour per Section 6.6 adversarial-test row).
//
// No std::fma calls; the compiler emits multiply-then-add pairs that
// the sim-path -ffp-contract=off / -mno-fma flags will not coalesce
// into fmla / vfmadd*.
// =====================================================================

FMatrix FMatrix::Inverse() const noexcept
{
    const float m00 = M[0][0]; const float m01 = M[0][1]; const float m02 = M[0][2]; const float m03 = M[0][3];
    const float m10 = M[1][0]; const float m11 = M[1][1]; const float m12 = M[1][2]; const float m13 = M[1][3];
    const float m20 = M[2][0]; const float m21 = M[2][1]; const float m22 = M[2][2]; const float m23 = M[2][3];
    const float m30 = M[3][0]; const float m31 = M[3][1]; const float m32 = M[3][2]; const float m33 = M[3][3];

    // 2x2 sub-determinants from rows 2/3 (cofactors with j-paired rows).
    const float s0 = m20 * m31 - m21 * m30;
    const float s1 = m20 * m32 - m22 * m30;
    const float s2 = m20 * m33 - m23 * m30;
    const float s3 = m21 * m32 - m22 * m31;
    const float s4 = m21 * m33 - m23 * m31;
    const float s5 = m22 * m33 - m23 * m32;

    // 2x2 sub-determinants from rows 0/1 (for the rows-2/3 cofactor rows).
    const float c0 = m00 * m11 - m01 * m10;
    const float c1 = m00 * m12 - m02 * m10;
    const float c2 = m00 * m13 - m03 * m10;
    const float c3 = m01 * m12 - m02 * m11;
    const float c4 = m01 * m13 - m03 * m11;
    const float c5 = m02 * m13 - m03 * m12;

    // Determinant.
    const float Det = c0 * s5 - c1 * s4 + c2 * s3 + c3 * s2 - c4 * s1 + c5 * s0;
    if (Det == 0.0f)
    {
        // Singular -> identity. The adversarial-test contract.
        return FMatrix::Identity();
    }
    const float InvDet = 1.0f / Det;

    // Cofactor matrix, transposed and scaled by InvDet -> adjugate/det.
    // The 4x4 inverse via cofactor expansion has 16 components.
    FMatrix R;
    R.M[0][0] = ( m11 * s5 - m12 * s4 + m13 * s3) * InvDet;
    R.M[0][1] = (-m01 * s5 + m02 * s4 - m03 * s3) * InvDet;
    R.M[0][2] = ( m31 * c5 - m32 * c4 + m33 * c3) * InvDet;
    R.M[0][3] = (-m21 * c5 + m22 * c4 - m23 * c3) * InvDet;

    R.M[1][0] = (-m10 * s5 + m12 * s2 - m13 * s1) * InvDet;
    R.M[1][1] = ( m00 * s5 - m02 * s2 + m03 * s1) * InvDet;
    R.M[1][2] = (-m30 * c5 + m32 * c2 - m33 * c1) * InvDet;
    R.M[1][3] = ( m20 * c5 - m22 * c2 + m23 * c1) * InvDet;

    R.M[2][0] = ( m10 * s4 - m11 * s2 + m13 * s0) * InvDet;
    R.M[2][1] = (-m00 * s4 + m01 * s2 - m03 * s0) * InvDet;
    R.M[2][2] = ( m30 * c4 - m31 * c2 + m33 * c0) * InvDet;
    R.M[2][3] = (-m20 * c4 + m21 * c2 - m23 * c0) * InvDet;

    R.M[3][0] = (-m10 * s3 + m11 * s1 - m12 * s0) * InvDet;
    R.M[3][1] = ( m00 * s3 - m01 * s1 + m02 * s0) * InvDet;
    R.M[3][2] = (-m30 * c3 + m31 * c1 - m32 * c0) * InvDet;
    R.M[3][3] = ( m20 * c3 - m21 * c1 + m22 * c0) * InvDet;

    return R;
}

// =====================================================================
// FMatrix::FromQuat.
//
// Standard quaternion-to-rotation-matrix conversion. Assumes Q is a
// unit quaternion; non-unit input produces a non-orthogonal matrix
// (the caller is responsible for normalisation).
//
// Row-major form (locked decision 2):
//
//   M[0] = (1 - 2(yy + zz),  2(xy + zw),      2(xz - yw),      0)
//   M[1] = (2(xy - zw),      1 - 2(xx + zz),  2(yz + xw),      0)
//   M[2] = (2(xz + yw),      2(yz - xw),      1 - 2(xx + yy),  0)
//   M[3] = (0,               0,               0,               1)
//
// Note: this is the row-major (row-vector composition) form.
// Column-major libraries (UE) transpose this. The handedness of the
// rotation is unchanged.
// =====================================================================

FMatrix FMatrix::FromQuat(const FQuat& Q) noexcept
{
    const float X2 = Q.X + Q.X;
    const float Y2 = Q.Y + Q.Y;
    const float Z2 = Q.Z + Q.Z;
    const float XX = Q.X * X2;
    const float YY = Q.Y * Y2;
    const float ZZ = Q.Z * Z2;
    const float XY = Q.X * Y2;
    const float XZ = Q.X * Z2;
    const float YZ = Q.Y * Z2;
    const float WX = Q.W * X2;
    const float WY = Q.W * Y2;
    const float WZ = Q.W * Z2;

    FMatrix R;
    R.M[0][0] = 1.0f - (YY + ZZ);
    R.M[0][1] = XY + WZ;
    R.M[0][2] = XZ - WY;
    R.M[0][3] = 0.0f;

    R.M[1][0] = XY - WZ;
    R.M[1][1] = 1.0f - (XX + ZZ);
    R.M[1][2] = YZ + WX;
    R.M[1][3] = 0.0f;

    R.M[2][0] = XZ + WY;
    R.M[2][1] = YZ - WX;
    R.M[2][2] = 1.0f - (XX + YY);
    R.M[2][3] = 0.0f;

    R.M[3][0] = 0.0f;
    R.M[3][1] = 0.0f;
    R.M[3][2] = 0.0f;
    R.M[3][3] = 1.0f;
    return R;
}

// =====================================================================
// FMatrix::FromQuatPositionScale.
//
// Composed TRS in row-major / row-vector convention:
//   M = ScaleMatrix * RotationMatrix * TranslationMatrix
// applied to the row vector v as v * M.
//
// The shortcut form (avoids three matrix multiplications):
//   1. Build the rotation matrix (3x3 upper-left).
//   2. Scale each row of the 3x3 by the corresponding scale component.
//   3. Set the last row to the translation.
// =====================================================================

FMatrix FMatrix::FromQuatPositionScale(
    const FQuat& Rotation, const FVector& Translation, const FVector& Scale) noexcept
{
    FMatrix R = FromQuat(Rotation);

    // Scale: pre-scale each row (rows 0/1/2 are the rotated basis axes).
    R.M[0][0] *= Scale.X; R.M[0][1] *= Scale.X; R.M[0][2] *= Scale.X;
    R.M[1][0] *= Scale.Y; R.M[1][1] *= Scale.Y; R.M[1][2] *= Scale.Y;
    R.M[2][0] *= Scale.Z; R.M[2][1] *= Scale.Z; R.M[2][2] *= Scale.Z;

    // Translation: set the last row.
    R.M[3][0] = Translation.X;
    R.M[3][1] = Translation.Y;
    R.M[3][2] = Translation.Z;
    R.M[3][3] = 1.0f;

    return R;
}

} // namespace XCore
