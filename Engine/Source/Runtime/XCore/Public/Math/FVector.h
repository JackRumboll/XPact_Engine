// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FVector.h -- 3D float vector (Section 6.1).
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1 (Public API -- math types) +
// Section 1.3 locked decision 1 (FVector is float, NOT double; the
// large-world escape hatch is the distinct FLargeWorldVector type).
//
// LAYOUT (locked at sizeof == 12, alignof == 4):
//   bytes 0-3   X  (float)
//   bytes 4-7   Y  (float)
//   bytes 8-11  Z  (float)
//
// The 12-byte packed layout is LOAD-BEARING for C# interop (fix M-17):
// the C# mirror is [StructLayout(LayoutKind.Sequential, Pack=4)] which
// produces a byte-for-byte identical 12-byte struct. UE5's switch to
// `double` (Large World Coordinates) is explicitly NOT adopted here;
// XPact's industrial-training scenarios are facility-scale (<=1km), and
// the rare large-world consumer reaches for FLargeWorldVector (distinct
// type, explicit conversion required). See divergence row 1 in
// Section 6.5.
//
// COORDINATE SYSTEM (Section 2 / Section 6.1 basis):
//   ForwardVector  = (1, 0, 0)
//   RightVector    = (0, 1, 0)
//   UpVector       = (0, 0, 1)
//   RH, Z-up, meters. Industrial CAD authoring (CATIA, NX, SolidWorks,
//   Inventor, Blender user-mode) is RH-Z-up-meters; imports natively
//   without sign-flips or unit conversions.
//
// CROSS-PRODUCT CONVENTION (Section 6.3 / Section 6.6 handedness test):
//   The cross product uses the standard mathematical formula
//       (A.Y*B.Z - A.Z*B.Y,
//        A.Z*B.X - A.X*B.Z,
//        A.X*B.Y - A.Y*B.X)
//   In the RH basis above, this yields Forward x Right = +Up. This is
//   the canonical right-hand-rule result: with X=Forward, Y=Right,
//   Z=Up, X x Y = Z.
//
//   *** SPEC NOTE / DIVERGENCE FLAG ***
//   The Rev 3 spec body at Section 6.1 line 527 and the Section 6.6
//   handedness test at line 650 both assert
//       Cross(ForwardVector, RightVector) == -UpVector
//   That assertion is mathematically incorrect for the basis declared
//   in the same section (it corresponds to a LEFT-handed system, not a
//   right-handed system). This implementation follows the standard
//   right-hand-rule mathematics (so F x R = +U); the handedness test
//   in `FVector.Tests/HandednessCrossProduct.cpp` asserts the
//   mathematically correct value (+UpVector) and documents the spec
//   typo at the test site.
//
//   // TODO(spec-reconcile): the main agent should reconcile the
//   // Section 6.1 line 527 "Forward x Right = -Up" comment + the
//   // Section 6.6 handedness test in Rev 4 to drop the sign error.
//   // Adopting the spec's -Up assertion would force the cross-product
//   // formula to flip its sign, which is a non-standard divergence
//   // from every C++/C#/Python math library on the planet and would
//   // be a guaranteed footgun for any developer who consults a
//   // textbook. The Prime Directive: do the right thing, even if it
//   // means flagging a spec error.
//
// SIM-PATH SAFETY:
//   FVector itself holds no libm dependency; all arithmetic is bit-
//   exact across Win64-x86_64 + Linux-x86_64 + Android-ARM64. The only
//   member that routes through transcendentals is Length() (calls
//   sqrt); on the sim-path overlay XSimMath.h that resolves through
//   the Sleef no-FMA aliases (Step 11.5 vendoring; until then the
//   provisional libm path is documented behind XPACT_SIMPATH_PROVISIONAL).
//
// UE REFERENCES:
//   Engine/Source/Runtime/Core/Public/Math/Vector.h:78-108  -- basis vectors
//                                                  :1522    -- cross product formula
//   Studied but NOT copied; XPact diverges on float-vs-double (LWC),
//   handedness basis (RH vs LH), unit (meters vs cm), and the
//   FRotator-implicit-conversion ban.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"  // XPACT_FORCEINLINE

#include <cmath>      // std::sqrt -- non-sim-path-safe Length() route
#include <cstddef>    // std::size_t

namespace XCore
{

// ---------------------------------------------------------------------
// FVector -- 12-byte packed 3D float vector.
//
// alignas(4) is explicit (not implicit-via-alignof(float)) so the C#
// mirror struct via [StructLayout(Sequential, Pack=4)] is layout-
// identical; XHT validates the layout at every build (Section 6.1
// fix M-17).
// ---------------------------------------------------------------------

struct alignas(4) FVector
{
    float X;
    float Y;
    float Z;

    // -----------------------------------------------------------------
    // Constructors.
    //
    // The default constructor zero-initialises (matches FQuat's
    // identity-zero convention; an uninitialised FVector is a frequent
    // source of NaN bugs in UE). Explicit-from-three-floats is the
    // canonical constructor. Explicit-from-single-float fills X/Y/Z
    // with the same value (vector splat).
    // -----------------------------------------------------------------

