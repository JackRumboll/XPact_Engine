// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FQuat.Tests/SlerpEndpoints.cpp -- Slerp endpoint identity.
// =====================================================================
//
// XCore-4a Rev 3, Section 6.6 "property" row: "Slerp endpoints".
//
//   Slerp(A, B, 0) == A
//   Slerp(A, B, 1) == B (up to short-arc sign flip)
//
// =====================================================================

#include "Math/FQuat.h"

#include <cmath>
#include <cstdio>

namespace
{
    [[nodiscard]] bool NearlyEqual(float A, float B, float Tol = 1.0e-5f) noexcept
    {
        const float D = A - B;
        return (D < 0.0f ? -D : D) < Tol;
    }

    [[nodiscard]] bool QuatNearlyEqual(const ::XCore::FQuat& A, const ::XCore::FQuat& B, float Tol = 1.0e-4f) noexcept
    {
        // Quaternions q and -q represent the same rotation; allow either sign.
        const bool Same = NearlyEqual(A.X, B.X, Tol) && NearlyEqual(A.Y, B.Y, Tol)
                       && NearlyEqual(A.Z, B.Z, Tol) && NearlyEqual(A.W, B.W, Tol);
        const bool Negated = NearlyEqual(A.X, -B.X, Tol) && NearlyEqual(A.Y, -B.Y, Tol)
                          && NearlyEqual(A.Z, -B.Z, Tol) && NearlyEqual(A.W, -B.W, Tol);
        return Same || Negated;
    }
}

int main()
{
    using ::XCore::FQuat;
    using ::XCore::FVector;

    // Identity slerps: Slerp(Identity, Q, ...) should reproduce the path.
    {
        const FQuat A = FQuat::Identity();
        const FQuat B = FQuat::FromAxisAngle(FVector{ 0.0f, 0.0f, 1.0f }, 1.0f);  // 1 rad about Z
        const FQuat S0 = FQuat::Slerp(A, B, 0.0f);
        const FQuat S1 = FQuat::Slerp(A, B, 1.0f);
        if (!QuatNearlyEqual(S0, A))
        {
            std::fprintf(stderr, "FAIL: Slerp(A, B, 0) != A\n");
            return 1;
        }
        if (!QuatNearlyEqual(S1, B))
        {
            std::fprintf(stderr, "FAIL: Slerp(A, B, 1) != B\n");
            return 1;
        }
    }

    // Two arbitrary quaternions.
    {
        const FQuat A = FQuat::FromAxisAngle(FVector{ 1.0f, 0.0f, 0.0f }, 0.5f);
        const FQuat B = FQuat::FromAxisAngle(FVector{ 0.0f, 1.0f, 0.0f }, 1.2f);
        const FQuat S0 = FQuat::Slerp(A, B, 0.0f);
        const FQuat S1 = FQuat::Slerp(A, B, 1.0f);
        if (!QuatNearlyEqual(S0, A))
        {
            std::fprintf(stderr, "FAIL: Slerp(arbitrary A, B, 0) != A\n");
            return 1;
        }
        if (!QuatNearlyEqual(S1, B))
        {
            std::fprintf(stderr, "FAIL: Slerp(arbitrary A, B, 1) != B\n");
            return 1;
        }
    }

    // Halfway slerp should produce a unit quaternion.
    {
        const FQuat A = FQuat::FromAxisAngle(FVector{ 0.0f, 0.0f, 1.0f }, 0.0f);  // identity
        const FQuat B = FQuat::FromAxisAngle(FVector{ 0.0f, 0.0f, 1.0f }, 1.0f);
        const FQuat M = FQuat::Slerp(A, B, 0.5f);
        if (!M.IsNormalized(1.0e-3f))
        {
            std::fprintf(stderr, "FAIL: Slerp halfway not unit (LengthSq = %f)\n", M.LengthSq());
            return 1;
        }
    }

    // Nlerp endpoints similarly.
    {
        const FQuat A = FQuat::Identity();
        const FQuat B = FQuat::FromAxisAngle(FVector{ 0.0f, 1.0f, 0.0f }, 0.7f);
        if (!QuatNearlyEqual(FQuat::Nlerp(A, B, 0.0f), A))
        {
            std::fprintf(stderr, "FAIL: Nlerp(A, B, 0) != A\n");
            return 1;
        }
        if (!QuatNearlyEqual(FQuat::Nlerp(A, B, 1.0f), B))
        {
            std::fprintf(stderr, "FAIL: Nlerp(A, B, 1) != B\n");
            return 1;
        }
    }

    return 0;
}
