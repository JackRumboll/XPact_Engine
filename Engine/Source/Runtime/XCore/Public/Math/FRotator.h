// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FRotator.h -- designer-facing Pitch/Yaw/Roll degrees triple.
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1 + Section 6.3 + locked decision 5
// (FRotator kept as designer-facing struct in non-sim-path code;
// sim-path overlay header XSimMath.h marks it [[deprecated]] so
// sim-path TUs reject it at compile time).
//
// LAYOUT (locked at sizeof == 12, alignof == 4):
//   bytes  0-3   Pitch  (float, degrees)
//   bytes  4-7   Yaw    (float, degrees)
//   bytes  8-11  Roll   (float, degrees)
//
// CONVENTION:
//   Pitch -- rotation about the Right axis (Y in RH-Z-up).
//            Positive pitch = nose up.
//   Yaw   -- rotation about the Up axis (Z in RH-Z-up).
//            Positive yaw = turning to the left when looking down.
//   Roll  -- rotation about the Forward axis (X in RH-Z-up).
//            Positive roll = right-side-down.
//
// Composition order in FromQuat (the matching FQuat-builder):
//   q = Q_yaw * Q_pitch * Q_roll
// i.e., apply roll first, then pitch, then yaw. This matches UE's
// intrinsic-rotation interpretation (the world's first rotation is
// the last quaternion in the chain).
//
// SIM-PATH BAN (locked decision 5):
//   This header is included by XMathFast.h (no decoration; non-sim-
//   path TUs use FRotator freely). The XSimMath.h overlay header
//   includes a #pragma deprecated FRotator (MSVC) and a
//   [[deprecated]] redecoration via a type alias so sim-path TUs
//   reject the type at compile time with the diagnostic
//       "not sim-path-safe; use FQuat -- degree-based rotation
//        accumulates non-deterministic error across the libm trig
//        path even with Sleef routing"
//
//   The sim-path ban is enforced at the namespace-overlay level
//   (XSimMath.h) rather than at this header so designer-facing UI
//   code (which includes XMathFast.h) is unaffected.
//
// UE REFERENCES:
//   Engine/Source/Runtime/Core/Public/Math/Rotator.h  -- studied.
//   UE's implicit FRotator <-> FQuat conversions are NOT adopted
//   (divergence rows 3 + 7 in Section 6.5).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Math/FQuat.h"
#include "Math/FVector.h"

#include <cmath>

namespace XCore
{

struct alignas(4) FRotator
{
    float Pitch;
    float Yaw;
    float Roll;

    XPACT_FORCEINLINE constexpr FRotator() noexcept : Pitch(0.0f), Yaw(0.0f), Roll(0.0f) {}
    XPACT_FORCEINLINE constexpr FRotator(float InPitch, float InYaw, float InRoll) noexcept
        : Pitch(InPitch), Yaw(InYaw), Roll(InRoll) {}

    [[nodiscard]] static XPACT_FORCEINLINE constexpr FRotator Zero() noexcept
    {
        return FRotator{ 0.0f, 0.0f, 0.0f };
    }

    XPACT_FORCEINLINE constexpr FRotator operator+(const FRotator& B) const noexcept
    {
        return FRotator{ Pitch + B.Pitch, Yaw + B.Yaw, Roll + B.Roll };
    }

    XPACT_FORCEINLINE constexpr FRotator operator-(const FRotator& B) const noexcept
    {
        return FRotator{ Pitch - B.Pitch, Yaw - B.Yaw, Roll - B.Roll };
    }

    XPACT_FORCEINLINE constexpr bool operator==(const FRotator& B) const noexcept
    {
        return Pitch == B.Pitch && Yaw == B.Yaw && Roll == B.Roll;
    }

