// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FVector.Tests/HandednessCrossProduct.cpp -- RH coordinate cross-product.
// =====================================================================
//
// XCore-4a Rev 3, Section 6.6 acceptance row + Section 17.3 C1.
//
// CONVENTION:
//   ForwardVector  = (1, 0, 0)
//   RightVector    = (0, 1, 0)
//   UpVector       = (0, 0, 1)
//   RH, Z-up, meters.
//
// CROSS-PRODUCT FORMULA (FVector.h):
//   Cross(A, B) = (A.Y*B.Z - A.Z*B.Y,
//                  A.Z*B.X - A.X*B.Z,
//                  A.X*B.Y - A.Y*B.X)
//
// Plugging in the basis:
//   Forward x Right = ((0)(0) - (0)(1),
//                       (0)(0) - (1)(0),
//                       (1)(1) - (0)(0))
//                   = (0, 0, 1)
//                   = +UpVector
//
// SPEC NOTE / DIVERGENCE FLAG (documented at FVector.h):
//   Rev 3 spec body Section 6.1 line 527 and Section 6.6 line 650
//   both assert
//       Cross(ForwardVector, RightVector) == -UpVector
//   That assertion is mathematically incorrect for the right-handed
//   basis declared in the same section (it corresponds to a LEFT-
//   handed system). This implementation follows the standard math:
//   F x R = +U is the canonical X x Y = Z identity for any RH basis.
//
//   The test below asserts the mathematically correct value. The
//   spec typo is documented at the FVector.h banner and at the
//   //TODO(spec-reconcile) marker; the main agent reconciles in Rev 4.
//
// =====================================================================

#include "Math/FVector.h"

namespace XCore::Tests::Handedness
{

// =====================================================================
// Compile-time handedness lock.
//
// Forward x Right = +Up (the canonical RH-Z-up cross-product result).
// =====================================================================

static_assert([]() constexpr {
    constexpr auto Result = ::XCore::ForwardVector.Cross(::XCore::RightVector);
    return Result == ::XCore::UpVector;
}(), "Forward x Right == +Up (RH-Z-up convention; standard X x Y = Z)");

// Symmetric checks: Right x Up == Forward, Up x Forward == Right.
static_assert([]() constexpr {
    constexpr auto Result = ::XCore::RightVector.Cross(::XCore::UpVector);
    return Result == ::XCore::ForwardVector;
}(), "Right x Up == Forward (Y x Z == X)");

static_assert([]() constexpr {
    constexpr auto Result = ::XCore::UpVector.Cross(::XCore::ForwardVector);
    return Result == ::XCore::RightVector;
}(), "Up x Forward == Right (Z x X == Y)");

// Antisymmetric checks: Right x Forward == -Up etc.
static_assert([]() constexpr {
    constexpr auto Result = ::XCore::RightVector.Cross(::XCore::ForwardVector);
    return Result.X == -::XCore::UpVector.X
        && Result.Y == -::XCore::UpVector.Y
        && Result.Z == -::XCore::UpVector.Z;
}(), "Right x Forward == -Up (antisymmetric)");

// =====================================================================
// Sanity: basis vectors are orthonormal.
// =====================================================================

static_assert(::XCore::ForwardVector.Dot(::XCore::RightVector) == 0.0f, "F . R == 0 (orthogonal)");
static_assert(::XCore::RightVector.Dot(::XCore::UpVector)      == 0.0f, "R . U == 0 (orthogonal)");
static_assert(::XCore::UpVector.Dot(::XCore::ForwardVector)    == 0.0f, "U . F == 0 (orthogonal)");

static_assert(::XCore::ForwardVector.LengthSq() == 1.0f, "F is unit-length");
static_assert(::XCore::RightVector.LengthSq()   == 1.0f, "R is unit-length");
static_assert(::XCore::UpVector.LengthSq()      == 1.0f, "U is unit-length");

} // namespace XCore::Tests::Handedness

int main()
{
    return 0;
}
