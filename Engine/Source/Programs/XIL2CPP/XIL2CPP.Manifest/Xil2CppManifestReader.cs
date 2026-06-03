// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Simgenics.XPact.XIL2CPP.Core;

namespace Simgenics.XPact.XIL2CPP.Manifest;

// ---------------------------------------------------------------------
// XBT manifest input DTOs. The shape mirrors
// /Engine/Source/Programs/XBT/XBT.Manifest/Manifest.fbs +
// ManifestSchema.cs for the fields XIL2CPP needs at parse time, per
// /Documents/XIL2CPP.html Rev 4 Section 9.7 + Contract Section 10.2.
//
// XIL2CPP does NOT link XBT.Manifest at runtime (standalone-tool
// discipline, the same architectural decision XHT.Manifest makes). Both
// projects build against the same Manifest.fbs schema; the C# DTO
// duplication here is the intentional cost so the transpiler ships as a
// self-contained executable.
//
// Phase 6.a needs, per module: the C# source file list (SourceFiles with
// IsCSharp = true, plus the parallel CSharpSources list) and the
// dependency-module list (ModuleDependencies), plus the top-level
// ContractVersion for the schema-agreement gate.
//
// Forward-compatibility (per XIL2CPP.html Section 3.4 + Section 9.7): the
// spec references manifest fields that are NOT in the current
// Manifest.fbs (schema_version, conditional_symbols, roslyn_version,
// dotnet_sdk_version). These are read-when-present, default-when-absent so
// a future XBT that appends them does not break this reader, and the
// current XBT that omits them parses cleanly.
// ---------------------------------------------------------------------

/// <summary>Module tier per master plan Section 2 (Engine / Studio / Project).</summary>
public enum ModuleTier
{
    /// <summary>Universal XPact engine code.</summary>
    Engine = 0,

    /// <summary>Simgenics-internal shared code.</summary>
    Studio = 1,

    /// <summary>Per-product project-specific code.</summary>
    Project = 2,
}

/// <summary>Module subfolder discriminator per Contract Rev 13 Section 10.2.</summary>
public enum ModuleType
{
    /// <summary>Runtime module (default).</summary>
    Runtime = 0,

    /// <summary>Editor-only module.</summary>
    Editor = 1,

    /// <summary>Developer-tools module.</summary>
    Developer = 2,

    /// <summary>Third-party vendored code.</summary>
    ThirdParty = 3,

    /// <summary>Standalone Programs/ tool (XBT, XHT, XIL2CPP, etc.).</summary>
    Programs = 4,
}

/// <summary>Module language surface (bit-flag per Contract Section 10.2).</summary>
[Flags]
public enum Languages
{
    /// <summary>C++ only.</summary>
    Cpp = 1,

    /// <summary>C# only.</summary>
    CSharp = 2,

    /// <summary>Both C++ and C# in the same module.</summary>
    Both = Cpp | CSharp,
}

/// <summary>Build target type per master plan Section 2.</summary>
public enum BuildTargetType
{
    /// <summary>Engineer-station editor target.</summary>
    Editor = 0,

    /// <summary>Game target (Instructor / Trainee at session join).</summary>
    Game = 1,

    /// <summary>Dedicated multi-user server target.</summary>
    Server = 2,
}

/// <summary>Target platform per master plan Section 2.</summary>
public enum Platform
{
    /// <summary>Windows 64-bit (primary dev + trainee desktop).</summary>
    Win64 = 0,

    /// <summary>Linux (Server priority).</summary>
    Linux = 1,

    /// <summary>Android (Quest-class VR HMDs via OpenXR).</summary>
    Android = 2,
}

/// <summary>Build configuration per master plan Section 2.</summary>
public enum BuildConfiguration
{
    /// <summary>Full-debug configuration.</summary>
    Debug = 0,

    /// <summary>Debug-game configuration (editor stack debug; game optimized).</summary>
    DebugGame = 1,

    /// <summary>Development configuration (default dev build).</summary>
    Development = 2,

    /// <summary>Test configuration.</summary>
    Test = 3,

    /// <summary>Shipping configuration (no exceptions per Contract Section 5.11).</summary>
    Shipping = 4,
}

/// <summary>Station role for Game-target builds per master plan Section 2.</summary>
public enum StationRole
{
    /// <summary>Not a Game build (Editor or Server target).</summary>
    None = 0,

    /// <summary>Engineer-station role.</summary>
    Engineer = 1,

    /// <summary>Instructor role.</summary>
    Instructor = 2,

    /// <summary>Trainee role.</summary>
    Trainee = 3,
}

/// <summary>SIMD baseline per Contract Rev 13 Section 4.2 / 9.1 / 10.2.</summary>
public enum SimdLevel
{
    /// <summary>Scalar-only (no SIMD).</summary>
    None = 0,

