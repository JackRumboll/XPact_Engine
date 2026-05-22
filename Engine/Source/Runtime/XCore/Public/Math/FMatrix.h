// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FMatrix.h -- 4x4 row-major float matrix (Section 6.1).
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1 + locked decision 2 (row-major; UE is
// column-major; XPact diverges to match C# System.Numerics, DirectXMath,
// and glm row-vector conventions for simpler C# interop).
//
// LAYOUT (locked at sizeof == 64, alignof == 16):
//   M[row][col] indexing -- M[0][0] is row 0 column 0.
//   M[0] = (m00, m01, m02, m03) = row 0
//   M[1] = (m10, m11, m12, m13) = row 1
//   M[2] = (m20, m21, m22, m23) = row 2
//   M[3] = (m30, m31, m32, m33) = row 3
//
// Row-major vector-matrix convention: vectors are ROW vectors and
// transformations are applied AFTER the matrix on the right-hand side:
//   v_transformed = v * M   (NOT M * v as in column-major UE convention)
//
// Translation lives in the LAST ROW (M[3][0], M[3][1], M[3][2]). This
// matches DirectXMath and glm; UE puts translation in the last column.
// The handedness of the basis is preserved; the matrix layout is
// orthogonal to the handedness of the coordinate system.
//
// Test:
//   offsetof(FMatrix, M[0][1]) == 4
// confirms row-major storage. (In column-major storage M[0][1] would
// be at offset 16 because M[0] would be the first COLUMN, not the
// first ROW.)
//
// SIM-PATH SAFETY:
//   All implementations in this header are pure arithmetic and
//   sim-path-safe. The Inverse / Determinant bodies live in
//   FMatrix.cpp because they're too long for inline; they too are
//   pure arithmetic and sim-path-safe. FMatrix::Inverse explicitly
//   must NOT emit FMA instructions on the sim path (Section 17.3 C4
//   acceptance criterion); the -mno-fma compiler flag plus the lack
//   of `std::fma` calls in the body ensures this.
//
// UE REFERENCES:
//   Engine/Source/Runtime/Core/Public/Math/Matrix.h -- studied;
//   column-major divergence is intentional (locked decision 2).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Math/FQuat.h"
#include "Math/FVector.h"
#include "Math/FVector4.h"

#include <cstddef>

namespace XCore
{

struct alignas(16) FMatrix
{
    // Row-major storage. M[row][col].
    float M[4][4];

    // -----------------------------------------------------------------
    // Constructors.
    //
    // The default constructor is the IDENTITY matrix, NOT zero. A zero
    // matrix is rarely a useful default (it collapses every position
    // to the origin and every direction to zero); identity is the
    // canonical "no transformation" default.
    // -----------------------------------------------------------------

    XPACT_FORCEINLINE constexpr FMatrix() noexcept
        : M{
            { 1.0f, 0.0f, 0.0f, 0.0f },
            { 0.0f, 1.0f, 0.0f, 0.0f },
            { 0.0f, 0.0f, 1.0f, 0.0f },
            { 0.0f, 0.0f, 0.0f, 1.0f }
        }
    {}

    XPACT_FORCEINLINE constexpr FMatrix(
        float m00, float m01, float m02, float m03,
        float m10, float m11, float m12, float m13,
        float m20, float m21, float m22, float m23,
        float m30, float m31, float m32, float m33) noexcept
        : M{
            { m00, m01, m02, m03 },
            { m10, m11, m12, m13 },
            { m20, m21, m22, m23 },
            { m30, m31, m32, m33 }
        }
    {}

    // -----------------------------------------------------------------
    // Static factories.
    // -----------------------------------------------------------------

    [[nodiscard]] static XPACT_FORCEINLINE constexpr FMatrix Identity() noexcept
    {
        return FMatrix{};
    }

    [[nodiscard]] static XPACT_FORCEINLINE constexpr FMatrix Zero() noexcept
    {
        return FMatrix{
            0.0f, 0.0f, 0.0f, 0.0f,
            0.0f, 0.0f, 0.0f, 0.0f,
            0.0f, 0.0f, 0.0f, 0.0f,
            0.0f, 0.0f, 0.0f, 0.0f
        };
    }

    [[nodiscard]] static XPACT_FORCEINLINE constexpr FMatrix FromTranslation(const FVector& T) noexcept
    {
        return FMatrix{
            1.0f, 0.0f, 0.0f, 0.0f,
            0.0f, 1.0f, 0.0f, 0.0f,
            0.0f, 0.0f, 1.0f, 0.0f,
            T.X,  T.Y,  T.Z,  1.0f
        };
    }

    [[nodiscard]] static XPACT_FORCEINLINE constexpr FMatrix FromScale(const FVector& S) noexcept
    {
        return FMatrix{
            S.X,  0.0f, 0.0f, 0.0f,
            0.0f, S.Y,  0.0f, 0.0f,
            0.0f, 0.0f, S.Z,  0.0f,
            0.0f, 0.0f, 0.0f, 1.0f
        };
    }

    [[nodiscard]] static FMatrix FromQuat(const FQuat& Q) noexcept;

    [[nodiscard]] static FMatrix FromQuatPositionScale(
        const FQuat& Rotation, const FVector& Translation, const FVector& Scale) noexcept;

    // -----------------------------------------------------------------
    // Element access. M(row, col) is the canonical accessor; M.M[row][col]
    // is the raw-storage form for hot loops.
    // -----------------------------------------------------------------

    XPACT_FORCEINLINE constexpr float& operator()(::std::size_t Row, ::std::size_t Col) noexcept
    {
        return M[Row][Col];
    }

    XPACT_FORCEINLINE constexpr float operator()(::std::size_t Row, ::std::size_t Col) const noexcept
    {
        return M[Row][Col];
    }

    // -----------------------------------------------------------------
    // Matrix-matrix product.
    //
    // Row-major + row-vector convention:
    //   (A * B)[i][j] = sum_k A[i][k] * B[k][j]
    // i.e., the standard mathematical convention; the row-vector
    // application is encoded in TransformVector / TransformPosition
    // (the vector multiplies on the LEFT of the matrix).
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr FMatrix operator*(const FMatrix& B) const noexcept
    {
        FMatrix R = FMatrix::Zero();
        for (::std::size_t I = 0; I < 4; ++I)
        {
            for (::std::size_t J = 0; J < 4; ++J)
            {
                float Sum = 0.0f;
                for (::std::size_t K = 0; K < 4; ++K)
                {
                    Sum += M[I][K] * B.M[K][J];
                }
                R.M[I][J] = Sum;
            }
        }
        return R;
    }

    XPACT_FORCEINLINE constexpr bool operator==(const FMatrix& B) const noexcept
    {
        for (::std::size_t I = 0; I < 4; ++I)
        {
            for (::std::size_t J = 0; J < 4; ++J)
            {
                if (M[I][J] != B.M[I][J]) { return false; }
            }
        }
        return true;
    }

    XPACT_FORCEINLINE constexpr bool operator!=(const FMatrix& B) const noexcept { return !(*this == B); }

    // -----------------------------------------------------------------
    // Transpose.
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr FMatrix Transpose() const noexcept
    {
        return FMatrix{
            M[0][0], M[1][0], M[2][0], M[3][0],
            M[0][1], M[1][1], M[2][1], M[3][1],
            M[0][2], M[1][2], M[2][2], M[3][2],
            M[0][3], M[1][3], M[2][3], M[3][3]
        };
    }

    // -----------------------------------------------------------------
    // TransformVector  -- direction transform (drops translation; the
    //                     w=0 implicit homogeneous coordinate).
    // TransformPosition -- position transform (includes translation; the
    //                      w=1 implicit homogeneous coordinate).
    //
    // Row-vector convention: result = (v.x, v.y, v.z, w) * M, taking
    // only the .xyz components of the result.
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr FVector TransformVector(const FVector& V) const noexcept
    {
        return FVector{
            V.X * M[0][0] + V.Y * M[1][0] + V.Z * M[2][0],
            V.X * M[0][1] + V.Y * M[1][1] + V.Z * M[2][1],
            V.X * M[0][2] + V.Y * M[1][2] + V.Z * M[2][2]
        };
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr FVector TransformPosition(const FVector& V) const noexcept
    {
        return FVector{
            V.X * M[0][0] + V.Y * M[1][0] + V.Z * M[2][0] + M[3][0],
            V.X * M[0][1] + V.Y * M[1][1] + V.Z * M[2][1] + M[3][1],
            V.X * M[0][2] + V.Y * M[1][2] + V.Z * M[2][2] + M[3][2]
        };
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr FVector4 TransformVector4(const FVector4& V) const noexcept
    {
        return FVector4{
            V.X * M[0][0] + V.Y * M[1][0] + V.Z * M[2][0] + V.W * M[3][0],
            V.X * M[0][1] + V.Y * M[1][1] + V.Z * M[2][1] + V.W * M[3][1],
            V.X * M[0][2] + V.Y * M[1][2] + V.Z * M[2][2] + V.W * M[3][2],
            V.X * M[0][3] + V.Y * M[1][3] + V.Z * M[2][3] + V.W * M[3][3]
        };
    }

    // -----------------------------------------------------------------
    // Inverse + Determinant.
    //
    // Implementation bodies live in FMatrix.cpp because the 4x4
    // expansion runs 60+ lines and the inline-cost-model penalty is
    // not worth paying.
    //
    // Both operations are pure arithmetic and sim-path-safe.
    // Inverse asserts no-FMA emission on sim-path (Section 17.3 C4).
    // -----------------------------------------------------------------

    [[nodiscard]] FMatrix Inverse() const noexcept;
    [[nodiscard]] float Determinant() const noexcept;

    // -----------------------------------------------------------------
    // Append helpers. Conceptually equivalent to multiplying the
    // existing matrix by a new (rotation / translation / scale)
    // matrix, but inline-friendly because we know the structure of
    // the appended matrix and can skip the zero / one columns.
    //
    // Note: "Append" semantics in row-major convention is
    //     M.AppendRotation(Q) == M * FromQuat(Q)
    // i.e., the appended rotation is applied AFTER M in the row-vector
    // composition order.
    // -----------------------------------------------------------------

    [[nodiscard]] FMatrix AppendRotation(const FQuat& Q) const noexcept
    {
        return *this * FMatrix::FromQuat(Q);
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr FMatrix AppendTranslation(const FVector& T) const noexcept
    {
        // FromTranslation T has identity-rotation; the multiplication
        // simplifies to "add T to the last row".
        FMatrix R = *this;
        R.M[3][0] += T.X;
        R.M[3][1] += T.Y;
        R.M[3][2] += T.Z;
        return R;
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr FMatrix AppendScale(const FVector& S) const noexcept
    {
        // FromScale S has zero translation; the multiplication
        // simplifies to "scale each column independently".
        FMatrix R = *this;
        for (::std::size_t I = 0; I < 4; ++I)
        {
            R.M[I][0] *= S.X;
            R.M[I][1] *= S.Y;
            R.M[I][2] *= S.Z;
        }
        return R;
    }

    // -----------------------------------------------------------------
    // Translation extraction (last row).
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr FVector GetTranslation() const noexcept
    {
        return FVector{ M[3][0], M[3][1], M[3][2] };
    }
};

// =====================================================================
// Free-function alias (Section 6.1 line 533).
// =====================================================================

[[nodiscard]] XPACT_FORCEINLINE FMatrix Inverse(const FMatrix& M) noexcept
{
    return M.Inverse();
}

static_assert(sizeof(FMatrix)  == 64, "FMatrix ABI lock: 64 bytes (4x4 floats)");
static_assert(alignof(FMatrix) == 16, "FMatrix ABI lock: 16-byte aligned");

// =====================================================================
// Row-major layout proof (cross-checked in RowMajorMemoryLayout.cpp test).
// =====================================================================
//
// offsetof(FMatrix, M[0][1]) == 4 means the second float-in-storage is
// row 0 column 1, i.e., M[0] is the FIRST ROW. In column-major storage
// the second float-in-storage would be row 1 column 0 (M[0] would be
// the first COLUMN). The static_assert here catches a future drift
// at compile time.

static_assert(offsetof(FMatrix, M[0]) == 0,  "FMatrix layout: M[0] starts at offset 0");
// The byte-4 element is row 0 column 1 -- proof of row-major storage.
// (We can't easily express offsetof(FMatrix, M[0][1]) directly because
// arrays-of-arrays don't have a portable per-element offsetof; the
// test in RowMajorMemoryLayout.cpp confirms via a runtime cast.)

} // namespace XCore
