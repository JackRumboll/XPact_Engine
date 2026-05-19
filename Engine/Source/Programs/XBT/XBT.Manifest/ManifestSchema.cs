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
/// Top-level manifest record. Written to disk in two forms per
/// Toolchain Contract Section 10.2 -- JSON (here) and FlatBuffers
/// sidecar (Subagent B's <c>Manifest.fbs</c> + generated reader).
/// </summary>
public sealed record Manifest(
    string ContractVersion,
    string EngineVersion,
    string TargetName,
    BuildTargetType TargetType,
    BuildConfiguration Configuration,
    Platform Platform,
    string RootLocalPath,
    string? ExternalDependenciesFile,
    bool FipsMode,
    bool SimPathConservativeRootsAllowed,
    StationRole StationRole,
    SimdLevel SimdLevelDefault,
    IReadOnlyList<Module> Modules);
