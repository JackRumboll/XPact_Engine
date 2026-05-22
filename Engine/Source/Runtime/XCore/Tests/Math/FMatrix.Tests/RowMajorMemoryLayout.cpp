// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FMatrix.Tests/RowMajorMemoryLayout.cpp -- locked decision 2 proof.
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1 locked decision 2: FMatrix is ROW-MAJOR
// (UE is column-major; XPact diverges for C# System.Numerics compat).
//
// Proof of layout:
//   * M[0][1] (row 0, col 1) is at offset 4 (the second float-in-storage).
//   * M[1][0] (row 1, col 0) is at offset 16 (the fifth float-in-storage).
//   In column-major layout these would be swapped (M[0][1] at offset 16,
//   M[1][0] at offset 4); the offsets here catch a future drift at
//   build time.
//
// =====================================================================

#include "Math/FMatrix.h"

#include <cstddef>
#include <cstdio>

namespace XCore::Tests::RowMajor
{

// The offsetof of a 2D array element is not portable as a single
// constant expression on every compiler; use the cast-based form.
// At runtime, write a known value into M[row][col] and read the
// underlying float array.

[[nodiscard]] bool VerifyOffset(int Row, int Col, ::std::ptrdiff_t ExpectedOffset)
{
    ::XCore::FMatrix M = ::XCore::FMatrix::Zero();
    M.M[Row][Col] = 1.0f;
    // Treat the matrix as a flat 16-float array and find the index
    // where the 1.0 lives.
    const float* Flat = reinterpret_cast<const float*>(&M);
    for (int I = 0; I < 16; ++I)
    {
        if (Flat[I] == 1.0f)
        {
            const ::std::ptrdiff_t Offset = static_cast<::std::ptrdiff_t>(I) * static_cast<::std::ptrdiff_t>(sizeof(float));
            if (Offset != ExpectedOffset)
            {
                std::fprintf(stderr,
                    "FAIL: M[%d][%d] at offset %ld, expected %ld (row-major would be %ld)\n",
                    Row, Col, static_cast<long>(Offset), static_cast<long>(ExpectedOffset),
                    static_cast<long>((Row * 4 + Col) * sizeof(float)));
                return false;
            }
            return true;
        }
    }
    return false;
}

} // namespace XCore::Tests::RowMajor

int main()
{
    using ::XCore::Tests::RowMajor::VerifyOffset;

    // Row-major: M[row][col] is at offset (row * 4 + col) * 4.
    if (!VerifyOffset(0, 0, 0))  return 1;
    if (!VerifyOffset(0, 1, 4))  return 1;  // The load-bearing assertion: row-major proof.
    if (!VerifyOffset(0, 2, 8))  return 1;
    if (!VerifyOffset(0, 3, 12)) return 1;
    if (!VerifyOffset(1, 0, 16)) return 1;  // Row-major: row 1 starts at offset 16.
    if (!VerifyOffset(1, 1, 20)) return 1;
    if (!VerifyOffset(2, 0, 32)) return 1;
    if (!VerifyOffset(3, 3, 60)) return 1;

    return 0;
}
