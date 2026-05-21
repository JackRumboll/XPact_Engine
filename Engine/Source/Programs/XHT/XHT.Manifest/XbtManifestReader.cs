// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Simgenics.XPact.XHT.Core;

namespace Simgenics.XPact.XHT.Manifest;

// ---------------------------------------------------------------------
// XBT manifest input DTOs. The shape mirrors
// /Engine/Source/Programs/XBT/XBT.Manifest/ManifestSchema.cs verbatim
// for the fields XHT needs at parse time, per /Documents/XHT.html
// Rev 7 Section 9.1 + Contract Section 10.2.
//
// XHT does NOT link XBT.Manifest at runtime per XHT.html Section 2 +
// the architectural decision in Section 25.2 item 1 (standalone-tool
// discipline). Both projects build against the same Manifest.fbs
// schema; the C# DTO duplication here is the intentional cost.
//
// Phase 1b limitation per XHT.html Section 9.1 cross-references to
// Addendum Section 10:
//   - PublicHeaders / PrivateHeaders / InternalHeaders are read as
//     empty arrays (Addendum item 5).
//   - ClassesHeaders is NOT a schema field (Addendum item 4).
//   - XHT discovers headers from SourceFiles list with IsHeader = true.
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
/// not linked at runtime per XHT.html Section 2 standalone-tool
/// discipline; this is a parallel DTO).
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
/// <c>ModuleDep</c>).
/// </summary>
/// <param name="Name">Module name (matches the producing module's Name).</param>
/// <param name="InterfaceModule">True if header-only / no link.</param>
public sealed record XbtModuleDep(
    string Name,
    bool InterfaceModule);

/// <summary>
/// One reflected module's manifest entry. The set of fields here is a
/// strict subset of XBT's <c>Module</c> record -- the subset XHT actually
/// needs at parse time per <c>/Documents/XHT.html</c> Rev 7 Section 9.1.
/// </summary>
/// <param name="Name">Module name (e.g. <c>XScoring</c>).</param>
/// <param name="Tier">Tier (Engine / Studio / Project).</param>
/// <param name="ModuleType">Runtime / Editor / Developer / ThirdParty / Programs.</param>
/// <param name="Languages">Bit-flag of C++ / C# / Both.</param>
/// <param name="BaseDirectory">Module source root, relative to RootLocalPath.</param>
/// <param name="SourceFiles">All source files (XHT iterates IsHeader + IsCSharp entries).</param>
/// <param name="PublicHeaders">Empty in Phase 1 per Addendum Section 10 item 5.</param>
/// <param name="PrivateHeaders">Empty in Phase 1 per Addendum Section 10 item 5.</param>
/// <param name="InternalHeaders">Empty in Phase 1 per Addendum Section 10 item 5.</param>
/// <param name="CSharpSources">C# source files (also enumerable via SourceFiles).</param>
/// <param name="IncludePaths">For resolving #include directives.</param>
/// <param name="PublicDefines">For honouring #if directives.</param>
/// <param name="ModuleDependencies">For cross-module type-reference validation.</param>
/// <param name="GeneratedCPPFilenameBase">Base name for {base}.init.gen.cpp / {base}.gen.manifest.</param>
/// <param name="SimPath">True if SimPath-determinism module.</param>
/// <param name="EngineVersionCompat">Engine compatibility version.</param>
/// <param name="SimdLevel">SIMD baseline override (or Default).</param>
/// <param name="PCHUsage">PCH usage mode.</param>
/// <param name="ExcludeFromSharedPCH">True if module opts out of shared PCH.</param>
/// <param name="AllowHotReload">True if module allows Live Coding hot reload.</param>
/// <param name="IsTestModule">True for test-only modules.</param>
/// <param name="DeprecationMessage">Optional deprecation warning message.</param>
/// <param name="MinimumToolchainVersion">Optional minimum toolchain version gate.</param>
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
    string? MinimumToolchainVersion);

