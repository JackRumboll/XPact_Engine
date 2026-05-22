// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FVector.Tests/Algebra.cpp -- vector algebra identities.
// =====================================================================
//
// XCore-4a Rev 3, Section 6.6 ("property" row): Dot(A,A) ==
// LengthSq(A); Cross antisymmetry; Lerp endpoints; normalize identity.
//
// All assertions are compile-time (constexpr) where possible; the
// runtime checks cover the sqrt-routed members (Length / GetSafeNormal).
//
// =====================================================================

#include "Math/FVector.h"

#include <cstdio>

namespace
{
    [[nodiscard]] constexpr bool NearlyEqual(float A, float B, float Tol = 1.0e-5f) noexcept
    {
        const float D = A - B;
        return (D < 0.0f ? -D : D) < Tol;
    }

    [[nodiscard]] constexpr bool VecNearlyEqual(const ::XCore::FVector& A, const ::XCore::FVector& B, float Tol = 1.0e-5f) noexcept
    {
        return NearlyEqual(A.X, B.X, Tol) && NearlyEqual(A.Y, B.Y, Tol) && NearlyEqual(A.Z, B.Z, Tol);
    }
}

// =====================================================================
// Compile-time identities.
// =====================================================================

// Dot(A, A) == LengthSq(A) -- the load-bearing inner-product property.
static_assert([]() constexpr {
    constexpr ::XCore::FVector V{ 1.0f, 2.0f, 3.0f };
    return V.Dot(V) == V.LengthSq();
}(), "Dot(A, A) == LengthSq(A)");

// Dot identity: 1 . 1 = 1 + 1 + 1 = 3.
static_assert(::XCore::FVector{ 1, 1, 1 }.Dot(::XCore::FVector{ 1, 1, 1 }) == 3.0f, "Dot(1,1,1) . (1,1,1) == 3");

// Cross is antisymmetric: Cross(A, B) == -Cross(B, A).
static_assert([]() constexpr {
    constexpr ::XCore::FVector A{ 1.0f, 2.0f, 3.0f };
    constexpr ::XCore::FVector B{ 4.0f, 5.0f, 6.0f };
    const auto AxB = A.Cross(B);
    const auto BxA = B.Cross(A);
    return AxB.X == -BxA.X && AxB.Y == -BxA.Y && AxB.Z == -BxA.Z;
}(), "Cross antisymmetry: Cross(A, B) == -Cross(B, A)");

// Cross(A, A) == 0.
static_assert([]() constexpr {
    constexpr ::XCore::FVector A{ 1.0f, 2.0f, 3.0f };
    const auto AxA = A.Cross(A);
    return AxA.X == 0.0f && AxA.Y == 0.0f && AxA.Z == 0.0f;
}(), "Cross(A, A) == 0");

// Cross is perpendicular to its operands: Cross(A, B) . A == 0.
static_assert([]() constexpr {
    constexpr ::XCore::FVector A{ 1.0f, 2.0f, 3.0f };
    constexpr ::XCore::FVector B{ 4.0f, 5.0f, 6.0f };
    return A.Cross(B).Dot(A) == 0.0f;
}(), "Cross(A, B) . A == 0");

static_assert([]() constexpr {
    constexpr ::XCore::FVector A{ 1.0f, 2.0f, 3.0f };
    constexpr ::XCore::FVector B{ 4.0f, 5.0f, 6.0f };
    return A.Cross(B).Dot(B) == 0.0f;
}(), "Cross(A, B) . B == 0");

// Lerp endpoints.
static_assert([]() constexpr {
    constexpr ::XCore::FVector A{ 1, 2, 3 };
    constexpr ::XCore::FVector B{ 4, 5, 6 };
    return ::XCore::FVector::Lerp(A, B, 0.0f) == A;
}(), "Lerp(A, B, 0) == A");

static_assert([]() constexpr {
    constexpr ::XCore::FVector A{ 1, 2, 3 };
    constexpr ::XCore::FVector B{ 4, 5, 6 };
    return ::XCore::FVector::Lerp(A, B, 1.0f) == B;
}(), "Lerp(A, B, 1) == B");

