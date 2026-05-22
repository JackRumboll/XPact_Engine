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
// from the spec's macro suite). For Phase 1a we ship the defaults; a
// `// TODO(Phase 1b)` per field documents the per-OS override target.
//
// =====================================================================

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
// ---------------------------------------------------------------------

struct FPlatformProperties
{
    // -----------------------------------------------------------------
    // HasTouchInput -- platform has touch-screen input as a primary
    // pointing device.
    //
    // Defaults to false.
    //
    // TODO(Phase 1b): override to `true` in the Android platform
    // selector; Win64 and Linux stay false. Drives the input
    // subsystem's choice of mouse/keyboard vs touch path.
    // -----------------------------------------------------------------
    static constexpr bool HasTouchInput = false;

    // -----------------------------------------------------------------
    // IsServer -- platform is a dedicated server build.
    //
    // Defaults to false (client builds).
    //
    // TODO(Phase 1b): override to `true` in the Linux platform
    // selector when TargetRules.TargetType == Server. Note that the
    // mapping is build-config-driven, not strictly OS-driven (a Win64
    // server build is theoretically possible; the Phase 1b override
    // will key on TargetType rather than EPlatform).
    //
    // Drives FPlatformTime::kTickRate (30 Hz vs 60 Hz; Phase 1b
    // TargetRules wiring) and the renderer/audio compile-out.
    // -----------------------------------------------------------------
    static constexpr bool IsServer = false;

    // -----------------------------------------------------------------
    // IsCaseSensitiveFilesystem -- platform's filesystem is
    // case-sensitive.
    //
    // Defaults to false (Win64-default).
    //
    // TODO(Phase 1b): override to `true` in the Linux and Android
    // platform selectors. Drives FString path-comparison semantics in
    // the asset-path lookup (Section 11.1). Note that even on a
    // case-insensitive filesystem, FString path operations are
    // byte-sensitive unless explicitly told otherwise via the
    // PathCompare helper.
    //
    // The split matters for the editor's "rename asset" path: an
    // attempt to rename `MyAsset.uasset` to `myasset.uasset` succeeds
    // on Linux (different filesystem entries) but fails on Win64
    // (same entry, different case). The engine's rename UI consults
    // this constant to surface the platform-specific behaviour.
    // -----------------------------------------------------------------
    static constexpr bool IsCaseSensitiveFilesystem = false;

    // -----------------------------------------------------------------
    // RequiresCookedData -- platform requires assets to be pre-cooked
    // (no raw asset loading at runtime).
    //
    // Defaults to false (editor builds load raw assets).
    //
    // TODO(Phase 1b): override to `true` for runtime-only client/
    // server builds (driven by TargetRules.BuildType == Game rather
    // than EPlatform). The editor build keeps `false` regardless of
    // platform.
    //
    // Drives the asset-loading path (cooked binary vs editor-source
    // parse).
    // -----------------------------------------------------------------
    static constexpr bool RequiresCookedData = false;

    // -----------------------------------------------------------------
    // PlatformName -- platform identifier string ("Win64" / "Linux" /
    // "Android").
    //
    // Defaults to "Unknown".
    //
    // TODO(Phase 1b): override per OS in the platform selector. The
    // returned const char* is a fixed string literal (lives in .rdata;
    // no allocation; safe to return by const-pointer indefinitely).
    //
    // Pattern reference: UE Core `GenericPlatformProperties.h:118`
    // declares `PlatformName` as a per-platform required override
    // (no default; missing implementation = link error). XPact's Phase
    // 1a default is "Unknown" so the abstract surface compiles
    // standalone; Phase 1b's per-OS slate replaces it.
    //
    // Constexpr-noexcept: callers can use the string at compile time
    // (e.g., in static_assert messages) and in noexcept paths.
    // -----------------------------------------------------------------
    static constexpr const char* PlatformName() noexcept
    {
        return "Unknown";  // TODO(Phase 1b): "Win64" / "Linux" / "Android"
    }
};

} // namespace XCore::HAL

// =====================================================================
// TODO(Phase 1b):
//   - Override HasTouchInput=true for Android; stay false on Win64/Linux.
//   - Override IsServer=true when TargetRules.TargetType == Server.
//   - Override IsCaseSensitiveFilesystem=true on Linux/Android.
//   - Override RequiresCookedData=true for runtime-only builds.
//   - Override PlatformName() to return "Win64" / "Linux" / "Android"
//     per the active platform selector.
//   - Decide whether the Phase 1b override mechanism uses (a) #include
//     of a per-OS .h file that #defines macros consumed here, or (b)
//     partial specialization via a template parameter, or (c) a
//     separate FPlatformProperties_Win64/Linux/Android type that the
//     platform-selector aliases to `FPlatformProperties`. The
//     engineering-principle preferred path is (c) because it keeps
//     the const-correctness clean and avoids #define-based field
//     overrides which fight the language's namespacing.
// =====================================================================