/// <summary>
/// Per-target ABI envelope mirroring XBT's <c>TargetInfo</c> record per
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
/// Top-level XBT manifest record. Mirrors
/// <c>Simgenics.XPact.XBT.Manifest.Manifest</c> (not linked at runtime;
/// the XHT-side reader maintains the parallel DTO shape).
/// </summary>
/// <param name="ContractVersion">Auto-derived contract version string (e.g. <c>"13.2+b04ae3cc84cdd9f3"</c>).</param>
/// <param name="EngineVersion">Engine semver discovered from <c>Engine.xengine</c>.</param>
/// <param name="Target">Per-target fields.</param>
/// <param name="RootLocalPath">Host-local repo root path; forward-slashed.</param>
/// <param name="ExternalDependenciesFile">Optional intermediate file recording the external-deps inventory.</param>
/// <param name="Modules">Modules participating in this build (sorted alphabetically by XBT).</param>
public sealed record XbtManifest(
    string ContractVersion,
    string EngineVersion,
    XbtTargetInfo Target,
    string RootLocalPath,
    string? ExternalDependenciesFile,
    IReadOnlyList<XbtModule> Modules);

/// <summary>
/// Reader for the XBT manifest XHT consumes per
/// <c>/Documents/XHT.html</c> Rev 7 Section 9.1.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 1b implementation.</b> Reads the JSON form only via
/// <see cref="Read"/>. The FBS sidecar reader -- <see cref="TryReadFbsSidecar"/> --
/// is a Phase 1c gate; for Phase 1b it returns null (FlatSharp wiring is
/// deferred until the schema-compile step lands in
/// <c>XHT.Manifest.csproj</c>'s FlatSharp.targets).
/// </para>
/// <para>
/// <b>Hardened reader limits per XHT.html Section 9.5 + Contract Section 10.2.</b>
/// Max payload 100 MB; max depth 64; max array length 65,536. Violations
/// throw <see cref="ManifestMalformedException"/> which XHT.Entry maps to
/// exit code 50.
/// </para>
/// </remarks>
public static class XbtManifestReader
{
    /// <summary>Maximum manifest payload size in bytes (100 MB).</summary>
    public const long MaxManifestBytes = 100L * 1024L * 1024L;

    /// <summary>Maximum JSON nesting depth (per Contract Section 10.2).</summary>
    public const int MaxJsonDepth = 64;

    /// <summary>Maximum array length per Contract Section 10.2.</summary>
    public const int MaxArrayLength = 65536;

