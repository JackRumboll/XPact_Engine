// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FTransform.Tests/MatrixRoundTrip.cpp -- ToMatrix / FromMatrix identity.
// =====================================================================
//
// XCore-4a Rev 3, Section 6.6 "property" row applied to FTransform:
// T.ToMatrix.FromMatrix == T within documented tolerance.
//
// The round-trip is not bit-exact (the quaternion-extraction
// algorithm in FromMatrix uses sqrt + branch on largest diagonal
// element); tolerance is 1e-3 for the rotation quaternion (which
// scales sub-linearly with the matrix entries' magnitudes).
//
// =====================================================================

#include "Math/FTransform.h"
#include "Math/FQuat.h"
#include "Math/FVector.h"
#include "Math/FMatrix.h"

#include <cmath>
#include <cstdio>

namespace
{
    [[nodiscard]] bool NearlyEqual(float A, float B, float Tol = 1.0e-3f) noexcept
    {
        const float D = A - B;
        return (D < 0.0f ? -D : D) < Tol;
    }

    [[nodiscard]] bool VecNearly(const ::XCore::FVector& A, const ::XCore::FVector& B, float Tol = 1.0e-3f) noexcept
    {
        return NearlyEqual(A.X, B.X, Tol) && NearlyEqual(A.Y, B.Y, Tol) && NearlyEqual(A.Z, B.Z, Tol);
    }

    [[nodiscard]] bool QuatNearly(const ::XCore::FQuat& A, const ::XCore::FQuat& B, float Tol = 1.0e-3f) noexcept
    {
        const bool Same = NearlyEqual(A.X, B.X, Tol) && NearlyEqual(A.Y, B.Y, Tol)
                       && NearlyEqual(A.Z, B.Z, Tol) && NearlyEqual(A.W, B.W, Tol);
        const bool Neg  = NearlyEqual(A.X, -B.X, Tol) && NearlyEqual(A.Y, -B.Y, Tol)
                       && NearlyEqual(A.Z, -B.Z, Tol) && NearlyEqual(A.W, -B.W, Tol);
        return Same || Neg;
    }
}

int main()
{
    using ::XCore::FTransform;
    using ::XCore::FQuat;
    using ::XCore::FVector;
    using ::XCore::FMatrix;

    // Identity round-trip.
    {
        const FTransform T = FTransform::Identity();
        const FMatrix M = T.ToMatrix();
        const FTransform T2 = FTransform::FromMatrix(M);
        if (!QuatNearly(T2.Rotation, T.Rotation)
            || !VecNearly(T2.Translation, T.Translation)
            || !VecNearly(T2.Scale3D, T.Scale3D))
        {
            std::fprintf(stderr, "FAIL: Identity transform round-trip\n");
            return 1;
        }
    }

    // Arbitrary TRS round-trip.
    {
        const FQuat Rot = FQuat::FromAxisAngle(FVector{ 0.0f, 0.0f, 1.0f }, 0.6f);
        const FVector Trans{ 5.0f, -3.0f, 2.0f };
        const FVector Scale{ 1.5f, 1.5f, 1.5f };
        const FTransform T{ Rot, Trans, Scale };
        const FMatrix M = T.ToMatrix();
        const FTransform T2 = FTransform::FromMatrix(M);
        if (!QuatNearly(T2.Rotation, T.Rotation))
        {
            std::fprintf(stderr, "FAIL: Arbitrary transform Rotation round-trip\n");
            std::fprintf(stderr, "      Got    (%f, %f, %f, %f)\n", T2.Rotation.X, T2.Rotation.Y, T2.Rotation.Z, T2.Rotation.W);
            std::fprintf(stderr, "      Want   (%f, %f, %f, %f)\n", T.Rotation.X, T.Rotation.Y, T.Rotation.Z, T.Rotation.W);
            return 1;
        }
        if (!VecNearly(T2.Translation, T.Translation))
        {
            std::fprintf(stderr, "FAIL: Arbitrary transform Translation round-trip\n");
            return 1;
        }
        if (!VecNearly(T2.Scale3D, T.Scale3D))
        {
            std::fprintf(stderr, "FAIL: Arbitrary transform Scale3D round-trip\n");
            return 1;
        }
    }

    // TransformPosition consistency.
    {
        const FQuat Rot = FQuat::FromAxisAngle(FVector{ 0.0f, 0.0f, 1.0f }, 1.5707963f);  // pi/2
        const FVector Trans{ 1.0f, 2.0f, 3.0f };
        const FVector Scale{ 1.0f, 1.0f, 1.0f };
        const FTransform T{ Rot, Trans, Scale };
        const FVector P{ 1.0f, 0.0f, 0.0f };
        const FVector P2 = T.TransformPosition(P);
        // Rotate (1, 0, 0) by 90 deg about Z -> (0, 1, 0); add translation (1, 2, 3) -> (1, 3, 3).
        if (!NearlyEqual(P2.X, 1.0f) || !NearlyEqual(P2.Y, 3.0f) || !NearlyEqual(P2.Z, 3.0f))
        {
            std::fprintf(stderr, "FAIL: TransformPosition expected (1, 3, 3); got (%f, %f, %f)\n",
                         P2.X, P2.Y, P2.Z);
            return 1;
        }
    }

    // Inverse(Inverse(T)) == T.
    {
        const FQuat Rot = FQuat::FromAxisAngle(FVector{ 1.0f, 0.0f, 0.0f }, 0.3f);
        const FTransform T{ Rot, FVector{ 1.0f, 2.0f, 3.0f }, FVector{ 1.0f, 1.0f, 1.0f } };
        const FTransform Inv = T.Inverse();
        const FTransform InvInv = Inv.Inverse();
        if (!QuatNearly(InvInv.Rotation, T.Rotation)
            || !VecNearly(InvInv.Translation, T.Translation)
            || !VecNearly(InvInv.Scale3D, T.Scale3D))
        {
            std::fprintf(stderr, "FAIL: Inverse(Inverse(T)) != T\n");
            return 1;
        }
    }

    return 0;
}