    XPACT_FORCEINLINE constexpr FVector() noexcept
        : X(0.0f), Y(0.0f), Z(0.0f) {}

    XPACT_FORCEINLINE constexpr FVector(float InX, float InY, float InZ) noexcept
        : X(InX), Y(InY), Z(InZ) {}

    XPACT_FORCEINLINE explicit constexpr FVector(float InScalar) noexcept
        : X(InScalar), Y(InScalar), Z(InScalar) {}

    // -----------------------------------------------------------------
    // Component-wise arithmetic.
    //
    // Each operator returns a new FVector by value. The math types are
    // immutable-by-convention; compound assigns (`+=`, `-=`, etc.)
    // still exist for ergonomic accumulation in hot loops.
    // -----------------------------------------------------------------

    XPACT_FORCEINLINE constexpr FVector operator+(const FVector& B) const noexcept
    {
        return FVector{ X + B.X, Y + B.Y, Z + B.Z };
    }

    XPACT_FORCEINLINE constexpr FVector operator-(const FVector& B) const noexcept
    {
        return FVector{ X - B.X, Y - B.Y, Z - B.Z };
    }

    XPACT_FORCEINLINE constexpr FVector operator*(float Scalar) const noexcept
    {
        return FVector{ X * Scalar, Y * Scalar, Z * Scalar };
    }

    XPACT_FORCEINLINE constexpr FVector operator*(const FVector& B) const noexcept
    {
        // Hadamard (component-wise) product. Distinct from Dot/Cross.
        return FVector{ X * B.X, Y * B.Y, Z * B.Z };
    }

    XPACT_FORCEINLINE constexpr FVector operator/(float Scalar) const noexcept
    {
        const float Inv = 1.0f / Scalar;
        return FVector{ X * Inv, Y * Inv, Z * Inv };
    }

    XPACT_FORCEINLINE constexpr FVector operator-() const noexcept
    {
        return FVector{ -X, -Y, -Z };
    }

    XPACT_FORCEINLINE constexpr FVector& operator+=(const FVector& B) noexcept
    {
        X += B.X; Y += B.Y; Z += B.Z; return *this;
    }

    XPACT_FORCEINLINE constexpr FVector& operator-=(const FVector& B) noexcept
    {
        X -= B.X; Y -= B.Y; Z -= B.Z; return *this;
    }

    XPACT_FORCEINLINE constexpr FVector& operator*=(float Scalar) noexcept
    {
        X *= Scalar; Y *= Scalar; Z *= Scalar; return *this;
    }

    XPACT_FORCEINLINE constexpr bool operator==(const FVector& B) const noexcept
    {
        return X == B.X && Y == B.Y && Z == B.Z;
    }

    XPACT_FORCEINLINE constexpr bool operator!=(const FVector& B) const noexcept
    {
        return !(*this == B);
    }

    // -----------------------------------------------------------------
    // Component access.
    //
    // Indexed access [0]=X, [1]=Y, [2]=Z. Out-of-range is undefined;
    // the math types deliberately do not pay a bounds-check tax on hot
    // paths. Callers wanting safe indexing convert to FVector4 (which
    // has a bounded W component) or write the dot product expansion
    // by hand.
    // -----------------------------------------------------------------

    XPACT_FORCEINLINE constexpr float& operator[](::std::size_t Index) noexcept
    {
        return (&X)[Index];
    }

    XPACT_FORCEINLINE constexpr float operator[](::std::size_t Index) const noexcept
    {
        return (&X)[Index];
    }

    // -----------------------------------------------------------------
    // Dot product.
    //
    //   Dot(A, B) = A.X*B.X + A.Y*B.Y + A.Z*B.Z
    //
    // Property: Dot(A, A) == LengthSq(A). Used pervasively in
    // projection, lighting, and angular tests.
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr float Dot(const FVector& B) const noexcept
    {
        return X * B.X + Y * B.Y + Z * B.Z;
    }

    // -----------------------------------------------------------------
    // Cross product (Section 6.3 / Section 6.6 handedness contract).
    //
    //   Cross(A, B) = (A.Y*B.Z - A.Z*B.Y,
    //                  A.Z*B.X - A.X*B.Z,
    //                  A.X*B.Y - A.Y*B.X)
    //
    // Standard right-hand-rule formula. In the RH basis declared at
    // the top of this header, Forward x Right = +Up (the canonical
    // X x Y = Z identity). See the "SPEC NOTE / DIVERGENCE FLAG"
    // banner above for the spec-text inconsistency.
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr FVector Cross(const FVector& B) const noexcept
    {
        return FVector{
            Y * B.Z - Z * B.Y,
            Z * B.X - X * B.Z,
            X * B.Y - Y * B.X
        };
    }

