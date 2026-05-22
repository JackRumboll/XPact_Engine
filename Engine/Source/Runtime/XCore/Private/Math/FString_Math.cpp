// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FString_Math.cpp -- ToString helpers for FVector / FQuat / FMatrix.
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1 + Section 11.1 (FString interop).
//
// The math types are header-only inline (the .h files are largely
// constexpr); ToString is the lone surface that requires FString
// (heap allocation, format) and so lives in its own .cpp.
//
// PHASE 1E STATUS:
//   FString::Format (Phase 1d Subagent A) is a partial stub on some
//   toolchains. We use FString::FromFloat directly (which goes via
//   std::to_chars and is sim-path-safe) rather than the locale-
//   dependent Format. The output format is deliberate-and-readable:
//
//     FVector::ToString -> "X=1.500 Y=2.500 Z=3.500"
//     FQuat::ToString   -> "X=0.0 Y=0.0 Z=0.0 W=1.0"
//     FMatrix::ToString -> 4 rows of "X.X X.X X.X X.X"
//
// These functions DEGRADE GRACEFULLY: they're for debug printing, not
// for sim-path replay. Sim-path determinism does not depend on the
// ToString output (the spec body does not require ToString output
// to be sim-path-safe).
//
// =====================================================================

#include "Math/FVector.h"
#include "Math/FQuat.h"
#include "Math/FMatrix.h"
#include "Math/FRotator.h"

#include "Containers/FString.h"

namespace XCore
{

// ---------------------------------------------------------------------
// Helper: float -> FString via FromFloat (std::to_chars; sim-path-safe).
// ---------------------------------------------------------------------

namespace
{
    [[nodiscard]] FString FloatToString(float V)
    {
        return FString::FromFloat(V);
    }
}

// ---------------------------------------------------------------------
// FVector::ToString  (free function in this TU; the spec calls it a
// member but the member would force FString into the header which the
// dependency-graph forbids).
//
// Free-function form ToString(FVector) is the canonical surface; the
// member would be a one-line forwarder if desired.
// ---------------------------------------------------------------------

[[nodiscard]] FString ToString(const FVector& V)
{
    // "X=... Y=... Z=..."
    FString Out;
    Out.Append("X=");
    Out.Append(FloatToString(V.X).ToUtf8Ptr(), FloatToString(V.X).LenBytes());
    Out.Append(" Y=");
    Out.Append(FloatToString(V.Y).ToUtf8Ptr(), FloatToString(V.Y).LenBytes());
    Out.Append(" Z=");
    Out.Append(FloatToString(V.Z).ToUtf8Ptr(), FloatToString(V.Z).LenBytes());
    return Out;
}

[[nodiscard]] FString ToString(const FQuat& Q)
{
    FString Out;
    Out.Append("X=");
    Out.Append(FloatToString(Q.X).ToUtf8Ptr(), FloatToString(Q.X).LenBytes());
    Out.Append(" Y=");
    Out.Append(FloatToString(Q.Y).ToUtf8Ptr(), FloatToString(Q.Y).LenBytes());
    Out.Append(" Z=");
    Out.Append(FloatToString(Q.Z).ToUtf8Ptr(), FloatToString(Q.Z).LenBytes());
    Out.Append(" W=");
    Out.Append(FloatToString(Q.W).ToUtf8Ptr(), FloatToString(Q.W).LenBytes());
    return Out;
}

[[nodiscard]] FString ToString(const FRotator& R)
{
    FString Out;
    Out.Append("P=");
    Out.Append(FloatToString(R.Pitch).ToUtf8Ptr(), FloatToString(R.Pitch).LenBytes());
    Out.Append(" Y=");
    Out.Append(FloatToString(R.Yaw).ToUtf8Ptr(), FloatToString(R.Yaw).LenBytes());
    Out.Append(" R=");
    Out.Append(FloatToString(R.Roll).ToUtf8Ptr(), FloatToString(R.Roll).LenBytes());
    return Out;
}

[[nodiscard]] FString ToString(const FMatrix& M)
{
    FString Out;
    Out.Append("[");
    for (int Row = 0; Row < 4; ++Row)
    {
        if (Row > 0) Out.Append(" ; ");
        for (int Col = 0; Col < 4; ++Col)
        {
            if (Col > 0) Out.Append(" ");
            FString F = FloatToString(M.M[Row][Col]);
            Out.Append(F.ToUtf8Ptr(), F.LenBytes());
        }
    }
    Out.Append("]");
    return Out;
}

} // namespace XCore
