// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FPlatformProperties.h -- compile-time platform-property constants.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1 (Platform HAL). Per-OS overrides land
// in Phase 1b.
//
// Pattern reference: UE Core `GenericPlatformProperties.h:16-160`
// defines `FGenericPlatformProperties` with all-constexpr bool/
// const-char* members (HasEditorOnlyData, IsServerOnly, IsLittleEndian,
// PlatformName, etc.). XPact's surface mirrors this shape but reduces
// the field count to the constants the engine actually consults:
//   - HasTouchInput: drives the input subsystem's compile-time path
//     selection (mouse/keyboard on Win64/Linux; touch on Android)
//   - IsServer: drives Render/Audio compile-out and the 30 Hz tick
//     selection (FPlatformTime::kTickRate Phase 1b TargetRules wiring)
//   - IsCaseSensitiveFilesystem: drives FString path-comparison
//     semantics (Section 11.1; Win64 paths are case-insensitive at the
//     filesystem level but FString operations are byte-sensitive
//     unless explicitly told otherwise)
//   - RequiresCookedData: drives asset-loading path (editor vs runtime)
//   - PlatformName: drives the FName-encoded platform string for logs
//     and crash reports
//
// UE has many more fields (HasSecurePackageFormat, IniPlatformName,
// SupportsAudioStreaming, etc.). XPact defers these to higher layers
// where they belong; the Platform HAL surface stays focused on the
// constants the foundation systems consume.
//
// Phase 1a: the abstract `FPlatformProperties` has the conservative
// defaults (everything false except where the engineering-principle
// default differs from "false"). Phase 1b supplies per-OS overrides via
// partial specialization is not applicable (non-templated struct);
// instead the per-OS header chain inclues this header THEN overrides
// the constants via an `#ifdef` slate inside Phase 1b's platform
// selector header (`Engine/Source/Runtime/XCore/Public/HAL/Platform.h`
// from the spec's macro suite). For Phase 1b the per-OS overrides are
// resolved by `#if XPACT_PLATFORM_*` directly in this header (option
// (a) from the Phase 1a TODO; the engineering-principle option (c) is
// deferred to a future revision because it would change the user-
// visible type name).
//
// Phase 1b resolution mechanism: the XPACT_PLATFORM_* macros (defined
// in Macros/XPactMacros.h lines 421-439) select exactly one platform's
// values. Per-OS .cpp marker files (Private/HAL/{Windows,Unix,Android}/
// {Win64,Linux,Android}PlatformProperties.cpp) static_assert the active
// resolution so a build whose XPACT_PLATFORM_* macros are inconsistent
// fails cleanly at the linker rather than producing wrong values.
//
// =====================================================================

#include "Macros/XPactMacros.h"  // XPACT_PLATFORM_WIN64 / LINUX / ANDROID

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// FPlatformProperties -- compile-time platform-property constants.
//
// Pure-constexpr; no instantiation; no virtuals. All members are
// `static constexpr`. Compile-time pruning of "if (IsServer) ..."
// branches drops dead code on the build target where it doesn't apply.
//
// ABI: zero -- the struct has no member storage; the constants are
// inlined at every call site.
//
// Per-OS resolution (Phase 1b):
//   * HasTouchInput               -- Android-only true; Win64/Linux false.
//   * IsServer                    -- Linux true (server target); Win64/Android false.
//                                    NOTE: this is currently keyed on
//                                    OS, not on TargetRules.TargetType.
//                                    A Win64 dedicated-server target
//                                    would still report IsServer=false
//                                    here; that drift is documented as
//                                    TODO(Phase 1b XBT integration).
//   * IsCaseSensitiveFilesystem   -- Linux + Android true; Win64 false.
//   * RequiresCookedData          -- Android true (no editor on Quest 3);
//                                    Win64/Linux false (editor + dev paths).
//                                    NOTE: same TargetType caveat as IsServer.
//   * PlatformName()              -- "Win64" / "Linux" / "Android".
// ---------------------------------------------------------------------

