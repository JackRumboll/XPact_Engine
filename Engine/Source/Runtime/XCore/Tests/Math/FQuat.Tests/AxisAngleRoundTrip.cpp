// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FQuat.Tests/AxisAngleRoundTrip.cpp -- FromAxisAngle -> ToAxisAngle.
// =====================================================================
//
// XCore-4a Rev 3, Section 6.6 ("property" row): FQuat <-> FRotator
// round-trip identity within documented tolerance.
//
// This test covers the axis-angle round-trip (more fundamental than
// the FRotator round-trip): we build a quaternion from a unit axis +
// angle, decompose it back, and assert axis/angle match within
// numerical tolerance.
//
// =====================================================================

#include "Math/FQuat.h"

#include <cmath>
#include <cstdio>

namespace
{
    [[nodiscard]] bool NearlyEqual(float A, float B, float Tol = 1.0e-3f) noexcept
    {
        const float D = A - B;
        return (D < 0.0f ? -D : D) < Tol;
    }

    [[nodiscard]] bool VecNearlyEqualUpToSign(const ::XCore::FVector& A, const ::XCore::FVector& B, float Tol = 1.0e-3f) noexcept
    {
        const bool Same = NearlyEqual(A.X, B.X, Tol) && NearlyEqual(A.Y, B.Y, Tol) && NearlyEqual(A.Z, B.Z, Tol);
        const bool Neg  = NearlyEqual(A.X, -B.X, Tol) && NearlyEqual(A.Y, -B.Y, Tol) && NearlyEqual(A.Z, -B.Z, Tol);
        return Same || Neg;
    }
}

int main()
{
    using ::XCore::FQuat;
    using ::XCore::FVector;

    // Test vectors: various unit-axis + radian angle pairs.
    struct Case { FVector Axis; float Angle; const char* Name; };
    const Case Cases[] = {
        { { 1.0f, 0.0f, 0.0f }, 0.5f, "X-axis 0.5 rad"  },
        { { 0.0f, 1.0f, 0.0f }, 1.2f, "Y-axis 1.2 rad"  },
        { { 0.0f, 0.0f, 1.0f }, 2.3f, "Z-axis 2.3 rad"  },
        { { 1.0f, 0.0f, 0.0f }, 0.0f, "X-axis identity" },
        // Normalized arbitrary axis. (1,1,1)/sqrt(3) ~ (0.5774, 0.5774, 0.5774).
        { { 0.5773503f, 0.5773503f, 0.5773503f }, 1.5f, "Diagonal axis 1.5 rad" },
    };

    for (const Case& C : Cases)
    {
        const FQuat Q = FQuat::FromAxisAngle(C.Axis, C.Angle);

        // The resulting quaternion must be unit.
        if (!Q.IsNormalized(1.0e-3f))
        {
            std::fprintf(stderr, "FAIL [%s]: FromAxisAngle output not unit (LengthSq = %f)\n",
                         C.Name, Q.LengthSq());
            return 1;
        }

        // Round-trip.
        FVector ResultAxis;
        float ResultAngle = 0.0f;
        Q.ToAxisAngle(ResultAxis, ResultAngle);

        if (C.Angle == 0.0f)
        {
            // Identity: angle is zero; axis is unspecified. Just check
            // the angle.
            if (!NearlyEqual(ResultAngle, 0.0f))
            {
                std::fprintf(stderr, "FAIL [%s]: identity angle non-zero (%f)\n", C.Name, ResultAngle);
                return 1;
            }
            continue;
        }

        if (!NearlyEqual(ResultAngle, C.Angle))
        {
            std::fprintf(stderr, "FAIL [%s]: round-trip angle %f != original %f\n",
                         C.Name, ResultAngle, C.Angle);
            return 1;
        }
        if (!VecNearlyEqualUpToSign(ResultAxis, C.Axis))
        {
            std::fprintf(stderr, "FAIL [%s]: round-trip axis (%f, %f, %f) != original (%f, %f, %f)\n",
                         C.Name, ResultAxis.X, ResultAxis.Y, ResultAxis.Z, C.Axis.X, C.Axis.Y, C.Axis.Z);
            return 1;
        }
    }

    // Sanity: rotating Forward 90 degrees about Up should produce Right
    // (in XPact's RH-Z-up convention with positive-yaw = left turn,
    // a 90 degree CCW rotation about +Z takes +X (Forward) to +Y (Right)).
    {
        const FQuat Yaw90 = FQuat::FromAxisAngle(::XCore::UpVector, 1.5707963f);  // pi/2
        const FVector Rotated = Yaw90.RotateVector(::XCore::ForwardVector);
        if (!NearlyEqual(Rotated.X, 0.0f, 1.0e-3f)
            || !NearlyEqual(Rotated.Y, 1.0f, 1.0e-3f)
            || !NearlyEqual(Rotated.Z, 0.0f, 1.0e-3f))
        {
            std::fprintf(stderr, "FAIL: 90-deg rotation about Up of Forward expected (0, 1, 0); got (%f, %f, %f)\n",
                         Rotated.X, Rotated.Y, Rotated.Z);
            return 1;
        }
    }

    return 0;
}