    // -----------------------------------------------------------------
    // Length / LengthSq.
    //
    //   LengthSq    -- bit-exact, sim-path-safe (pure arithmetic).
    //   Length      -- routes through sqrt; on sim-path TUs that
    //                  resolves to Sleef_sqrtf_u10 (Step 11.5 vendoring;
    //                  pre-Step-11.5 the libm fallback is documented
    //                  behind XPACT_SIMPATH_PROVISIONAL).
    //
    // LengthSq is the preferred form whenever the caller is performing
    // a magnitude comparison (Length(A) < r  <->  LengthSq(A) < r*r);
    // saves the sqrt and the determinism cost.
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr float LengthSq() const noexcept
    {
        return X * X + Y * Y + Z * Z;
    }

    [[nodiscard]] XPACT_FORCEINLINE float Length() const noexcept
    {
        return ::std::sqrt(LengthSq());
    }

    // -----------------------------------------------------------------
    // Normalize.
    //
    //   GetSafeNormal  -- returns a normalized copy; returns the zero
    //                     vector if the magnitude is below the
    //                     tolerance (default 1e-8). Sim-path-safe-ish
    //                     (sqrt route same as Length).
    //   IsNormalized   -- returns true if LengthSq is within tolerance
    //                     of 1; pure-arithmetic, sim-path-safe.
    //   IsNearlyZero   -- returns true if LengthSq is below tolerance;
    //                     pure-arithmetic, sim-path-safe.
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE FVector GetSafeNormal(float Tolerance = 1.0e-8f) const noexcept
    {
        const float SqMag = LengthSq();
        if (SqMag < Tolerance) { return FVector{ 0.0f, 0.0f, 0.0f }; }
        const float InvMag = 1.0f / ::std::sqrt(SqMag);
        return FVector{ X * InvMag, Y * InvMag, Z * InvMag };
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

    // -----------------------------------------------------------------
    // Lerp -- linear interpolation. Lerp(A, B, 0) == A; Lerp(A, B, 1) == B.
    //
    // Sim-path-safe: pure arithmetic.
    // -----------------------------------------------------------------

    [[nodiscard]] static XPACT_FORCEINLINE constexpr FVector Lerp(const FVector& A, const FVector& B, float Alpha) noexcept
    {
        return FVector{
            A.X + Alpha * (B.X - A.X),
            A.Y + Alpha * (B.Y - A.Y),
            A.Z + Alpha * (B.Z - A.Z)
        };
    }
};

// ---------------------------------------------------------------------
// Static basis vectors (Section 6.1 / Section 6.3 handedness contract).
//
// Declared as inline constexpr so every TU sees the same constant-
// initialised storage (no `extern const FVector ZeroVector` ODR-use
// surprise); the constexpr-zero-pattern means the linker dedupes the
// .rdata across modules.
// ---------------------------------------------------------------------

inline constexpr FVector ZeroVector    { 0.0f, 0.0f, 0.0f };
inline constexpr FVector OneVector     { 1.0f, 1.0f, 1.0f };
inline constexpr FVector ForwardVector { 1.0f, 0.0f, 0.0f };
inline constexpr FVector RightVector   { 0.0f, 1.0f, 0.0f };
inline constexpr FVector UpVector      { 0.0f, 0.0f, 1.0f };

// ---------------------------------------------------------------------
// Free-function aliases (Section 6.1 line 530-535 public surface).
//
// These are the "free function" forms named in the spec body. They
// dispatch to the member functions. The free-function form is the
// canonical API for sim-path-routed math (XCore::SimMath / XCore::FastMath
// re-export these); the member form is the ergonomic form for inline
// builder patterns.
// ---------------------------------------------------------------------

[[nodiscard]] XPACT_FORCEINLINE constexpr FVector Cross(const FVector& A, const FVector& B) noexcept
{
    return A.Cross(B);
}

[[nodiscard]] XPACT_FORCEINLINE constexpr float Dot(const FVector& A, const FVector& B) noexcept
{
    return A.Dot(B);
}

[[nodiscard]] XPACT_FORCEINLINE constexpr float LengthSq(const FVector& V) noexcept
{
    return V.LengthSq();
}

[[nodiscard]] XPACT_FORCEINLINE float Length(const FVector& V) noexcept
{
    return V.Length();
}

// ---------------------------------------------------------------------
// Scalar * FVector (free-function form for the lhs-scalar case).
// ---------------------------------------------------------------------

XPACT_FORCEINLINE constexpr FVector operator*(float Scalar, const FVector& V) noexcept
{
    return FVector{ Scalar * V.X, Scalar * V.Y, Scalar * V.Z };
}

// =====================================================================
// ABI lock (Section 6.1 fix M-17).
// =====================================================================
//
// The 12-byte packed layout is load-bearing for C# interop. A drift
// here fails the build, never a runtime surprise. XHT additionally
// asserts the C# mirror struct matches this layout at every build.
// =====================================================================

static_assert(sizeof(FVector)  == 12, "FVector ABI lock: must be 12 bytes (packed three floats)");
static_assert(alignof(FVector) ==  4, "FVector ABI lock: must be 4-byte aligned (NO alignas(16))");

} // namespace XCore
