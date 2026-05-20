// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;

namespace Simgenics.XPact.XBT.Manifest;

// JSON DTOs for the manifest XBT emits per Toolchain Contract Rev 13
// Section 10.2. The shape mirrors the FBS schema in Manifest.fbs
// (owned by Subagent B); field naming uses PascalCase so the JSON
// produced by System.Text.Json reads cleanly in editors and diffs
// well in version control. The FBS sidecar uses the FlatBuffers
// generated reader; both readers are derived from the same C# DTOs +
// schema so the two forms stay in lockstep.

/// <summary>
/// One source file the module compiles. The relative path is module
/// <c>BaseDirectory</c>-relative so the manifest stays portable across
/// machines (Toolchain Contract Section 10.2 -- repo root is recorded
/// once in <see cref="Manifest.RootLocalPath"/>).
/// </summary>
/// <param name="RelativePath">Path relative to the module's base directory.</param>
/// <param name="IsCSharp">True for .cs sources; false for .h/.cpp/.cc.</param>
/// <param name="IsHeader">True for headers (.h, .hpp); false otherwise.</param>
/// <param name="IsTestOnly">
/// True if the file participates only in test builds. The action graph
/// filters test-only sources out of Game / Server target builds at
/// link time.
/// </param>
public sealed record SourceFile(
    string RelativePath,
    bool IsCSharp,
    bool IsHeader,
    bool IsTestOnly);

/// <summary>
/// One reflected module's manifest entry per Toolchain Contract Rev 13
/// Section 10.2. The set of fields here is a strict subset of the FBS
/// <c>table Module</c>; Subagent B's tests validate field parity at
/// build time.
/// </summary>
public sealed record Module(
    string Name,
    ModuleTier Tier,
    ModuleType ModuleType,
    Languages Languages,
    string BaseDirectory,
    IReadOnlyList<SourceFile> SourceFiles,
    IReadOnlyList<string> PublicHeaders,
    IReadOnlyList<string> PrivateHeaders,
    IReadOnlyList<string> InternalHeaders,
    IReadOnlyList<string> CSharpSources,
    IReadOnlyList<string> IncludePaths,
    IReadOnlyList<string> PublicDefines,
    IReadOnlyList<ModuleDep> ModuleDependencies,
    string GeneratedCPPFilenameBase,
    bool SimPath,
    string EngineVersionCompat,
    SimdLevel SimdLevel,
    PCHUsageMode PCHUsage,
    bool ExcludeFromSharedPCH,
    bool AllowHotReload,
    bool IsTestModule,
    string? DeprecationMessage,
    string? MinimumToolchainVersion);

/// <summary>
/// Per-target information for one manifest emission. Mirrors the FBS
/// <c>table TargetInfo</c> in <c>Manifest.fbs</c> per Toolchain Contract
/// Rev 13 Section 10.2. Every field on this record differs across
/// platform / configuration / architecture builds of the same source
/// tree; fields that are invariant across targets (engine version,
/// repo root, the modules inventory) stay on the top-level
/// <see cref="Manifest"/> record.
/// </summary>
/// <remarks>
/// <para>
/// <b>ABI envelope fields.</b> <see cref="Architecture"/>,
/// <see cref="GCRootABI"/>, <see cref="ExceptionABI"/>, and
/// <see cref="ManglingScheme"/> identify the binary ABI a manifest was
/// emitted under. XHT and XIL2CPP consult these at parse time so a
/// manifest produced under one ABI cannot be silently consumed under
/// another. Per Toolchain Contract Rev 13 Section 10.2 + audit fix
/// C1/C10: every emitted manifest carries the architecture from
/// <see cref="Configuration.TargetRules.Architecture"/> rather than the
/// previous hardcoded "x86_64" default in <see cref="ManifestFbs"/>.
/// </para>
/// <para>
/// The three ABI fields default to the Phase 1 baseline values:
/// <c>GCRootABI = "Span-based v1"</c>,
/// <c>ExceptionABI = "Tier1-Shim/Tier2-Direct"</c>,
/// <c>ManglingScheme = "Itanium-LengthPrefixed-v1"</c>.
/// </para>
/// </remarks>
/// <param name="Name">The target's logical name (e.g. <c>MiningTrainingEditor</c>).</param>
/// <param name="Type">Editor / Game / Server.</param>
/// <param name="Platform">Win64 / Linux / Android.</param>
/// <param name="Configuration">Debug / DebugGame / Development / Test / Shipping.</param>
/// <param name="Architecture">CPU architecture string (<c>x86_64</c> or <c>aarch64</c>).</param>
/// <param name="GCRootABI">GC-root ABI identifier; Phase 1 baseline is <c>"Span-based v1"</c>.</param>
/// <param name="ExceptionABI">Exception-handling ABI identifier; Phase 1 baseline is <c>"Tier1-Shim/Tier2-Direct"</c>.</param>
/// <param name="ManglingScheme">Symbol-mangling scheme identifier; Phase 1 baseline is <c>"Itanium-LengthPrefixed-v1"</c>.</param>
/// <param name="FipsMode">True when the target compiles with FIPS-mode crypto restrictions.</param>
/// <param name="SimPathConservativeRootsAllowed">True when SimPath conservative-roots scanning is permitted on this target.</param>
/// <param name="SimdLevelDefault">SIMD baseline targets without an explicit module-level override compile against.</param>
/// <param name="StationRole">Operator role for Game-target stations (<c>None</c> on Editor / Server).</param>
public sealed record TargetInfo(
    string Name,
    BuildTargetType Type,
    Platform Platform,
    BuildConfiguration Configuration,
    string Architecture,
    string GCRootABI,
    string ExceptionABI,
    string ManglingScheme,
    bool FipsMode,
    bool SimPathConservativeRootsAllowed,
    SimdLevel SimdLevelDefault,
    StationRole StationRole);

/// <summary>
/// Top-level manifest record. Written to disk in two forms per
/// Toolchain Contract Section 10.2 -- JSON (here) and FlatBuffers
/// sidecar (Subagent B's <c>Manifest.fbs</c> + generated reader).
/// </summary>
/// <remarks>
/// <para>
/// Per-target fields (target name / type / platform / configuration /
/// architecture / ABI envelope / SimPath + station policy) are grouped
/// under the nested <see cref="TargetInfo"/> record; this mirrors the
/// FBS schema's <c>table TargetInfo</c> grouping (Toolchain Contract
/// Rev 13 Section 10.2) and isolates the per-emission fields from the
/// invariants (engine version, repo root, modules list).
/// </para>
/// </remarks>
/// <param name="ContractVersion">Auto-derived contract version string (see <see cref="Manifest.ContractVersion"/>).</param>
/// <param name="EngineVersion">Engine semver discovered from <c>Engine.xengine</c>; invariant across targets.</param>
/// <param name="Target">Per-target fields grouped per the FBS schema's <c>table TargetInfo</c>.</param>
/// <param name="RootLocalPath">Host-local repo root path; forward-slashed for portability.</param>
/// <param name="ExternalDependenciesFile">Optional intermediate file recording the external-deps inventory; null when none.</param>
/// <param name="Modules">Modules participating in this build, sorted alphabetically for determinism.</param>
public sealed record Manifest(
    string ContractVersion,
    string EngineVersion,
    TargetInfo Target,
    string RootLocalPath,
    string? ExternalDependenciesFile,
    IReadOnlyList<Module> Modules);