    XPACT_FORCEINLINE constexpr bool operator!=(const FRotator& B) const noexcept { return !(*this == B); }
};

// =====================================================================
// FQuat <-> FRotator conversion bodies (declared in FQuat.h).
// =====================================================================
//
// Defined here because they depend on the full FRotator type. Both
// directions are explicit-only (locked decision 5).
//
// Degree-to-radian conversion constant: PI / 180 = 0.0174532925f
//
// FromEuler order: yaw * pitch * roll (intrinsic Z-Y-X = world Yaw-
// Pitch-Roll). Matches the FRotator banner comment above.
//
// ToEuler extracts the inverse of FromEuler. The extraction uses
// the atan2-based form which is well-conditioned everywhere except
// the gimbal-lock pole (pitch == +/- 90 degrees); at the pole the
// roll is set to zero and the entire rotation is folded into yaw.
// =====================================================================

[[nodiscard]] XPACT_FORCEINLINE FQuat FQuat::FromEuler(const FRotator& R) noexcept
{
    constexpr float DegToRad = 0.017453292519943295f;  // PI / 180

    const float HalfPitch = R.Pitch * 0.5f * DegToRad;
    const float HalfYaw   = R.Yaw   * 0.5f * DegToRad;
    const float HalfRoll  = R.Roll  * 0.5f * DegToRad;

    const float CP = ::std::cos(HalfPitch);
    const float SP = ::std::sin(HalfPitch);
    const float CY = ::std::cos(HalfYaw);
    const float SY = ::std::sin(HalfYaw);
    const float CR = ::std::cos(HalfRoll);
    const float SR = ::std::sin(HalfRoll);

    // q = Q_yaw * Q_pitch * Q_roll
    // (Z-axis * Y-axis * X-axis; intrinsic Z-Y-X = world Yaw-Pitch-Roll)
    return FQuat{
        CY * CP * SR - SY * SP * CR,  // X
        CY * SP * CR + SY * CP * SR,  // Y
        SY * CP * CR - CY * SP * SR,  // Z
        CY * CP * CR + SY * SP * SR   // W
    };
}

[[nodiscard]] XPACT_FORCEINLINE FRotator FQuat::ToEuler() const noexcept
{
    constexpr float RadToDeg = 57.29577951308232f;  // 180 / PI

    // Singularity check at the poles (pitch +/- 90 degrees).
    const float SinP = 2.0f * (W * Y - Z * X);
    if (SinP >= 1.0f)
    {
        // North pole. Roll collapses into yaw.
        const float Yaw = 2.0f * ::std::atan2(X, W);
        return FRotator{ 90.0f, Yaw * RadToDeg, 0.0f };
    }
    if (SinP <= -1.0f)
    {
        // South pole. Roll collapses into yaw.
        const float Yaw = -2.0f * ::std::atan2(X, W);
        return FRotator{ -90.0f, Yaw * RadToDeg, 0.0f };
    }

    const float Pitch = ::std::asin(SinP);
    const float Yaw   = ::std::atan2(2.0f * (W * Z + X * Y),
                                     1.0f - 2.0f * (Y * Y + Z * Z));
    const float Roll  = ::std::atan2(2.0f * (W * X + Y * Z),
                                     1.0f - 2.0f * (X * X + Y * Y));

    return FRotator{ Pitch * RadToDeg, Yaw * RadToDeg, Roll * RadToDeg };
}

// =====================================================================
// Free-function conversion aliases (Section 6.1 line 538-539).
// =====================================================================

[[nodiscard]] XPACT_FORCEINLINE FQuat QuatFromRotator(const FRotator& R) noexcept
{
    return FQuat::FromEuler(R);
}

[[nodiscard]] XPACT_FORCEINLINE FRotator RotatorFromQuat(const FQuat& Q) noexcept
{
    return Q.ToEuler();
}

static_assert(sizeof(FRotator)  == 12, "FRotator ABI lock: 12 bytes (3 floats, degrees)");
static_assert(alignof(FRotator) ==  4, "FRotator ABI lock: 4-byte aligned");

} // namespace XCore
