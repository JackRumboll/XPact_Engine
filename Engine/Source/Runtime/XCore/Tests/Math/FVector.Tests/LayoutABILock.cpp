// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FVector.Tests/LayoutABILock.cpp -- FVector ABI lock (12 bytes packed).
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1 fix M-17 (C# layout-identity invariant).
//
// The 12-byte packed layout is load-bearing for C# interop; XHT
// validates the C# mirror struct against this layout at every build.
// A drift here is a build error, never a runtime surprise.
//
// =====================================================================

#include "Math/FVector.h"
#include "Math/FVector2D.h"
#include "Math/FVector4.h"
#include "Math/FQuat.h"
#include "Math/FMatrix.h"
#include "Math/FLargeWorldVector.h"
#include "Math/FRotator.h"
#include "Math/FColor.h"

#include <cstddef>

namespace XCore::Tests::Layout
{

// Duplicated from each math header so the test catches drift at any
// of the four places these locks live (header + test).

// FVector: 12 bytes, 4-byte aligned (packed three floats; explicit
// NO alignas(16) to keep the C# Pack=4 mirror byte-for-byte
// identical per fix M-17).
static_assert(sizeof(::XCore::FVector)  == 12, "FVector sizeof == 12 bytes");
static_assert(alignof(::XCore::FVector) ==  4, "FVector alignof == 4 bytes");
static_assert(offsetof(::XCore::FVector, X) == 0, "FVector::X at offset 0");
static_assert(offsetof(::XCore::FVector, Y) == 4, "FVector::Y at offset 4");
static_assert(offsetof(::XCore::FVector, Z) == 8, "FVector::Z at offset 8");

// FVector2D: 8 bytes, 4-byte aligned.
static_assert(sizeof(::XCore::FVector2D)  == 8, "FVector2D sizeof == 8");
static_assert(alignof(::XCore::FVector2D) == 4, "FVector2D alignof == 4");

// FVector4: 16 bytes, 16-byte aligned (SIMD).
static_assert(sizeof(::XCore::FVector4)  == 16, "FVector4 sizeof == 16");
static_assert(alignof(::XCore::FVector4) == 16, "FVector4 alignof == 16");

// FQuat: 16 bytes, 16-byte aligned (SIMD).
static_assert(sizeof(::XCore::FQuat)  == 16, "FQuat sizeof == 16");
static_assert(alignof(::XCore::FQuat) == 16, "FQuat alignof == 16");

// FMatrix: 64 bytes, 16-byte aligned.
static_assert(sizeof(::XCore::FMatrix)  == 64, "FMatrix sizeof == 64");
static_assert(alignof(::XCore::FMatrix) == 16, "FMatrix alignof == 16");

// FLargeWorldVector: 24 bytes, 8-byte aligned.
static_assert(sizeof(::XCore::FLargeWorldVector)  == 24, "FLargeWorldVector sizeof == 24");
static_assert(alignof(::XCore::FLargeWorldVector) ==  8, "FLargeWorldVector alignof == 8");

// FRotator: 12 bytes, 4-byte aligned.
static_assert(sizeof(::XCore::FRotator)  == 12, "FRotator sizeof == 12");
static_assert(alignof(::XCore::FRotator) ==  4, "FRotator alignof == 4");

// FColor: 4 bytes (BGRA order).
static_assert(sizeof(::XCore::FColor)  == 4, "FColor sizeof == 4");
static_assert(alignof(::XCore::FColor) == 1, "FColor alignof == 1");

} // namespace XCore::Tests::Layout

int main()
{
    return 0;
}
