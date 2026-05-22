// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FMatrix.Tests/InverseRoundTrip.cpp -- M.Inverse().Inverse() approx M.
// =====================================================================
//
// XCore-4a Rev 3, Section 6.6 "property" row: Inverse(Inverse(M)) == M
// within tolerance.
//
// Inverse is not perfectly exact due to floating-point round-off; the
// tolerance is set to 1e-4 (sufficient for any well-conditioned
// matrix; the test inputs are intentionally well-conditioned).
//
// =====================================================================

#include "Math/FMatrix.h"
#include "Math/FQuat.h"
#include "Math/FVector.h"

#include <cmath>
#include <cstdio>

namespace
{
    [[nodiscard]] bool NearlyEqual(float A, float B, float Tol = 1.0e-4f) noexcept
    {
        const float D = A - B;
        return (D < 0.0f ? -D : D) < Tol;
    }

    [[nodiscard]] bool MatNearlyEqual(const ::XCore::FMatrix& A, const ::XCore::FMatrix& B, float Tol = 1.0e-4f) noexcept
    {
        for (int I = 0; I < 4; ++I)
            for (int J = 0; J < 4; ++J)
                if (!NearlyEqual(A.M[I][J], B.M[I][J], Tol)) return false;
        return true;
    }
}

int main()
{
    using ::XCore::FMatrix;
    using ::XCore::FQuat;
    using ::XCore::FVector;

    // Identity round-trip.
    {
        const FMatrix I = FMatrix::Identity();
        const FMatrix Inv = I.Inverse();
        if (!MatNearlyEqual(Inv, I))
        {
            std::fprintf(stderr, "FAIL: Inverse(Identity) != Identity\n");
            return 1;
        }
    }

    // Inverse(Inverse(M)) == M for several well-conditioned matrices.
    {
        const FMatrix Cases[] = {
            FMatrix::FromTranslation(FVector{ 5.0f, -2.0f, 3.0f }),
            FMatrix::FromScale(FVector{ 2.0f, 3.0f, 4.0f }),
            FMatrix::FromQuat(FQuat::FromAxisAngle(FVector{ 0.0f, 0.0f, 1.0f }, 0.7f)),
            FMatrix::FromQuatPositionScale(
                FQuat::FromAxisAngle(FVector{ 1.0f, 0.0f, 0.0f }, 1.2f),
                FVector{ 1.0f, 2.0f, 3.0f },
                FVector{ 1.5f, 1.5f, 1.5f }),
        };
        for (const FMatrix& M : Cases)
        {
            const FMatrix Inv = M.Inverse();
            const FMatrix InvInv = Inv.Inverse();
            if (!MatNearlyEqual(InvInv, M))
            {
                std::fprintf(stderr, "FAIL: Inverse(Inverse(M)) != M\n");
                return 1;
            }
            // M * M^-1 == I (within tolerance).
            const FMatrix Prod = M * Inv;
            if (!MatNearlyEqual(Prod, FMatrix::Identity()))
            {
                std::fprintf(stderr, "FAIL: M * M^-1 != I\n");
                for (int I = 0; I < 4; ++I)
                {
                    std::fprintf(stderr, "      [%f %f %f %f]\n",
                                 Prod.M[I][0], Prod.M[I][1], Prod.M[I][2], Prod.M[I][3]);
                }
                return 1;
            }
        }
    }

    // Singular matrix: zero matrix should produce identity (no NaN).
    {
        const FMatrix Z = FMatrix::Zero();
        const FMatrix Inv = Z.Inverse();
        if (!MatNearlyEqual(Inv, FMatrix::Identity()))
        {
            std::fprintf(stderr, "FAIL: Inverse(Zero) should be Identity (graceful degradation)\n");
            return 1;
        }
    }

    return 0;
}
