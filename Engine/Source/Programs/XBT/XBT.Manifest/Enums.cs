// Copyright Simgenics. All Rights Reserved.

using System;

namespace Simgenics.XPact.XBT.Manifest;

// All enums in this file mirror the FBS schema in
// /Engine/Source/Programs/XBT/Manifest.fbs (delivered by Subagent B)
// per Toolchain Contract Rev 13 Section 10.2. The FBS schema is
// append-only and the C# ordinals MUST match the FBS ordinals
// byte-for-byte -- Subagent B's tests verify this invariant.

/// <summary>
/// Three reusability tiers per master plan Section 2 Folder structure
/// row (Rev 6). Engine = universal XPact engine; Studio = Simgenics
/// internal shared code; Project = per-product specifics.
/// </summary>
public enum ModuleTier
{
    Engine = 0,
    Studio = 1,
    Project = 2,
}

/// <summary>
/// Unreal-convention subfolder discriminator inside a tier's
/// <c>Source/</c> directory. Per Toolchain Contract Rev 13 Section 10.2
/// FBS schema, <c>Programs</c> is appended at ordinal 4 after
/// <c>ThirdParty</c> (ordinal 3); this is the append-only Rev 13
/// addition for Layer 0 standalone tools (XBT itself, XHT, XIL2CPP,
/// XAutomationTool, etc.).
/// </summary>
public enum ModuleType
{
    Runtime = 0,
    Editor = 1,
    Developer = 2,
    ThirdParty = 3,
    Programs = 4,
}

/// <summary>
/// Module language surface, bit-flag aligned with FBS
/// <c>Languages</c> (<c>bit_flags</c> attribute). Modules may emit
/// C++ only, C# only, or both within one DLL per master plan Section 2
/// Module composition row.
/// </summary>
[Flags]
public enum Languages
{
    Cpp = 1,
    CSharp = 2,
    Both = Cpp | CSharp,
}

/// <summary>
/// SIMD baseline per Toolchain Contract Rev 13 Section 4.2 / 9.1 /
/// 10.2. <c>None</c> = scalar-only; <c>Default</c> = use the target's
/// <c>SimdLevelDefault</c>; <c>SSE2</c>..<c>AVX512</c> = explicit x86_64
/// baselines. SimPath modules clamp to <c>&lt;= SSE42</c> at
/// flag-derivation time per Section 4.2.
/// </summary>
public enum SimdLevel
{
    None = 0,
    Default = 1,
    SSE2 = 2,
    SSE42 = 3,
    AVX = 4,
    AVX2 = 5,
    AVX512 = 6,
}

/// <summary>
/// Five build configurations per master plan Section 2 Build
/// configurations row. Exceptions on in Debug/DebugGame/Development/Test;
/// off in Shipping (Toolchain Contract Section 5.11).
/// </summary>
public enum BuildConfiguration
{
    Debug = 0,
    DebugGame = 1,
    Development = 2,
    Test = 3,
    Shipping = 4,
}

/// <summary>
/// Three build targets per master plan Section 2 Build targets row.
/// Editor = Engineer station; Game = Instructor + Trainee (role at
/// session join); Server = dedicated multi-user host.
/// </summary>
public enum BuildTargetType
{
    Editor = 0,
    Game = 1,
    Server = 2,
}

/// <summary>
/// Three platforms per master plan Section 2 Platforms row.
/// Win64 = primary dev + trainee desktop; Linux = Server priority;
/// Android = Quest 3-class VR HMDs via OpenXR.
/// </summary>
public enum Platform
{
    Win64 = 0,
    Linux = 1,
    Android = 2,
}

/// <summary>
/// Station role per master plan Section 2 Trainee deployment row +
/// XBT.html Section 4.10 (Rev 3 propagation as compile-define).
/// <c>None</c> = not a Game build (Editor or Server target);
/// other values populate <c>-DXPACT_STATION_ROLE=...</c> on Game-target
/// compile actions.
/// </summary>
public enum StationRole
{
    None = 0,
    Engineer = 1,
    Instructor = 2,
    Trainee = 3,
}

/// <summary>
/// PCH usage mode per Toolchain Contract Rev 13 Section 9.1.
/// SimPath modules are forced to <c>NoSharedPCHs</c> at validation time
/// (Section 1.5 PCH lock) -- mixing SimPath with SharedPCH is a build
/// failure.
/// </summary>
public enum PCHUsageMode
{
    Default = 0,
    NoPCHs = 1,
    NoSharedPCHs = 2,
    UseSharedPCHs = 3,
    UseExplicitOrSharedPCHs = 4,
}

/// <summary>
/// Floating-point semantics per Toolchain Contract Rev 13 Section 4.
/// <c>Precise</c> is forced on SimPath modules (with banned-flag check
/// rejecting <c>Imprecise</c>).
/// </summary>
public enum FPSemantics
{
    Default = 0,
    Precise = 1,
    Imprecise = 2,
}

/// <summary>
/// Code-optimization mode per UE-mirror enum. Used by the toolchain
/// abstraction in <c>XBT.Toolchain</c> when emitting per-TU flag sets.
/// </summary>
public enum OptimizeCodeMode
{
    Default = 0,
    Always = 1,
    Never = 2,
    InNonDebugBuilds = 3,
}