// Lerp halfway.
static_assert([]() constexpr {
    constexpr ::XCore::FVector A{ 0, 0, 0 };
    constexpr ::XCore::FVector B{ 2, 4, 6 };
    constexpr auto Mid = ::XCore::FVector::Lerp(A, B, 0.5f);
    return Mid.X == 1.0f && Mid.Y == 2.0f && Mid.Z == 3.0f;
}(), "Lerp halfway");

// =====================================================================
// Runtime checks (sqrt-routed members).
// =====================================================================

int main()
{
    using ::XCore::FVector;

    // Length of (3, 4, 0) is 5 (3-4-5 triangle).
    {
        const FVector V{ 3.0f, 4.0f, 0.0f };
        const float L = V.Length();
        if (!NearlyEqual(L, 5.0f))
        {
            std::fprintf(stderr, "FAIL: Length of (3,4,0) expected 5, got %f\n", L);
            return 1;
        }
    }

    // GetSafeNormal preserves direction; the result is unit-length.
    {
        const FVector V{ 3.0f, 4.0f, 0.0f };
        const FVector N = V.GetSafeNormal();
        if (!NearlyEqual(N.Length(), 1.0f))
        {
            std::fprintf(stderr, "FAIL: Normalized vector not unit-length: %f\n", N.Length());
            return 1;
        }
        // (3/5, 4/5, 0)
        if (!NearlyEqual(N.X, 0.6f) || !NearlyEqual(N.Y, 0.8f) || !NearlyEqual(N.Z, 0.0f))
        {
            std::fprintf(stderr, "FAIL: GetSafeNormal direction: (%f, %f, %f)\n", N.X, N.Y, N.Z);
            return 1;
        }
    }

    // GetSafeNormal of zero vector returns zero (no NaN propagation).
    {
        const FVector Zero{ 0.0f, 0.0f, 0.0f };
        const FVector N = Zero.GetSafeNormal();
        if (!(N.X == 0.0f && N.Y == 0.0f && N.Z == 0.0f))
        {
            std::fprintf(stderr, "FAIL: Normalize(0) should return zero, got (%f, %f, %f)\n", N.X, N.Y, N.Z);
            return 1;
        }
    }

    // IsNormalized of unit vector returns true.
    {
        const FVector Unit{ 1.0f, 0.0f, 0.0f };
        if (!Unit.IsNormalized())
        {
            std::fprintf(stderr, "FAIL: (1,0,0).IsNormalized() returned false\n");
            return 1;
        }
    }

    // IsNearlyZero of zero returns true; of unit returns false.
    {
        const FVector Zero{ 0.0f, 0.0f, 0.0f };
        const FVector Unit{ 1.0f, 0.0f, 0.0f };
        if (!Zero.IsNearlyZero())
        {
            std::fprintf(stderr, "FAIL: Zero.IsNearlyZero() returned false\n");
            return 1;
        }
        if (Unit.IsNearlyZero())
        {
            std::fprintf(stderr, "FAIL: Unit.IsNearlyZero() returned true\n");
            return 1;
        }
    }

    // Free function aliases.
    {
        const FVector A{ 1, 2, 3 };
        const FVector B{ 4, 5, 6 };
        if (::XCore::Dot(A, B) != A.Dot(B))
        {
            std::fprintf(stderr, "FAIL: Dot free-function != member\n");
            return 1;
        }
        if (!(::XCore::Cross(A, B) == A.Cross(B)))
        {
            std::fprintf(stderr, "FAIL: Cross free-function != member\n");
            return 1;
        }
    }

    // Scalar * FVector commutative.
    {
        const FVector V{ 1, 2, 3 };
        const FVector A = V * 2.0f;
        const FVector B = 2.0f * V;
        if (!(A == B))
        {
            std::fprintf(stderr, "FAIL: V * scalar != scalar * V\n");
            return 1;
        }
    }

    (void)VecNearlyEqual;  // silence unused-warning
    return 0;
}