struct FPlatformProperties
{
    // -----------------------------------------------------------------
    // HasTouchInput -- platform has touch-screen input as a primary
    // pointing device. Phase 1b: Android-only.
    // -----------------------------------------------------------------
#if XPACT_PLATFORM_ANDROID
    static constexpr bool HasTouchInput = true;
#else
    static constexpr bool HasTouchInput = false;
#endif

    // -----------------------------------------------------------------
    // IsServer -- platform is a dedicated server build. Phase 1b:
    // Linux-only (XPact's server target ships on Linux per Section 2).
    //
    // TODO(Phase 1b XBT integration): if XBT adds an
    // XPACT_TARGET_SERVER preprocessor define that is independent of
    // the OS (e.g., a Win64 dedicated-server build), key on that
    // macro here. Until then OS == server is the locked default per
    // Section 2 platforms row.
    // -----------------------------------------------------------------
#if XPACT_PLATFORM_LINUX
    static constexpr bool IsServer = true;
#else
    static constexpr bool IsServer = false;
#endif

    // -----------------------------------------------------------------
    // IsCaseSensitiveFilesystem -- platform's filesystem is
    // case-sensitive. Phase 1b: Linux + Android true (POSIX
    // case-sensitive); Win64 false (NTFS case-insensitive default).
    // -----------------------------------------------------------------
#if XPACT_PLATFORM_LINUX || XPACT_PLATFORM_ANDROID
    static constexpr bool IsCaseSensitiveFilesystem = true;
#else
    static constexpr bool IsCaseSensitiveFilesystem = false;
#endif

    // -----------------------------------------------------------------
    // RequiresCookedData -- platform requires assets to be pre-cooked.
    // Phase 1b: Android-only true (no editor on Quest 3 device);
    // Win64 + Linux runtime builds keep raw-asset loading available.
    //
    // TODO(Phase 1b XBT integration): the cooked-vs-raw split is
    // properly TargetType-driven (Editor vs Game), not OS-driven. The
    // current resolution is a placeholder until XBT exposes the
    // target type as a preprocessor define.
    // -----------------------------------------------------------------
#if XPACT_PLATFORM_ANDROID
    static constexpr bool RequiresCookedData = true;
#else
    static constexpr bool RequiresCookedData = false;
#endif

    // -----------------------------------------------------------------
    // PlatformName -- platform identifier string. Phase 1b: returns
    // "Win64" / "Linux" / "Android" per the active platform selector.
    //
    // The returned const char* is a string literal (lives in .rdata;
    // no allocation; safe to return by const-pointer indefinitely).
    // Constexpr-noexcept: callers can use it in static_assert messages
    // and in noexcept paths.
    // -----------------------------------------------------------------
    static constexpr const char* PlatformName() noexcept
    {
#if XPACT_PLATFORM_WIN64
        return "Win64";
#elif XPACT_PLATFORM_LINUX
        return "Linux";
#elif XPACT_PLATFORM_ANDROID
        return "Android";
#else
        return "Unknown";
#endif
    }
};

} // namespace XCore::HAL

// =====================================================================
// Phase 1b acceptance: exactly one of XPACT_PLATFORM_WIN64,
// XPACT_PLATFORM_LINUX, XPACT_PLATFORM_ANDROID must be set to 1; the
// rest must be 0. The per-OS marker .cpp files in
// Private/HAL/{Windows,Unix,Android}/*PlatformProperties.cpp
// static_assert this invariant.
// =====================================================================
static_assert((XPACT_PLATFORM_WIN64
             + XPACT_PLATFORM_LINUX
             + XPACT_PLATFORM_ANDROID) == 1,
              "XPACT_PLATFORM_* macros: exactly one must be set; check "
              "Macros/XPactMacros.h lines 421-439 for the resolution.");