    /// <summary>Use the target's <c>SimdLevelDefault</c>.</summary>
    Default = 1,

    /// <summary>SSE2 baseline.</summary>
    SSE2 = 2,

    /// <summary>SSE4.2 baseline (SimPath modules clamp here).</summary>
    SSE42 = 3,

    /// <summary>AVX baseline.</summary>
    AVX = 4,

    /// <summary>AVX2 baseline.</summary>
    AVX2 = 5,

    /// <summary>AVX-512 baseline.</summary>
    AVX512 = 6,
}

/// <summary>PCH usage mode per Contract Rev 13 Section 9.1.</summary>
public enum PCHUsageMode
{
    /// <summary>Default PCH usage.</summary>
    Default = 0,

    /// <summary>No PCHs at all.</summary>
    NoPCHs = 1,

    /// <summary>No shared PCHs (SimPath modules forced here).</summary>
    NoSharedPCHs = 2,

    /// <summary>Use shared PCHs.</summary>
    UseSharedPCHs = 3,

    /// <summary>Use explicit or shared PCHs.</summary>
    UseExplicitOrSharedPCHs = 4,
}

/// <summary>
/// One source file the module compiles. Mirrors XBT's
/// <c>Simgenics.XPact.XBT.Manifest.SourceFile</c> shape (the XBT type is
/// not linked at runtime; this is a parallel DTO matching
/// <c>Manifest.fbs</c>'s <c>SourceFile</c> table).
/// </summary>
/// <param name="RelativePath">Path relative to the module's BaseDirectory.</param>
/// <param name="IsCSharp">True for .cs sources; false for .h / .cpp / .cc.</param>
/// <param name="IsHeader">True for headers (.h, .hpp); false otherwise.</param>
/// <param name="IsTestOnly">True if the file participates only in test builds.</param>
public sealed record XbtSourceFile(
    string RelativePath,
    bool IsCSharp,
    bool IsHeader,
    bool IsTestOnly);

/// <summary>
/// Typed dependency entry per Contract Rev 13 Section 9.1 (mirrors XBT's
/// <c>ModuleDep</c> table in <c>Manifest.fbs</c>).
/// </summary>
/// <param name="Name">Module name (matches the producing module's Name).</param>
/// <param name="InterfaceModule">True if header-only / no link.</param>
/// <param name="IsDynamic">
/// True when the dependency is a dynamically-loaded module (the
/// <c>is_dynamic</c> field XBT's WriteManifestAction populates from the
/// source list per <c>Manifest.fbs</c>). Defaults to false when absent so
/// older manifests parse cleanly.
/// </param>
public sealed record XbtModuleDep(
    string Name,
    bool InterfaceModule,
    bool IsDynamic = false);

/// <summary>
/// One reflected module's manifest entry. The set of fields here mirrors
/// XBT's <c>Module</c> table for the surface XIL2CPP needs at parse time
/// per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 9.7.
/// </summary>
/// <param name="Name">Module name (e.g. <c>XScoring</c>).</param>
/// <param name="Tier">Tier (Engine / Studio / Project).</param>
/// <param name="ModuleType">Runtime / Editor / Developer / ThirdParty / Programs.</param>
/// <param name="Languages">Bit-flag of C++ / C# / Both.</param>
/// <param name="BaseDirectory">Module source root, relative to RootLocalPath.</param>
/// <param name="SourceFiles">All source files (XIL2CPP iterates IsCSharp entries).</param>
/// <param name="PublicHeaders">Public-header list (Contract Section 10.2).</param>
/// <param name="PrivateHeaders">Private-header list (Contract Section 10.2).</param>
/// <param name="InternalHeaders">Internal-header list (Contract Section 10.2).</param>
/// <param name="CSharpSources">C# source files (also enumerable via SourceFiles).</param>
/// <param name="IncludePaths">For resolving #include directives.</param>
/// <param name="PublicDefines">For honouring #if directives.</param>
/// <param name="ModuleDependencies">For cross-module type-reference resolution.</param>
/// <param name="GeneratedCPPFilenameBase">Base name for the per-module generated outputs.</param>
/// <param name="SimPath">True if SimPath-determinism module.</param>
/// <param name="EngineVersionCompat">Engine compatibility version.</param>
/// <param name="SimdLevel">SIMD baseline override (or Default).</param>
/// <param name="PCHUsage">PCH usage mode.</param>
/// <param name="ExcludeFromSharedPCH">True if module opts out of shared PCH.</param>
/// <param name="AllowHotReload">True if module allows Live Coding hot reload.</param>
/// <param name="IsTestModule">True for test-only modules.</param>
/// <param name="DeprecationMessage">Optional deprecation warning message.</param>
/// <param name="MinimumToolchainVersion">Optional minimum toolchain version gate.</param>
/// <param name="ConditionalSymbols">
/// Optional <c>[Conditional("X")]</c> / preprocessor-symbol list per
/// <c>/Documents/XIL2CPP.html</c> Section 3.4 (cache-key field
/// <c>Module_conditional_symbols</c>). NOT in the current
/// <c>Manifest.fbs</c>; read-when-present, defaults to an empty list when
/// the emitter omits it.
/// </param>
public sealed record XbtModule(
    string Name,
    ModuleTier Tier,
    ModuleType ModuleType,
    Languages Languages,
    string BaseDirectory,
    IReadOnlyList<XbtSourceFile> SourceFiles,
    IReadOnlyList<string> PublicHeaders,
    IReadOnlyList<string> PrivateHeaders,
    IReadOnlyList<string> InternalHeaders,
    IReadOnlyList<string> CSharpSources,
    IReadOnlyList<string> IncludePaths,
    IReadOnlyList<string> PublicDefines,
    IReadOnlyList<XbtModuleDep> ModuleDependencies,
    string GeneratedCPPFilenameBase,
    bool SimPath,
    string EngineVersionCompat,
    SimdLevel SimdLevel,
    PCHUsageMode PCHUsage,
    bool ExcludeFromSharedPCH,
    bool AllowHotReload,
    bool IsTestModule,
    string? DeprecationMessage,
    string? MinimumToolchainVersion,
    IReadOnlyList<string>? ConditionalSymbols = null);

