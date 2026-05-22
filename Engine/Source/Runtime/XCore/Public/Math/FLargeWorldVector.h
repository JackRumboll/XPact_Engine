// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FLargeWorldVector.h -- 24-byte large-world (double) vector escape hatch.
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1 + locked decision 1.
//
// FVector stays `float` to keep cache and GPU bandwidth low on Quest 3
// for industrial-training-scale scenes (facility-scale; <=1km). The
// rare consumer with a >1km scene reaches for THIS distinct type
// (FLargeWorldVector) instead of UE5's implicit FVector-becomes-double
// pattern. The type difference forces the developer to make the
// precision trade-off VISIBLE -- a 1-character variable name change
// from `FVector` to `FLargeWorldVector` documents the intent at every
// declaration site.
//
// EXPLICIT-CONVERSION-ONLY CONTRACT (locked decision 1):
//   * No implicit FVector -> FLargeWorldVector (would silently widen
//     and break the cache-bandwidth invariant for callers that meant
//     to stay in float).
//   * No implicit FLargeWorldVector -> FVector (would silently
//     truncate and lose the precision the caller paid for).
//   * Explicit FromFVector / ToFVector conversion methods only.
//
// LAYOUT (locked at sizeof == 24, alignof == 8):
//   bytes  0-7   X  (double)
//   bytes  8-15  Y  (double)
//   bytes 16-23  Z  (double)
//
// alignas(8) is the natural alignment of double; the prompt's
// "alignas(8)" matches the implementation. The spec body at Section 6.1
// line 514 writes "alignas(32)" but the static_assert at
// `static_assert(sizeof(FLargeWorldVector) == 24)` per the prompt's
// instructions implies 24-byte packed (no 32-byte padding). The
// engineering principles ("absolute capability; no silent overpadding")
// resolve in favour of the prompt's 24-byte ABI lock: a 32-byte
// alignment would force the struct to be 32 bytes via end-padding (or
// be a sub-object that wastes 8 bytes per instance). 8-byte alignment
// keeps the type at exactly 24 bytes per instance.
//
//   // TODO(spec-reconcile): the main agent should reconcile the
//   // Section 6.1 line 514 "alignas(32)" with the 24-byte ABI lock
//   // here in Rev 4. The 24-byte locked size and the 32-byte alignment
//   // are mutually exclusive; the prompt's "alignas(8)" + the 24-byte
//   // static_assert is the consistent pair.
//
// SIM-PATH SAFETY:
//   All arithmetic is bit-exact across all three platforms; double
//   arithmetic is fully deterministic with -ffp-contract=off + no-FMA.
//   Length() routes through sqrt (same Sleef-routing rules).
//
// UE REFERENCES:
//   Engine/Source/Runtime/Core/Public/Math/MathFwd.h:46  -- UE5's
//   LWC type alias (TVector<double>). Studied; explicit-type-difference
//   chosen over UE5's implicit double-everywhere.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Math/FVector.h"  // explicit conversion methods name FVector

#include <cmath>

namespace XCore
{

struct alignas(8) FLargeWorldVector
{
    double X;
    double Y;
    double Z;

    XPACT_FORCEINLINE constexpr FLargeWorldVector() noexcept : X(0.0), Y(0.0), Z(0.0) {}
    XPACT_FORCEINLINE constexpr FLargeWorldVector(double InX, double InY, double InZ) noexcept
        : X(InX), Y(InY), Z(InZ) {}
    XPACT_FORCEINLINE explicit constexpr FLargeWorldVector(double InScalar) noexcept
        : X(InScalar), Y(InScalar), Z(InScalar) {}

    // -----------------------------------------------------------------
    // Explicit conversion to / from FVector (locked decision 1).
    //
    // Both directions are EXPLICIT. The caller has to write the cast
    // out so the precision trade-off is auditable at every conversion
    // site. The conversion is `static_cast<double>` / `static_cast<float>`
    // which is the standard IEEE-754-defined widening / truncation
    // (round-to-nearest-even at narrowing).
    // -----------------------------------------------------------------

    [[nodiscard]] static XPACT_FORCEINLINE constexpr FLargeWorldVector FromFVector(const FVector& V) noexcept
    {
        return FLargeWorldVector{
            static_cast<double>(V.X),
            static_cast<double>(V.Y),
            static_cast<double>(V.Z)
        };
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr FVector ToFVector() const noexcept
    {
        return FVector{
            static_cast<float>(X),
            static_cast<float>(Y),
            static_cast<float>(Z)
        };
    }

    // -----------------------------------------------------------------
    // Arithmetic. Mirrors FVector's API but in double precision.
    // -----------------------------------------------------------------

    XPACT_FORCEINLINE constexpr FLargeWorldVector operator+(const FLargeWorldVector& B) const noexcept
    {
        return FLargeWorldVector{ X + B.X, Y + B.Y, Z + B.Z };
    }

    XPACT_FORCEINLINE constexpr FLargeWorldVector operator-(const FLargeWorldVector& B) const noexcept
    {
        return FLargeWorldVector{ X - B.X, Y - B.Y, Z - B.Z };
    }

    XPACT_FORCEINLINE constexpr FLargeWorldVector operator*(double Scalar) const noexcept
    {
        return FLargeWorldVector{ X * Scalar, Y * Scalar, Z * Scalar };
    }

    XPACT_FORCEINLINE constexpr bool operator==(const FLargeWorldVector& B) const noexcept
    {
        return X == B.X && Y == B.Y && Z == B.Z;
    }

    XPACT_FORCEINLINE constexpr bool operator!=(const FLargeWorldVector& B) const noexcept { return !(*this == B); }

    [[nodiscard]] XPACT_FORCEINLINE constexpr double Dot(const FLargeWorldVector& B) const noexcept
    {
        return X * B.X + Y * B.Y + Z * B.Z;
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr FLargeWorldVector Cross(const FLargeWorldVector& B) const noexcept
    {
        return FLargeWorldVector{
            Y * B.Z - Z * B.Y,
            Z * B.X - X * B.Z,
            X * B.Y - Y * B.X
        };
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr double LengthSq() const noexcept
    {
        return X * X + Y * Y + Z * Z;
    }

    [[nodiscard]] XPACT_FORCEINLINE double Length() const noexcept { return ::std::sqrt(LengthSq()); }
};

static_assert(sizeof(FLargeWorldVector)  == 24, "FLargeWorldVector ABI lock: 24 bytes (3 doubles, no padding)");
static_assert(alignof(FLargeWorldVector) ==  8, "FLargeWorldVector ABI lock: 8-byte aligned");

} // namespace XCore
