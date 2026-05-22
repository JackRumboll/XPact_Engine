// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FLinearColor.h -- linear-space float RGBA color.
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1.
//
// LAYOUT:
//   float R, G, B, A   (16 bytes; 4-byte aligned per component;
//                       struct aligned to 4 to match FVector pattern)
//
// Linear-space colors are the canonical representation for rendering
// and shading; sRGB-space colors (FColor) are the encoded form for
// storage in textures + framebuffers + on-disk image files.
//
// Range: 0..1 by convention; HDR shading allows values >1 (light
// emitters; bloom-source pixels).
//
// SIM-PATH SAFETY:
//   Lerp / arithmetic are pure-arithmetic, sim-path-safe.
//   FromColor / ToColor (sRGB conversion) use the standard sRGB
//   transfer-function approximation (piecewise linear + pow). The
//   pow call routes through XCore::Math at the call site (libm or
//   Sleef). The conversion is documented as "non-sim-path" by
//   convention because color-space conversion is rarely on the sim
//   path (it's a rendering / asset-pipeline concern).
//
// UE REFERENCES:
//   Engine/Source/Runtime/Core/Public/Math/Color.h -- studied;
//   FLinearColor layout matches.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include <cmath>

namespace XCore
{

struct FColor;  // fwd-decl; full type in FColor.h

struct alignas(4) FLinearColor
{
    float R;
    float G;
    float B;
    float A;

    XPACT_FORCEINLINE constexpr FLinearColor() noexcept : R(0.0f), G(0.0f), B(0.0f), A(1.0f) {}
    XPACT_FORCEINLINE constexpr FLinearColor(float InR, float InG, float InB, float InA = 1.0f) noexcept
        : R(InR), G(InG), B(InB), A(InA) {}

    XPACT_FORCEINLINE constexpr FLinearColor operator+(const FLinearColor& X) const noexcept
    {
        return FLinearColor{ R + X.R, G + X.G, B + X.B, A + X.A };
    }

    XPACT_FORCEINLINE constexpr FLinearColor operator*(float Scalar) const noexcept
    {
        return FLinearColor{ R * Scalar, G * Scalar, B * Scalar, A * Scalar };
    }

    XPACT_FORCEINLINE constexpr bool operator==(const FLinearColor& X) const noexcept
    {
        return R == X.R && G == X.G && B == X.B && A == X.A;
    }

    XPACT_FORCEINLINE constexpr bool operator!=(const FLinearColor& X) const noexcept { return !(*this == X); }

    [[nodiscard]] static XPACT_FORCEINLINE constexpr FLinearColor Lerp(
        const FLinearColor& X, const FLinearColor& Y, float Alpha) noexcept
    {
        return FLinearColor{
            X.R + Alpha * (Y.R - X.R),
            X.G + Alpha * (Y.G - X.G),
            X.B + Alpha * (Y.B - X.B),
            X.A + Alpha * (Y.A - X.A)
        };
    }

    // -----------------------------------------------------------------
    // sRGB <-> Linear conversion. Implementation in FColor.h after
    // FColor is defined.
    // -----------------------------------------------------------------

    [[nodiscard]] static FLinearColor FromColor(const FColor& Srgb) noexcept;
    [[nodiscard]] FColor ToColor(bool bSRGB = true) const noexcept;
};

inline constexpr FLinearColor Black { 0.0f, 0.0f, 0.0f, 1.0f };
inline constexpr FLinearColor White { 1.0f, 1.0f, 1.0f, 1.0f };

} // namespace XCore