/// <summary>
/// Per-target ABI envelope mirroring XBT's <c>TargetInfo</c> table per
/// Contract Section 10.2.
/// </summary>
/// <param name="Name">The target's logical name (e.g. <c>MiningTrainingEditor</c>).</param>
/// <param name="Type">Editor / Game / Server.</param>
/// <param name="Platform">Win64 / Linux / Android.</param>
/// <param name="Configuration">Debug / DebugGame / Development / Test / Shipping.</param>
/// <param name="Architecture">CPU architecture string (<c>x86_64</c> or <c>aarch64</c>).</param>
/// <param name="GCRootABI">GC-root ABI identifier.</param>
/// <param name="ExceptionABI">Exception-handling ABI identifier.</param>
/// <param name="ManglingScheme">Symbol-mangling scheme identifier.</param>
/// <param name="FipsMode">True when compiling with FIPS-mode crypto restrictions.</param>
/// <param name="SimPathConservativeRootsAllowed">True when conservative-roots scanning is permitted.</param>
/// <param name="SimdLevelDefault">SIMD baseline default.</param>
/// <param name="StationRole">Station role for Game targets.</param>
public sealed record XbtTargetInfo(
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
/// Top-level XBT manifest record. Mirrors XBT's <c>Manifest</c> table (not
/// linked at runtime; the XIL2CPP-side reader maintains the parallel DTO
/// shape).
/// </summary>
/// <param name="ContractVersion">Auto-derived contract version string (e.g. <c>"13.9+381d8ef7a7770d9b"</c>).</param>
/// <param name="EngineVersion">Engine semver discovered from <c>Engine.xengine</c>.</param>
/// <param name="Target">Per-target fields.</param>
/// <param name="RootLocalPath">Host-local repo root path; forward-slashed.</param>
/// <param name="ExternalDependenciesFile">Optional intermediate file recording the external-deps inventory.</param>
/// <param name="Modules">Modules participating in this build (sorted alphabetically by XBT).</param>
/// <param name="SchemaVersion">
/// Optional FBS manifest schema version per
/// <c>/Documents/XIL2CPP.html</c> Section 9.7 (forward-commit XBT Rev 11).
/// NOT in the current <c>Manifest.fbs</c>; read-when-present. When present
/// and non-zero it is compared against
/// <see cref="Xil2CppManifestReader.SupportedSchemaVersion"/> by the inert
/// schema-version gate; <c>null</c> / 0 (absent) is accepted unconditionally
/// so the current XBT manifests parse.
/// </param>
/// <param name="RoslynVersion">
/// Optional Roslyn compiler version per
/// <c>/Documents/XIL2CPP.html</c> Section 8.1 (cache-key field
/// <c>Roslyn_compiler_version</c>). NOT in the current <c>Manifest.fbs</c>;
/// read-when-present, <c>null</c> when absent.
/// </param>
/// <param name="DotNetSdkVersion">
/// Optional .NET SDK version per <c>/Documents/XIL2CPP.html</c> Section 3.4
/// (cache-key field <c>DotNet_SDK_version</c>). NOT in the current
/// <c>Manifest.fbs</c>; read-when-present, <c>null</c> when absent.
/// </param>
public sealed record XbtManifest(
    string ContractVersion,
    string EngineVersion,
    XbtTargetInfo Target,
    string RootLocalPath,
    string? ExternalDependenciesFile,
    IReadOnlyList<XbtModule> Modules,
    uint? SchemaVersion = null,
    string? RoslynVersion = null,
    string? DotNetSdkVersion = null);

/// <summary>
/// Reader for the XBT manifest XIL2CPP consumes per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 9.7. Mirrors the
/// XHT.Manifest <c>XbtManifestReader</c> (the standalone tools maintain
/// parallel reader stacks against the shared <c>Manifest.fbs</c> schema).
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 6.a implementation.</b> Reads the JSON form only via
/// <see cref="Read"/>. The FBS sidecar reader &#8212;
/// <see cref="TryReadFbsSidecar"/> &#8212; is a deferred gate that returns
/// null until the schema-compile step lands in
/// <c>XIL2CPP.Manifest.csproj</c>'s FlatSharp.targets, matching the
/// XHT.Manifest deferral exactly (XHT.Manifest does not compile the .fbs
/// either; both readers ship JSON-only and stub the FBS path identically).
/// </para>
/// <para>
/// <b>Hardened reader limits per Contract Section 10.2.</b> Max payload
/// 100 MB; max depth 64; max array length 65,536; max string field 16 KB.
/// Violations throw <see cref="ManifestMalformedException"/> which
/// XIL2CPP.Entry maps to exit code 50.
/// </para>
/// <para>
/// <b>Forward-compatibility.</b> Manifest fields the spec references but
/// that are NOT present in the current <c>Manifest.fbs</c> (schema_version,
/// conditional_symbols, roslyn_version, dotnet_sdk_version) are
/// read-when-present and default-when-absent. The schema-version gate
/// (<see cref="ValidateSchemaVersion"/>) is present but inert: a manifest
/// that omits <c>SchemaVersion</c> (or carries 0) passes unconditionally,
/// so the gate becomes load-bearing only once XBT appends the field.
/// </para>
/// </remarks>
public static class Xil2CppManifestReader
{
    /// <summary>Maximum manifest payload size in bytes (100 MB).</summary>
    public const long MaxManifestBytes = 100L * 1024L * 1024L;

    /// <summary>Maximum JSON nesting depth (per Contract Section 10.2).</summary>
    public const int MaxJsonDepth = 64;

    /// <summary>Maximum array length per Contract Section 10.2.</summary>
    public const int MaxArrayLength = 65536;

    /// <summary>Maximum individual string-field length (16 KB per Contract Section 10.2).</summary>
    public const int MaxStringField = 16 * 1024;

    /// <summary>
    /// The FBS manifest schema version XIL2CPP supports per
    /// <c>/Documents/XIL2CPP.html</c> Section 9.7. The gate in
    /// <see cref="ValidateSchemaVersion"/> compares a manifest's
    /// <see cref="XbtManifest.SchemaVersion"/> against this value. Because
    /// the current <c>Manifest.fbs</c> does not yet carry a
    /// <c>schema_version</c> field, a manifest that omits it (or carries 0)
    /// is accepted unconditionally and this value is never consulted; the
    /// gate is inert until XBT forward-commits the field (Rev 11).
    /// </summary>
    public const uint SupportedSchemaVersion = 1;

    private static readonly JsonReaderOptions s_readerOptions = new()
    {
        MaxDepth = MaxJsonDepth,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    private static readonly JsonSerializerOptions s_deserializerOptions = new()
    {
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaxJsonDepth,
        // Property names in XBT's emitted manifest are PascalCase (default
        // System.Text.Json behaviour from the C# record property names).
        // PropertyNameCaseInsensitive lets XIL2CPP accept either form so a
        // hand-edited manifest with camelCase keys (or a future emitter
        // that changes naming policy) still parses cleanly.
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Read the XBT JSON manifest from <paramref name="manifestJsonPath"/>.
    /// </summary>
    /// <param name="manifestJsonPath">Absolute path to <c>Manifest.json</c>.</param>
    /// <returns>The parsed manifest.</returns>
    /// <exception cref="ArgumentException">If <paramref name="manifestJsonPath"/> is null / empty / whitespace.</exception>
    /// <exception cref="ManifestMalformedException">If the JSON fails any hardened-reader limit or schema check (exit code 50).</exception>
    public static XbtManifest Read(string manifestJsonPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJsonPath);

        if (!File.Exists(manifestJsonPath))
        {
            // Manifest missing. The XIL2CPP Section 12 catalog does not
            // allocate a dedicated manifest-not-found code (unlike XHT001),
            // so this surfaces un-anchored at exit 50; the message names the
            // path so the operator can act.
            throw new ManifestMalformedException(
                message: $"XBT manifest not found: {manifestJsonPath}");
        }

        FileInfo fi = new(manifestJsonPath);
        if (fi.Length > MaxManifestBytes)
        {
            throw new ManifestMalformedException(
                message: $"XBT manifest exceeds {MaxManifestBytes} bytes (got {fi.Length}): {manifestJsonPath}");
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(manifestJsonPath);
        }
        catch (IOException ex)
        {
            // I/O during read: the manifest file exists but is unreadable,
            // so the verifier cannot validate it. Verifier-rejection class
            // at exit 50.
            throw new ManifestMalformedException(
                message: $"Failed to read XBT manifest at {manifestJsonPath}: {ex.Message}",
                inner: ex);
        }

        return DeserializeJsonBytes(bytes);
    }

    /// <summary>
    /// Try to read the FBS-binary sidecar (<c>Manifest.fbs.bin</c>).
    /// Phase 6.a returns null unconditionally; a later sub-phase wires up
    /// the FlatSharp greedy-materialised deserializer once the
    /// schema-compile step lands in <c>XIL2CPP.Manifest.csproj</c>'s
    /// FlatSharp.targets. This mirrors XHT.Manifest's identical deferral
    /// (XHT does not compile the .fbs in its manifest project either; its
    /// reader returns null here too).
    /// </summary>
    /// <param name="manifestFbsBinPath">Absolute path to <c>Manifest.fbs.bin</c>.</param>
    /// <returns>Always null in Phase 6.a.</returns>
    public static XbtManifest? TryReadFbsSidecar(string manifestFbsBinPath)
    {
        _ = manifestFbsBinPath; // suppress unused-parameter warning under TreatWarningsAsErrors
        // TODO later sub-phase: wire up FlatSharp greedy-materialised reader.
        // Schema-compile step lands in XIL2CPP.Manifest.csproj's
        // FlatSharp.targets mirroring
        // /Engine/Source/Programs/XBT/XBT.Manifest/FlatSharp.targets.
        // Phase 6.a is JSON-only per /Documents/XIL2CPP.html Rev 4 Section 9.7.
        return null;
    }

    /// <summary>
    /// Deserialize a JSON byte sequence (used for round-trip tests + by
    /// <see cref="Read"/>).
    /// </summary>
    /// <param name="json">UTF-8 encoded JSON bytes.</param>
    /// <returns>The parsed manifest.</returns>
    /// <exception cref="ManifestMalformedException">If the JSON fails any hardened-reader limit or schema check.</exception>
    public static XbtManifest DeserializeJsonBytes(ReadOnlySpan<byte> json)
    {
        if (json.Length == 0)
        {
            // Empty payload is a verifier-limit floor violation.
            throw new ManifestMalformedException(
                message: "XBT manifest payload is empty.");
        }

        if (json.Length > MaxManifestBytes)
        {
            throw new ManifestMalformedException(
                message: $"XBT manifest payload exceeds {MaxManifestBytes} bytes ({json.Length}).");
        }

        // Strip the UTF-8 BOM if a hand-edited manifest carries one. XBT's
        // writer never emits a BOM (System.Text.Json defaults to BOM-less),
        // but a developer editing the manifest in VS Code or Notepad on
        // Windows can accidentally save with a BOM. The hardened JSON
        // reader otherwise chokes on the EF BB BF prefix.
        if (json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF)
        {
            json = json[3..];
        }

        // Pre-validate with the hardened reader so depth + comment +
        // trailing-comma violations are caught before the binder rejects
        // them with a less specific error.
        Utf8JsonReader reader = new(json, s_readerOptions);
        try
        {
            while (reader.Read())
            {
                // Drain. Reader throws on protocol violations.
            }
        }
        catch (JsonException ex)
        {
            // The hardened reader catches depth / trailing-comma / comment
            // violations here before the binder runs.
            throw new ManifestMalformedException(
                message: $"XBT manifest JSON failed hardened-reader validation: {ex.Message}",
                inner: ex);
        }

        XbtManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<XbtManifest>(json, s_deserializerOptions);
        }
        catch (JsonException ex)
        {
            // Binder-stage rejection: a bad-shape payload, an unknown enum
            // member, or a type mismatch lands here.
            throw new ManifestMalformedException(
                message: $"XBT manifest JSON failed to deserialize: {ex.Message}",
                inner: ex);
        }

        if (manifest is null)
        {
            // A literal `null` JSON payload deserializes to a null manifest
            // reference; treat as a verifier-limit violation rather than a
            // silent accept.
            throw new ManifestMalformedException(
                message: "XBT manifest JSON deserialized to null.");
        }

        // Validation ordering (mirrors XHT's R4-MA2 invariant):
        // ContractVersion MUST run before the schema-version gate and the
        // limits check. If a manifest carries both a CV mismatch AND a
        // limits violation, the operator's actionable fix is "rebuild
        // against the matching Contract" -- the limits violation is a
        // downstream consequence of the schema drift, not a user-fixable
        // problem on its own. Surfacing the CV mismatch first gives the
        // operator the actionable diagnostic.
        ValidateContractVersion(manifest);
        ValidateSchemaVersion(manifest);
        ValidateLimits(manifest);
        return manifest;
    }

    /// <summary>
    /// Verify the manifest's <see cref="XbtManifest.ContractVersion"/>
    /// matches XIL2CPP's compile-time
    /// <see cref="Xil2CppVersion.ContractVersion"/> per
    /// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 9.7. Comparison is an
    /// ordinal string-equality check over the full composite
    /// <c>&lt;tag&gt;+&lt;hash&gt;</c> form (e.g.
    /// <c>"13.9+381d8ef7a7770d9b"</c>).
    /// </summary>
    /// <param name="manifest">The deserialised manifest. Must not be null.</param>
    /// <exception cref="ManifestMalformedException">
    /// Thrown with exit code <see cref="ExitCodes.ManifestMalformed"/> (50)
    /// when the manifest's ContractVersion does not match XIL2CPP's. The
    /// message names both the observed and expected values so operators can
    /// decide which side to rebuild.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This is the manifest-schema-mismatch detection XIL2CPP must never
    /// silently bypass (engineering-principles directive: "do it right the
    /// first time, not the easy way"). Without it, a manifest emitted by an
    /// XBT rebuilt against a newer Contract revision would be silently
    /// consumed by an older XIL2CPP, with the failure surfacing only later
    /// as a linker error or runtime corruption.
    /// </para>
    /// <para>
    /// The comparison uses <see cref="StringComparison.Ordinal"/>: the
    /// ContractVersion string is a canonical opaque tag from XBT's emitter
    /// and any case-folding or culture-aware comparison would be a stability
    /// bug. The full composite form is compared (semantic-tag <c>+</c>
    /// structure-hash); a partial-tag-only fallback would defeat the
    /// structure-hash protection that
    /// <see cref="Xil2CppVersion.ContractVersion"/> deliberately encodes.
    /// </para>
    /// <para>
    /// The expected value is read symbolically from
    /// <see cref="Xil2CppVersion.ContractVersion"/> so the gate tracks the
    /// compile-time pin without drift; a later sub-phase re-pins that
    /// constant in lockstep with the XBT + XHT pins.
    /// </para>
    /// </remarks>
    private static void ValidateContractVersion(XbtManifest manifest)
    {
        string expected = Xil2CppVersion.ContractVersion;
        string actual = manifest.ContractVersion;
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new ManifestMalformedException(
                message: string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Manifest ContractVersion '{0}' does not match XIL2CPP's compile-time "
                    + "ContractVersion '{1}'. This XIL2CPP build was compiled against a different "
                    + "version of the contract surface. Rebuild XIL2CPP against the current Contract "
                    + "(or rebuild XBT against the Contract this XIL2CPP was compiled with) so both "
                    + "tools agree on the manifest schema.",
                    actual,
                    expected));
        }
    }

    /// <summary>
    /// Inert schema-version gate per <c>/Documents/XIL2CPP.html</c> Section
    /// 9.7 (forward-commit XBT Rev 11). The current <c>Manifest.fbs</c> does
    /// not carry a <c>schema_version</c> field, so a manifest that omits it
    /// (<see cref="XbtManifest.SchemaVersion"/> is null) or carries 0 is
    /// accepted unconditionally. The gate becomes load-bearing only once
    /// XBT appends the field: a present, non-zero schema version that
    /// differs from <see cref="SupportedSchemaVersion"/> would then be
    /// rejected here (diagnostic <c>XIL2CPP141</c>). Keeping the gate
    /// present-but-inert avoids a silent-accept hole the moment the field
    /// lands without requiring a reader change at that point.
    /// </summary>
    /// <param name="manifest">The deserialised manifest. Must not be null.</param>
    /// <exception cref="ManifestMalformedException">
    /// Thrown (anchored to <c>XIL2CPP141</c>, exit 50) when the manifest
    /// carries a present, non-zero schema version that differs from
    /// <see cref="SupportedSchemaVersion"/>.
    /// </exception>
    private static void ValidateSchemaVersion(XbtManifest manifest)
    {
        uint? schemaVersion = manifest.SchemaVersion;
        if (schemaVersion is null or 0)
        {
            // Absent / zero: the field is not in the current schema. Accept
            // unconditionally (forward-compatible default-when-absent).
            return;
        }

        if (schemaVersion.Value != SupportedSchemaVersion)
        {
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.ManifestUnsupportedSchemaVersion,
                message: string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Manifest schema version {0} is outside XIL2CPP's supported set "
                    + "(supported: {1}); rebuild XBT with the matching schema.",
                    schemaVersion.Value,
                    SupportedSchemaVersion));
        }
    }

    /// <summary>
    /// Convenience overload that accepts a JSON string (used by tests).
    /// </summary>
    /// <param name="json">The JSON text. Must not be null.</param>
    /// <returns>The parsed manifest.</returns>
    public static XbtManifest DeserializeJsonString(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return DeserializeJsonBytes(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// Find a module by name. Returns null when the module is not present.
    /// A module-not-in-manifest lookup failure is the caller's signal to
    /// exit <see cref="ExitCodes.ManifestMalformed"/> (50); the caller that
    /// requires the module present should use
    /// <see cref="RequireModule"/> instead.
    /// </summary>
    /// <param name="manifest">The manifest to search.</param>
    /// <param name="moduleName">The module name to look up (case-sensitive Ordinal match).</param>
    /// <returns>The module, or null when not present.</returns>
    public static XbtModule? FindModule(XbtManifest manifest, string moduleName)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(moduleName);
        foreach (XbtModule mod in manifest.Modules)
        {
            if (string.Equals(mod.Name, moduleName, StringComparison.Ordinal))
            {
                return mod;
            }
        }
        return null;
    }

    /// <summary>
    /// Find a module by name, throwing when it is not present. XIL2CPP's
    /// per-module entry points (parse-module / emit-module) require the
    /// requested module to exist in the manifest; a miss is a malformed-input
    /// condition surfaced at exit <see cref="ExitCodes.ManifestMalformed"/>
    /// (50). The XIL2CPP Section 12 catalog allocates no dedicated
    /// module-not-in-manifest code (unlike XHT004), so this surfaces
    /// un-anchored with a message naming the missing module.
    /// </summary>
    /// <param name="manifest">The manifest to search.</param>
    /// <param name="moduleName">The module name to look up (case-sensitive Ordinal match).</param>
    /// <returns>The module (never null).</returns>
    /// <exception cref="ManifestMalformedException">When the module is not present (exit 50).</exception>
    public static XbtModule RequireModule(XbtManifest manifest, string moduleName)
    {
        XbtModule? mod = FindModule(manifest, moduleName);
        if (mod is null)
        {
            throw new ManifestMalformedException(
                message: $"Module '{moduleName}' is not present in the XBT manifest.");
        }
        return mod;
    }

    /// <summary>
    /// The C# source files a module compiles, per
    /// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 9.7. Phase 6.a's Roslyn
    /// front-end consumes this list as its compilation inputs. Returns the
    /// union of the module's <see cref="XbtModule.CSharpSources"/> list and
    /// the <see cref="XbtModule.SourceFiles"/> entries whose
    /// <see cref="XbtSourceFile.IsCSharp"/> flag is set, de-duplicated by
    /// ordinal path while preserving first-seen order (CSharpSources first,
    /// then any IsCSharp SourceFiles not already listed). XBT may populate
    /// either or both lists; taking the union is robust to both emit shapes.
    /// </summary>
    /// <param name="module">The module to enumerate. Must not be null.</param>
    /// <returns>The module's C# source paths (relative to its BaseDirectory).</returns>
    public static IReadOnlyList<string> GetCSharpSourceFiles(XbtModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        List<string> result = new();
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (string path in module.CSharpSources)
        {
            if (seen.Add(path))
            {
                result.Add(path);
            }
        }

        foreach (XbtSourceFile sf in module.SourceFiles)
        {
            if (sf.IsCSharp && seen.Add(sf.RelativePath))
            {
                result.Add(sf.RelativePath);
            }
        }

        return result;
    }

    /// <summary>
    /// The dependency-module names a module declares, per
    /// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 9.7. Phase 6.a's
    /// cross-module type-resolution prerequisite walk consumes this list.
    /// Returns the <see cref="XbtModuleDep.Name"/> of every entry in the
    /// module's <see cref="XbtModule.ModuleDependencies"/> list, in
    /// declaration order.
    /// </summary>
    /// <param name="module">The module to enumerate. Must not be null.</param>
    /// <returns>The dependency-module names, in declaration order.</returns>
    public static IReadOnlyList<string> GetDependencyModuleNames(XbtModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        List<string> result = new(module.ModuleDependencies.Count);
        foreach (XbtModuleDep dep in module.ModuleDependencies)
        {
            result.Add(dep.Name);
        }

        return result;
    }

    private static void ValidateLimits(XbtManifest m)
    {
        CheckString(m.ContractVersion, nameof(m.ContractVersion));
        CheckString(m.EngineVersion, nameof(m.EngineVersion));
        CheckString(m.RootLocalPath, nameof(m.RootLocalPath));
        CheckOptionalString(m.ExternalDependenciesFile, nameof(m.ExternalDependenciesFile));
        CheckOptionalString(m.RoslynVersion, nameof(m.RoslynVersion));
        CheckOptionalString(m.DotNetSdkVersion, nameof(m.DotNetSdkVersion));

        if (m.Target is null)
        {
            throw new ManifestMalformedException(
                message: $"{nameof(m.Target)} is null (required).");
        }
        CheckString(m.Target.Name, $"{nameof(m.Target)}.{nameof(m.Target.Name)}");
        CheckString(m.Target.Architecture, $"{nameof(m.Target)}.{nameof(m.Target.Architecture)}");
        CheckString(m.Target.GCRootABI, $"{nameof(m.Target)}.{nameof(m.Target.GCRootABI)}");
        CheckString(m.Target.ExceptionABI, $"{nameof(m.Target)}.{nameof(m.Target.ExceptionABI)}");
        CheckString(m.Target.ManglingScheme, $"{nameof(m.Target)}.{nameof(m.Target.ManglingScheme)}");

        if (m.Modules is null)
        {
            throw new ManifestMalformedException(
                message: $"{nameof(m.Modules)} is null (required).");
        }
        if (m.Modules.Count > MaxArrayLength)
        {
            throw new ManifestMalformedException(
                message: $"Manifest.Modules array exceeds {MaxArrayLength} entries ({m.Modules.Count}).");
        }

        foreach (XbtModule mod in m.Modules)
        {
            CheckString(mod.Name, $"Module[{mod.Name}].Name");
            CheckString(mod.BaseDirectory, $"Module[{mod.Name}].BaseDirectory");
            CheckString(mod.GeneratedCPPFilenameBase, $"Module[{mod.Name}].GeneratedCPPFilenameBase");
            CheckString(mod.EngineVersionCompat, $"Module[{mod.Name}].EngineVersionCompat");
            CheckOptionalString(mod.DeprecationMessage, $"Module[{mod.Name}].DeprecationMessage");
            CheckOptionalString(mod.MinimumToolchainVersion, $"Module[{mod.Name}].MinimumToolchainVersion");

            CheckCount(mod.SourceFiles, $"Module[{mod.Name}].SourceFiles");
            CheckStringList(mod.PublicHeaders, $"Module[{mod.Name}].PublicHeaders");
            CheckStringList(mod.PrivateHeaders, $"Module[{mod.Name}].PrivateHeaders");
            CheckStringList(mod.InternalHeaders, $"Module[{mod.Name}].InternalHeaders");
            CheckStringList(mod.CSharpSources, $"Module[{mod.Name}].CSharpSources");
            CheckStringList(mod.IncludePaths, $"Module[{mod.Name}].IncludePaths");
            CheckStringList(mod.PublicDefines, $"Module[{mod.Name}].PublicDefines");
            CheckCount(mod.ModuleDependencies, $"Module[{mod.Name}].ModuleDependencies");

            // ConditionalSymbols is a forward-compat optional list (absent
            // in the current schema). Validate it only when present.
            if (mod.ConditionalSymbols is not null)
            {
                CheckStringList(mod.ConditionalSymbols, $"Module[{mod.Name}].ConditionalSymbols");
            }

            foreach (XbtSourceFile sf in mod.SourceFiles)
            {
                CheckString(sf.RelativePath, $"Module[{mod.Name}].SourceFile.RelativePath");
            }

            foreach (XbtModuleDep dep in mod.ModuleDependencies)
            {
                CheckString(dep.Name, $"Module[{mod.Name}].Dep.Name");
            }
        }
    }

    private static void CheckString(string value, string context)
    {
        if (value is null)
        {
            throw new ManifestMalformedException(
                message: $"{context} is null (required).");
        }
        if (value.Length > MaxStringField)
        {
            throw new ManifestMalformedException(
                message: $"{context} exceeds {MaxStringField} chars ({value.Length}).");
        }
    }

    private static void CheckOptionalString(string? value, string context)
    {
        if (value is null)
        {
            return;
        }
        if (value.Length > MaxStringField)
        {
            throw new ManifestMalformedException(
                message: $"{context} exceeds {MaxStringField} chars ({value.Length}).");
        }
    }

    private static void CheckStringList(IReadOnlyList<string> list, string context)
    {
        if (list is null)
        {
            throw new ManifestMalformedException(
                message: $"{context} is null (required).");
        }
        if (list.Count > MaxArrayLength)
        {
            throw new ManifestMalformedException(
                message: $"{context} exceeds {MaxArrayLength} entries ({list.Count}).");
        }
        foreach (string s in list)
        {
            CheckString(s, $"{context}[]");
        }
    }

    private static void CheckCount<T>(IReadOnlyList<T> list, string context)
    {
        if (list is null)
        {
            throw new ManifestMalformedException(
                message: $"{context} is null (required).");
        }
        if (list.Count > MaxArrayLength)
        {
            throw new ManifestMalformedException(
                message: $"{context} exceeds {MaxArrayLength} entries ({list.Count}).");
        }
    }
}