    /// <summary>Maximum individual string-field length (16 KB per Contract Section 10.2).</summary>
    public const int MaxStringField = 16 * 1024;

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
        // Property names in XBT's emitted manifest are PascalCase
        // (default System.Text.Json behaviour from the C# record
        // property names). PropertyNameCaseInsensitive lets XHT accept
        // either form so a hand-edited manifest with camelCase keys
        // (or a future emitter that changes naming policy) still
        // parses cleanly.
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
            // XHT001 -- Manifest missing per /Documents/XHT.html Rev 7
            // Section 23.2. The catalog-anchored code lets the entry-point
            // catch surface "error XHT001: ..." instead of the generic
            // XHT050 shim so operators can distinguish missing-file from
            // other manifest failures without parsing the message string.
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.ManifestNotFound,
                message: $"XBT manifest not found: {manifestJsonPath}");
        }

        FileInfo fi = new(manifestJsonPath);
        if (fi.Length > MaxManifestBytes)
        {
            // XHT003 -- Verifier limit violation (manifest payload too
            // large) per Section 23.2.
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.ManifestVerifierRejection,
                message: $"XBT manifest exceeds {MaxManifestBytes} bytes (got {fi.Length}): {manifestJsonPath}");
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(manifestJsonPath);
        }
        catch (IOException ex)
        {
            // I/O during read is classified as a verifier-rejection
            // (XHT003); the manifest file exists but is unreadable, so
            // the verifier cannot validate it.
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.ManifestVerifierRejection,
                message: $"Failed to read XBT manifest at {manifestJsonPath}: {ex.Message}",
                inner: ex);
        }

        return DeserializeJsonBytes(bytes);
    }

    /// <summary>
    /// Try to read the FBS-binary sidecar (<c>Manifest.fbs.bin</c>).
    /// Phase 1b returns null unconditionally; Phase 1c wires up the
    /// FlatSharp greedy-materialised deserializer per Addendum Section 3.2.
    /// </summary>
    /// <param name="manifestFbsBinPath">Absolute path to <c>Manifest.fbs.bin</c>.</param>
    /// <returns>Always null in Phase 1b.</returns>
    public static XbtManifest? TryReadFbsSidecar(string manifestFbsBinPath)
    {
        _ = manifestFbsBinPath; // suppress unused-parameter warning under TreatWarningsAsErrors
        // TODO Phase 1c: wire up FlatSharp greedy-materialised reader.
        // Schema-compile step lands in XHT.Manifest.csproj's FlatSharp.targets
        // mirroring /Engine/Source/Programs/XBT/XBT.Manifest/FlatSharp.targets.
        // Phase 1b is JSON-only per /Documents/XHT.html Rev 7 Section 9.1.
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
            // XHT003 -- Manifest verifier rejection (empty payload is a
            // verifier-limit floor violation).
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.ManifestVerifierRejection,
                message: "XBT manifest payload is empty.");
        }

        if (json.Length > MaxManifestBytes)
        {
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.ManifestVerifierRejection,
                message: $"XBT manifest payload exceeds {MaxManifestBytes} bytes ({json.Length}).");
        }

        // Strip the UTF-8 BOM if a hand-edited manifest carries one.
        // XBT's writer never emits a BOM (System.Text.Json defaults to
        // BOM-less), but a developer editing the manifest in VS Code or
        // Notepad on Windows can accidentally save with a BOM. The
        // hardened JSON reader otherwise chokes on the EF BB BF prefix.
        if (json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF)
        {
            json = json[3..];
        }

        // Pre-validate with the hardened reader so depth + comment + trailing-
        // comma violations are caught before the binder rejects them with a
        // less specific error.
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
            // XHT003 -- Manifest verifier rejection. The hardened reader
            // catches depth / trailing-comma / comment violations here
            // before the binder runs.
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.ManifestVerifierRejection,
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
            // XHT003 -- Manifest verifier rejection at binder stage. A
            // bad-shape payload, an unknown enum member, or a type
            // mismatch lands here.
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.ManifestVerifierRejection,
                message: $"XBT manifest JSON failed to deserialize: {ex.Message}",
                inner: ex);
        }

        if (manifest is null)
        {
            // XHT003 -- Manifest verifier rejection. A literal `null` JSON
            // payload deserializes to a null manifest reference; treat as
            // a verifier-limit violation rather than a silent accept.
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.ManifestVerifierRejection,
                message: "XBT manifest JSON deserialized to null.");
        }

        // Round 5 R4-MA2: ValidateContractVersion MUST run BEFORE
        // ValidateLimits. If a manifest carries both a CV mismatch AND a
        // limits violation (oversize string field, too-deep array, etc.),
        // the operator's actionable fix is "rebuild against the matching
        // Contract" -- the limits violation is a downstream consequence
        // of the schema drift, not a user-fixable problem on its own.
        // Swapping the order surfaces XHT002 (the catalog-anchored
        // diagnostic with both observed + expected version strings)
        // instead of an XHT003 limit message that the operator cannot
        // act on without first knowing the schemas disagree.
        ValidateContractVersion(manifest);
        ValidateLimits(manifest);
        return manifest;
    }

    /// <summary>
    /// Verify the manifest's <see cref="XbtManifest.ContractVersion"/>
    /// matches XHT's compile-time
    /// <see cref="XhtVersion.ContractVersion"/> per
    /// <c>/Documents/XHT.html</c> Rev 7 Section 23.2 (diagnostic
    /// <c>XHT002</c>). Comparison is an ordinal string-equality check
    /// over the full composite <c>&lt;tag&gt;+&lt;hash&gt;</c> form
    /// (e.g. <c>"13.2+b04ae3cc84cdd9f3"</c>).
    /// </summary>
    /// <param name="manifest">The deserialised manifest. Must not be null.</param>
    /// <exception cref="ManifestMalformedException">
    /// Thrown with <see cref="ManifestMalformedException.DiagnosticCode"/>
    /// = <c>"XHT002"</c> and exit code
    /// <see cref="ExitCodes.ManifestMalformed"/> (50) when the manifest's
    /// ContractVersion does not match XHT's. The message names both the
    /// observed and expected values so operators can decide which side to
    /// rebuild.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This is the manifest-schema-mismatch detection the user explicitly
    /// directed XHT to never silently accept (engineering-principles
    /// directive: "do it right the first time, not the easy way"; "no
    /// silent corruption, no flaky edge cases"). Without this check, a
    /// manifest emitted by an XBT that was rebuilt against a newer
    /// Contract revision (with new fields or rotated semantics) would
    /// be silently consumed by an older XHT compiled against the prior
    /// Contract, with the failure surfacing only later as a linker error
    /// or a runtime corruption.
    /// </para>
    /// <para>
    /// The comparison uses <see cref="StringComparison.Ordinal"/> -- the
    /// ContractVersion string is a canonical opaque tag from XBT's
    /// emitter and any case folding or culture-aware comparison would
    /// be a stability bug. The full composite form is compared
    /// (semantic-tag <c>+</c> structure-hash); a partial-tag-only
    /// fallback would defeat the structure-hash protection that
    /// <see cref="XhtVersion.ContractVersion"/> deliberately encodes.
    /// </para>
    /// <para>
    /// <b>Ordering invariant (Round 5 R4-MA2).</b> This check MUST run
    /// before <see cref="ValidateLimits"/>. A manifest that both mismatches
    /// the contract version AND violates a limit (oversize string field,
    /// over-long array, etc.) is fundamentally a schema-drift case: the
    /// limits violation is a downstream consequence the operator cannot
    /// fix without first knowing the schemas disagree. Surfacing XHT002
    /// first gives the operator the actionable diagnostic; the limits
    /// check runs only against payloads that have at least passed the
    /// schema-version gate.
    /// </para>
    /// </remarks>
    private static void ValidateContractVersion(XbtManifest manifest)
    {
        string expected = XhtVersion.ContractVersion;
        string actual = manifest.ContractVersion;
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.ContractVersionMismatch,
                message: string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Manifest ContractVersion '{0}' does not match XHT's compile-time "
                    + "ContractVersion '{1}'. This XHT build was compiled against a different "
                    + "version of the contract surface. Rebuild XHT against the current Contract "
                    + "(or rebuild XBT against the Contract this XHT was compiled with) so both "
                    + "tools agree on the manifest schema.",
                    actual,
                    expected));
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
    /// Per <c>/Documents/XHT.html</c> Rev 7 Section 1.3, a module-not-in-
    /// manifest lookup failure is the caller's signal to exit
    /// <see cref="Simgenics.XPact.XHT.Core.ExitCodes.ManifestMalformed"/>
    /// (50).
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

    private static void ValidateLimits(XbtManifest m)
    {
        CheckString(m.ContractVersion, nameof(m.ContractVersion));
        CheckString(m.EngineVersion, nameof(m.EngineVersion));
        CheckString(m.RootLocalPath, nameof(m.RootLocalPath));
        CheckOptionalString(m.ExternalDependenciesFile, nameof(m.ExternalDependenciesFile));

        if (m.Target is null)
        {
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.ManifestVerifierRejection,
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
                diagnosticCode: DiagnosticCodes.ManifestVerifierRejection,
                message: $"{nameof(m.Modules)} is null (required).");
        }
        if (m.Modules.Count > MaxArrayLength)
        {
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.ManifestVerifierRejection,
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
                diagnosticCode: DiagnosticCodes.ManifestVerifierRejection,
                message: $"{context} is null (required).");
        }
        if (value.Length > MaxStringField)
        {
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.ManifestVerifierRejection,
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
                diagnosticCode: DiagnosticCodes.ManifestVerifierRejection,
                message: $"{context} exceeds {MaxStringField} chars ({value.Length}).");
        }
    }

    private static void CheckStringList(IReadOnlyList<string> list, string context)
    {
        if (list is null)
        {
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.ManifestVerifierRejection,
                message: $"{context} is null (required).");
        }
        if (list.Count > MaxArrayLength)
        {
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.ManifestVerifierRejection,
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
                diagnosticCode: DiagnosticCodes.ManifestVerifierRejection,
                message: $"{context} is null (required).");
        }
        if (list.Count > MaxArrayLength)
        {
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.ManifestVerifierRejection,
                message: $"{context} exceeds {MaxArrayLength} entries ({list.Count}).");
        }
    }
}
