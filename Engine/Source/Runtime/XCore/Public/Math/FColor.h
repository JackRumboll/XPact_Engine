// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FColor.h -- sRGB-encoded 8-bit BGRA color.
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1.
//
// LAYOUT (per spec body, 4 bytes BGRA order):
//   bytes 0   B  (uint8)
//   bytes 1   G  (uint8)
//   bytes 2   R  (uint8)
//   bytes 3   A  (uint8)
//
// BGRA byte order matches DirectX 11 native swap-chain layout and is
// the canonical on-wire format for textures + framebuffers. The
// component-named accessors (R, G, B, A) read the right field
// regardless of the storage order.
//
// SIM-PATH SAFETY:
//   Pure arithmetic; sim-path-safe.
//
// UE REFERENCES:
//   Engine/Source/Runtime/Core/Public/Math/Color.h -- studied;
//   FColor BGRA layout matches.
//
// =====================================================================

#include "Macros/XCoreTypes.h"  // uint8
#include "Macros/XPactMacros.h"

#include "Math/FLinearColor.h"

#include <cmath>

namespace XCore
{

struct FColor
{
    ::uint8 B;
    ::uint8 G;
    ::uint8 R;
    ::uint8 A;

    XPACT_FORCEINLINE constexpr FColor() noexcept : B(0), G(0), R(0), A(255) {}
    XPACT_FORCEINLINE constexpr FColor(::uint8 InR, ::uint8 InG, ::uint8 InB, ::uint8 InA = 255) noexcept
        : B(InB), G(InG), R(InR), A(InA) {}

    XPACT_FORCEINLINE constexpr bool operator==(const FColor& X) const noexcept
    {
        return B == X.B && G == X.G && R == X.R && A == X.A;
    }

    XPACT_FORCEINLINE constexpr bool operator!=(const FColor& X) const noexcept { return !(*this == X); }
};

static_assert(sizeof(FColor)  == 4, "FColor ABI lock: 4 bytes (BGRA)");
static_assert(alignof(FColor) == 1, "FColor ABI lock: 1-byte aligned");

// =====================================================================
// sRGB <-> Linear conversion (declared in FLinearColor.h).
// =====================================================================
//
// The standard sRGB transfer function (IEC 61966-2-1):
//
//   Linear = sRGB / 12.92                     if sRGB <= 0.04045
//          = ((sRGB + 0.055) / 1.055) ^ 2.4   otherwise
//
//   sRGB = Linear * 12.92                                  if Linear <= 0.0031308
//        = 1.055 * Linear^(1/2.4) - 0.055                  otherwise
//
// Implementation uses the standard piecewise form (rather than a
// pow(2.2) approximation) so the conversion is accurate at near-black
// values where the 2.2 approximation underestimates.
// =====================================================================

[[nodiscard]] XPACT_FORCEINLINE FLinearColor FLinearColor::FromColor(const FColor& Srgb) noexcept
{
    auto SrgbToLinearChannel = [](::uint8 Byte) noexcept -> float
    {
        const float V = static_cast<float>(Byte) * (1.0f / 255.0f);
        return V <= 0.04045f
            ? V * (1.0f / 12.92f)
            : ::std::pow((V + 0.055f) * (1.0f / 1.055f), 2.4f);
    };

    return FLinearColor{
        SrgbToLinearChannel(Srgb.R),
        SrgbToLinearChannel(Srgb.G),
        SrgbToLinearChannel(Srgb.B),
        static_cast<float>(Srgb.A) * (1.0f / 255.0f)
    };
}

[[nodiscard]] XPACT_FORCEINLINE FColor FLinearColor::ToColor(bool bSRGB) const noexcept
{
    auto Clamp01 = [](float V) noexcept -> float
    {
        return V < 0.0f ? 0.0f : (V > 1.0f ? 1.0f : V);
    };

    auto LinearToSrgbChannel = [&](float V) noexcept -> ::uint8
    {
        const float C = Clamp01(V);
        const float S = bSRGB
            ? (C <= 0.0031308f
                ? C * 12.92f
                : 1.055f * ::std::pow(C, 1.0f / 2.4f) - 0.055f)
            : C;
        // Round-to-nearest -> uint8.
        const float Scaled = S * 255.0f + 0.5f;
        return static_cast<::uint8>(Scaled < 0.0f ? 0.0f : (Scaled > 255.0f ? 255.0f : Scaled));
    };

    return FColor{
        LinearToSrgbChannel(R),
        LinearToSrgbChannel(G),
        LinearToSrgbChannel(B),
        static_cast<::uint8>(Clamp01(A) * 255.0f + 0.5f)
    };
}

} // namespace XCore
