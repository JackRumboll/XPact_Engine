// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Manifest;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Manifest;

/// <summary>
/// Tests for <see cref="Xil2CppManifestReader"/>. The reader consumes
/// XBT's <c>Manifest.json</c> per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 9.7 + Contract Section 10.2. Mirrors the XHT.Manifest
/// <c>XbtManifestReaderTests</c> pattern with XIL2CPP types; the
/// ContractVersion is referenced symbolically via
/// <see cref="Xil2CppVersion.ContractVersion"/> so the suite tracks the
/// compile-time pin without drift.
/// </summary>
public class Xil2CppManifestReaderTests : IDisposable
{
    private readonly string _tempDir;

    public Xil2CppManifestReaderTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "XIL2CPP.Tests-XbtRdr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }

        GC.SuppressFinalize(this);
    }

    // JSON uses PascalCase keys -- matches what XBT emits today (the C#
    // record property names through System.Text.Json's default policy).
    // XIL2CPP's reader is case-insensitive, so this also exercises both
    // forms via the same test fixture. The ContractVersion is injected
    // symbolically so the literal never drifts from the compile-time pin.
    private static string MinimalValidJson() =>
        MinimalValidJsonWithContractVersion(Xil2CppVersion.ContractVersion);

    private static string OneModuleJson() =>
        OneModuleJsonWithContractVersion(Xil2CppVersion.ContractVersion);

    private static string MinimalValidJsonWithContractVersion(string contractVersion) => $$"""
        {
          "ContractVersion": "{{contractVersion}}",
          "EngineVersion": "0.1.0",
          "Target": {
            "Name": "MiningTrainingEditor",
            "Type": "Editor",
            "Platform": "Win64",
            "Configuration": "Development",
            "Architecture": "x86_64",
            "GCRootABI": "Span-based v1",
            "ExceptionABI": "Tier1-Shim/Tier2-Direct",
            "ManglingScheme": "Itanium-LengthPrefixed-v1",
            "FipsMode": false,
            "SimPathConservativeRootsAllowed": false,
            "SimdLevelDefault": "SSE42",
            "StationRole": "None"
          },
          "RootLocalPath": "C:/repo",
          "ExternalDependenciesFile": null,
          "Modules": []
        }
        """;

    private static string OneModuleJsonWithContractVersion(string contractVersion) => $$"""
        {
          "ContractVersion": "{{contractVersion}}",
          "EngineVersion": "0.1.0",
          "Target": {
            "Name": "MiningTrainingEditor",
            "Type": "Editor",
            "Platform": "Win64",
            "Configuration": "Development",
            "Architecture": "x86_64",
            "GCRootABI": "Span-based v1",
            "ExceptionABI": "Tier1-Shim/Tier2-Direct",
            "ManglingScheme": "Itanium-LengthPrefixed-v1",
            "FipsMode": false,
            "SimPathConservativeRootsAllowed": false,
            "SimdLevelDefault": "SSE42",
            "StationRole": "None"
          },
          "RootLocalPath": "C:/repo",
          "ExternalDependenciesFile": null,
          "Modules": [
            {
              "Name": "XScoring",
              "Tier": "Engine",
              "ModuleType": "Runtime",
              "Languages": "Both",
              "BaseDirectory": "Engine/Source/Runtime/XScoring",
              "SourceFiles": [
                {
                  "RelativePath": "Public/XScoring.h",
                  "IsCSharp": false,
                  "IsHeader": true,
                  "IsTestOnly": false
                },
                {
                  "RelativePath": "Private/Scoring.cs",
                  "IsCSharp": true,
                  "IsHeader": false,
                  "IsTestOnly": false
                }
              ],
              "PublicHeaders": [],
              "PrivateHeaders": [],
              "InternalHeaders": [],
              "CSharpSources": [],
              "IncludePaths": [],
              "PublicDefines": [],
              "ModuleDependencies": [
                { "Name": "XCore", "InterfaceModule": false }
              ],
              "GeneratedCPPFilenameBase": "XScoring",
              "SimPath": false,
              "EngineVersionCompat": "0.1.0",
              "SimdLevel": "Default",
              "PCHUsage": "Default",
              "ExcludeFromSharedPCH": false,
              "AllowHotReload": false,
              "IsTestModule": false,
              "DeprecationMessage": null,
              "MinimumToolchainVersion": null
            }
          ]
        }
        """;

    [Fact]
    public void DeserializeJsonString_MinimalEmpty_ParsesAllTopLevelFields()
    {
        XbtManifest m = Xil2CppManifestReader.DeserializeJsonString(MinimalValidJson());
        Assert.Equal(Xil2CppVersion.ContractVersion, m.ContractVersion);
        Assert.Equal("0.1.0", m.EngineVersion);
        Assert.Equal("MiningTrainingEditor", m.Target.Name);
        Assert.Equal(BuildTargetType.Editor, m.Target.Type);
        Assert.Equal(Platform.Win64, m.Target.Platform);
        Assert.Equal(BuildConfiguration.Development, m.Target.Configuration);
        Assert.Equal("x86_64", m.Target.Architecture);
        Assert.Equal("C:/repo", m.RootLocalPath);
        Assert.Empty(m.Modules);
    }

    [Fact]
    public void DeserializeJsonString_OneModule_ParsesNestedFields()
    {
        XbtManifest m = Xil2CppManifestReader.DeserializeJsonString(OneModuleJson());
        Assert.Single(m.Modules);

        XbtModule mod = m.Modules[0];
        Assert.Equal("XScoring", mod.Name);
        Assert.Equal(ModuleTier.Engine, mod.Tier);
        Assert.Equal(ModuleType.Runtime, mod.ModuleType);
        Assert.Equal(Languages.Both, mod.Languages);
        Assert.Equal(2, mod.SourceFiles.Count);
        Assert.True(mod.SourceFiles[0].IsHeader);
        Assert.False(mod.SourceFiles[0].IsCSharp);
        Assert.True(mod.SourceFiles[1].IsCSharp);
        Assert.False(mod.SourceFiles[1].IsHeader);
        Assert.Single(mod.ModuleDependencies);
        Assert.Equal("XCore", mod.ModuleDependencies[0].Name);
        Assert.False(mod.ModuleDependencies[0].InterfaceModule);
    }

    [Fact]
    public void DeserializeJsonString_MalformedJson_ThrowsManifestMalformedException()
    {
        const string bad = "{ not valid json";
        ManifestMalformedException ex = Assert.Throws<ManifestMalformedException>(
            () => Xil2CppManifestReader.DeserializeJsonString(bad));
        Assert.Equal(ExitCodes.ManifestMalformed, ex.ExitCode);
    }

    [Fact]
    public void DeserializeJsonString_EmptyPayload_ThrowsManifestMalformedException()
    {
        Assert.Throws<ManifestMalformedException>(
            () => Xil2CppManifestReader.DeserializeJsonString(""));
    }

    [Fact]
    public void DeserializeJsonString_RejectsTrailingCommas()
    {
        const string trailing = """{ "modules": [], }""";
        Assert.Throws<ManifestMalformedException>(
            () => Xil2CppManifestReader.DeserializeJsonString(trailing));
    }

    [Fact]
    public void DeserializeJsonString_RejectsJsonComments()
    {
        const string commented = """
            {
              // not allowed
              "modules": []
            }
            """;
        Assert.Throws<ManifestMalformedException>(
            () => Xil2CppManifestReader.DeserializeJsonString(commented));
    }

    [Fact]
    public void DeserializeJsonString_TooDeep_ThrowsManifestMalformedException()
    {
        // 80 levels of array nesting exceeds MaxJsonDepth = 64.
        StringBuilder sb = new();
        for (int i = 0; i < 80; i++) { sb.Append('['); }
        for (int i = 0; i < 80; i++) { sb.Append(']'); }
        Assert.Throws<ManifestMalformedException>(
            () => Xil2CppManifestReader.DeserializeJsonString(sb.ToString()));
    }

    [Fact]
    public void Read_FileDoesNotExist_ThrowsManifestMalformedException()
    {
        string missing = Path.Combine(_tempDir, "nope.json");
        ManifestMalformedException ex = Assert.Throws<ManifestMalformedException>(
            () => Xil2CppManifestReader.Read(missing));
        Assert.Equal(ExitCodes.ManifestMalformed, ex.ExitCode);
        // XIL2CPP's Section 12 catalog allocates no manifest-not-found code
        // (unlike XHT001), so this surfaces un-anchored; the message names
        // the path so the operator can still act.
        Assert.Null(ex.DiagnosticCode);
        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RoundTripsThroughDisk()
    {
        string path = Path.Combine(_tempDir, "Manifest.json");
        // Write without BOM to match XBT's emit convention (System.Text.Json
        // produces BOM-less UTF-8 by default; File.WriteAllText(_, Encoding.UTF8)
        // emits a BOM, which would not match real XBT output).
        UTF8Encoding utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(path, OneModuleJson(), utf8NoBom);
        XbtManifest m = Xil2CppManifestReader.Read(path);
        Assert.Single(m.Modules);
        Assert.Equal("XScoring", m.Modules[0].Name);
    }

    [Fact]
    public void Read_TolerableBomPrefix_StillParses()
    {
        // A hand-edited manifest saved with a UTF-8 BOM must still parse.
        string path = Path.Combine(_tempDir, "ManifestBom.json");
        UTF8Encoding utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(path, MinimalValidJson(), utf8WithBom);
        XbtManifest m = Xil2CppManifestReader.Read(path);
        Assert.Equal(Xil2CppVersion.ContractVersion, m.ContractVersion);
    }

    [Fact]
    public void FindModule_ReturnsModuleWhenPresent()
    {
        XbtManifest m = Xil2CppManifestReader.DeserializeJsonString(OneModuleJson());
        XbtModule? found = Xil2CppManifestReader.FindModule(m, "XScoring");
        Assert.NotNull(found);
        Assert.Equal("XScoring", found!.Name);
    }

    [Fact]
    public void FindModule_ReturnsNullWhenAbsent()
    {
        XbtManifest m = Xil2CppManifestReader.DeserializeJsonString(OneModuleJson());
        XbtModule? notFound = Xil2CppManifestReader.FindModule(m, "XNotPresent");
        Assert.Null(notFound);
    }

    [Fact]
    public void FindModule_IsCaseSensitive()
    {
        XbtManifest m = Xil2CppManifestReader.DeserializeJsonString(OneModuleJson());
        Assert.Null(Xil2CppManifestReader.FindModule(m, "xscoring"));
        Assert.Null(Xil2CppManifestReader.FindModule(m, "XSCORING"));
    }

    [Fact]
    public void RequireModule_ReturnsModuleWhenPresent()
    {
        XbtManifest m = Xil2CppManifestReader.DeserializeJsonString(OneModuleJson());
        XbtModule mod = Xil2CppManifestReader.RequireModule(m, "XScoring");
        Assert.Equal("XScoring", mod.Name);
    }

    [Fact]
    public void RequireModule_ThrowsWhenAbsent_ExitCode50()
    {
        // Module-not-in-manifest is a malformed-input condition surfaced at
        // exit 50 per /Documents/XIL2CPP.html Rev 4 Section 9.7. The XIL2CPP
        // catalog allocates no dedicated code (unlike XHT004), so the
        // exception is un-anchored but names the missing module.
        XbtManifest m = Xil2CppManifestReader.DeserializeJsonString(OneModuleJson());
        ManifestMalformedException ex = Assert.Throws<ManifestMalformedException>(
            () => Xil2CppManifestReader.RequireModule(m, "XNotPresent"));
        Assert.Equal(ExitCodes.ManifestMalformed, ex.ExitCode);
        Assert.Null(ex.DiagnosticCode);
        Assert.Contains("XNotPresent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryReadFbsSidecar_ReturnsNullInPhase6a()
    {
        // Phase 6.a stub: the FBS reader is a deferred gate mirroring
        // XHT.Manifest's identical deferral (XHT does not compile the .fbs
        // in its manifest project either). Returns null unconditionally.
        XbtManifest? m = Xil2CppManifestReader.TryReadFbsSidecar("anything.fbs.bin");
        Assert.Null(m);
    }

    [Fact]
    public void Read_TooLargeFile_ThrowsManifestMalformedException()
    {
        // We can't easily produce 100 MB on disk in a unit test without a
        // long delay. Verify via the byte-array path instead.
        byte[] huge = new byte[Xil2CppManifestReader.MaxManifestBytes + 1];
        ManifestMalformedException ex = Assert.Throws<ManifestMalformedException>(
            () => Xil2CppManifestReader.DeserializeJsonBytes(huge));
        Assert.Equal(ExitCodes.ManifestMalformed, ex.ExitCode);
    }

    // ---------------------------------------------------------------------
    // Phase-6.a accessors: per-module C# source list + dependency-module
    // list per /Documents/XIL2CPP.html Rev 4 Section 9.7. These are the two
    // surfaces Phase 6.a's Roslyn front-end and cross-module prerequisite
    // walk consume.
    // ---------------------------------------------------------------------

    [Fact]
    public void GetCSharpSourceFiles_PicksIsCSharpSourceFiles()
    {
        XbtManifest m = Xil2CppManifestReader.DeserializeJsonString(OneModuleJson());
        XbtModule mod = m.Modules[0];
        IReadOnlyList<string> cs = Xil2CppManifestReader.GetCSharpSourceFiles(mod);
        Assert.Single(cs);
        Assert.Equal("Private/Scoring.cs", cs[0]);
    }

    [Fact]
    public void GetCSharpSourceFiles_UnionsCSharpSourcesListAndIsCSharpEntries_Deduped()
    {
        // CSharpSources may carry an explicit list; SourceFiles may carry
        // IsCSharp entries. The accessor unions them, CSharpSources first,
        // de-duplicating by ordinal path so a path present in both lists
        // appears once.
        string json = $$"""
            {
              "ContractVersion": "{{Xil2CppVersion.ContractVersion}}",
              "EngineVersion": "0.1.0",
              "Target": {
                "Name": "T", "Type": "Editor", "Platform": "Win64",
                "Configuration": "Development", "Architecture": "x86_64",
                "GCRootABI": "g", "ExceptionABI": "e", "ManglingScheme": "m",
                "FipsMode": false, "SimPathConservativeRootsAllowed": false,
                "SimdLevelDefault": "SSE42", "StationRole": "None"
              },
              "RootLocalPath": "C:/repo",
              "ExternalDependenciesFile": null,
              "Modules": [
                {
                  "Name": "M", "Tier": "Engine", "ModuleType": "Runtime",
                  "Languages": "CSharp", "BaseDirectory": "B",
                  "SourceFiles": [
                    { "RelativePath": "A.cs", "IsCSharp": true, "IsHeader": false, "IsTestOnly": false },
                    { "RelativePath": "B.cs", "IsCSharp": true, "IsHeader": false, "IsTestOnly": false },
                    { "RelativePath": "C.h",  "IsCSharp": false, "IsHeader": true, "IsTestOnly": false }
                  ],
                  "PublicHeaders": [], "PrivateHeaders": [], "InternalHeaders": [],
                  "CSharpSources": ["A.cs", "Z.cs"],
                  "IncludePaths": [], "PublicDefines": [], "ModuleDependencies": [],
                  "GeneratedCPPFilenameBase": "M", "SimPath": false,
                  "EngineVersionCompat": "0.1.0", "SimdLevel": "Default",
                  "PCHUsage": "Default", "ExcludeFromSharedPCH": false,
                  "AllowHotReload": false, "IsTestModule": false,
                  "DeprecationMessage": null, "MinimumToolchainVersion": null
                }
              ]
            }
            """;
        XbtManifest m = Xil2CppManifestReader.DeserializeJsonString(json);
        IReadOnlyList<string> cs = Xil2CppManifestReader.GetCSharpSourceFiles(m.Modules[0]);
        // CSharpSources first (A.cs, Z.cs), then IsCSharp SourceFiles not
        // already seen (B.cs); A.cs deduped, C.h excluded (not C#).
        Assert.Equal(new[] { "A.cs", "Z.cs", "B.cs" }, cs);
    }

    [Fact]
    public void GetDependencyModuleNames_ReturnsDependencyNamesInOrder()
    {
        XbtManifest m = Xil2CppManifestReader.DeserializeJsonString(OneModuleJson());
        IReadOnlyList<string> deps = Xil2CppManifestReader.GetDependencyModuleNames(m.Modules[0]);
        Assert.Equal(new[] { "XCore" }, deps);
    }

    // ---------------------------------------------------------------------
    // ContractVersion-mismatch detection per /Documents/XIL2CPP.html Rev 4
    // Section 9.7. XIL2CPP must not silently accept a manifest whose
    // ContractVersion does not match its compile-time pin; the expected
    // value is read symbolically from Xil2CppVersion.ContractVersion.
    // ---------------------------------------------------------------------

    [Fact]
    public void DeserializeJsonString_ContractVersionMatchesCompileTime_Succeeds()
    {
        string json = MinimalValidJsonWithContractVersion(Xil2CppVersion.ContractVersion);
        XbtManifest m = Xil2CppManifestReader.DeserializeJsonString(json);
        Assert.Equal(Xil2CppVersion.ContractVersion, m.ContractVersion);
    }

    [Fact]
    public void DeserializeJsonString_ContractVersionMismatch_ThrowsExit50WithBothValues()
    {
        // Use a synthetic mismatched ContractVersion (different semantic tag
        // AND different structure hash) to force the validator to fire. The
        // thrown exception must (a) be a ManifestMalformedException,
        // (b) carry exit code 50, (c) name BOTH the observed and expected
        // values so operators can decide which side to rebuild.
        const string mismatchedVersion = "99.99+deadbeefcafebabe";
        string json = MinimalValidJsonWithContractVersion(mismatchedVersion);
        ManifestMalformedException ex = Assert.Throws<ManifestMalformedException>(
            () => Xil2CppManifestReader.DeserializeJsonString(json));
        Assert.Equal(ExitCodes.ManifestMalformed, ex.ExitCode);
        Assert.Contains(mismatchedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Contains(Xil2CppVersion.ContractVersion, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeJsonString_ContractVersionDifferOnlyInHashSuffix_Throws()
    {
        // The structure-hash suffix is load-bearing: two manifests with the
        // same semantic tag but different structure hashes describe different
        // contract surfaces and the check must reject the mismatched one.
        const string sameTagDifferentHash = "13.9+0000000000000000";
        Assert.NotEqual(Xil2CppVersion.ContractVersion, sameTagDifferentHash);

        string json = MinimalValidJsonWithContractVersion(sameTagDifferentHash);
        ManifestMalformedException ex = Assert.Throws<ManifestMalformedException>(
            () => Xil2CppManifestReader.DeserializeJsonString(json));
        Assert.Equal(ExitCodes.ManifestMalformed, ex.ExitCode);
        Assert.Contains(sameTagDifferentHash, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeJsonString_ContractVersionMismatchByCase_Throws()
    {
        // The comparison MUST be ordinal (case-sensitive) because the
        // canonical form is lower-case hex; upper-casing the hash must fire
        // the mismatch.
        string upperCased = Xil2CppVersion.ContractVersion.ToUpperInvariant();
        if (string.Equals(upperCased, Xil2CppVersion.ContractVersion, StringComparison.Ordinal))
        {
            // No lowercase hex chars in the pin: the case-flipped value is
            // identical, so this input has nothing to distinguish. Skip.
            return;
        }
        string json = MinimalValidJsonWithContractVersion(upperCased);
        ManifestMalformedException ex = Assert.Throws<ManifestMalformedException>(
            () => Xil2CppManifestReader.DeserializeJsonString(json));
        Assert.Equal(ExitCodes.ManifestMalformed, ex.ExitCode);
    }

    [Fact]
    public void DeserializeJsonString_ContractVersionEmptyString_Throws()
    {
        // An empty ContractVersion is structurally valid JSON but
        // semantically a mismatch -- ValidateLimits accepts a zero-length
        // string (no upper-bound violation) so the ContractVersion check is
        // the layer responsible for rejecting it.
        string json = MinimalValidJsonWithContractVersion("");
        ManifestMalformedException ex = Assert.Throws<ManifestMalformedException>(
            () => Xil2CppManifestReader.DeserializeJsonString(json));
        Assert.Equal(ExitCodes.ManifestMalformed, ex.ExitCode);
    }

    // ---------------------------------------------------------------------
    // Validation ordering (mirrors XHT's R4-MA2 invariant): a manifest that
    // BOTH mismatches the contract version AND violates a limit must surface
    // the ContractVersion-mismatch message (the actionable schema-drift
    // diagnostic) rather than the downstream limit violation.
    // ---------------------------------------------------------------------

    [Fact]
    public void DeserializeJsonString_BothMismatchAndOversizeStringField_SurfacesContractVersionFirst()
    {
        const string mismatchedVersion = "99.99+deadbeefcafebabe";
        string oversizedName = new('X', Xil2CppManifestReader.MaxStringField + 1);

        string json = $$"""
            {
              "ContractVersion": "{{mismatchedVersion}}",
              "EngineVersion": "0.1.0",
              "Target": {
                "Name": "MiningTrainingEditor", "Type": "Editor", "Platform": "Win64",
                "Configuration": "Development", "Architecture": "x86_64",
                "GCRootABI": "Span-based v1", "ExceptionABI": "Tier1-Shim/Tier2-Direct",
                "ManglingScheme": "Itanium-LengthPrefixed-v1", "FipsMode": false,
                "SimPathConservativeRootsAllowed": false, "SimdLevelDefault": "SSE42",
                "StationRole": "None"
              },
              "RootLocalPath": "C:/repo",
              "ExternalDependenciesFile": null,
              "Modules": [
                {
                  "Name": "{{oversizedName}}", "Tier": "Engine", "ModuleType": "Runtime",
                  "Languages": "Both", "BaseDirectory": "Engine/Source/Runtime/X",
                  "SourceFiles": [], "PublicHeaders": [], "PrivateHeaders": [],
                  "InternalHeaders": [], "CSharpSources": [], "IncludePaths": [],
                  "PublicDefines": [], "ModuleDependencies": [],
                  "GeneratedCPPFilenameBase": "X", "SimPath": false,
                  "EngineVersionCompat": "0.1.0", "SimdLevel": "Default",
                  "PCHUsage": "Default", "ExcludeFromSharedPCH": false,
                  "AllowHotReload": false, "IsTestModule": false,
                  "DeprecationMessage": null, "MinimumToolchainVersion": null
                }
              ]
            }
            """;

        ManifestMalformedException ex = Assert.Throws<ManifestMalformedException>(
            () => Xil2CppManifestReader.DeserializeJsonString(json));
        // The actionable mismatch diagnostic surfaces; the limit violation
        // never gets a chance to fire.
        Assert.Contains(mismatchedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Contains(Xil2CppVersion.ContractVersion, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeJsonString_OversizeStringField_WhenContractMatches_ThrowsLimitRejection()
    {
        // Module name exceeds MaxStringField but ContractVersion matches, so
        // the CV check passes and the limit check fires.
        string oversize = new('X', Xil2CppManifestReader.MaxStringField + 1);
        string json = $$"""
            {
              "ContractVersion": "{{Xil2CppVersion.ContractVersion}}",
              "EngineVersion": "0.1.0",
              "Target": {
                "Name": "MiningTrainingEditor", "Type": "Editor", "Platform": "Win64",
                "Configuration": "Development", "Architecture": "x86_64",
                "GCRootABI": "Span-based v1", "ExceptionABI": "Tier1-Shim/Tier2-Direct",
                "ManglingScheme": "Itanium-LengthPrefixed-v1", "FipsMode": false,
                "SimPathConservativeRootsAllowed": false, "SimdLevelDefault": "SSE42",
                "StationRole": "None"
              },
              "RootLocalPath": "C:/repo",
              "ExternalDependenciesFile": null,
              "Modules": [
                {
                  "Name": "{{oversize}}", "Tier": "Engine", "ModuleType": "Runtime",
                  "Languages": "Both", "BaseDirectory": "Engine/Source/Runtime/X",
                  "SourceFiles": [], "PublicHeaders": [], "PrivateHeaders": [],
                  "InternalHeaders": [], "CSharpSources": [], "IncludePaths": [],
                  "PublicDefines": [], "ModuleDependencies": [],
                  "GeneratedCPPFilenameBase": "X", "SimPath": false,
                  "EngineVersionCompat": "0.1.0", "SimdLevel": "Default",
                  "PCHUsage": "Default", "ExcludeFromSharedPCH": false,
                  "AllowHotReload": false, "IsTestModule": false,
                  "DeprecationMessage": null, "MinimumToolchainVersion": null
                }
              ]
            }
            """;
        ManifestMalformedException ex = Assert.Throws<ManifestMalformedException>(
            () => Xil2CppManifestReader.DeserializeJsonString(json));
        Assert.Equal(ExitCodes.ManifestMalformed, ex.ExitCode);
        Assert.Contains("exceeds", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // Forward-compatibility: fields the spec references but the current
    // Manifest.fbs omits (schema_version, conditional_symbols,
    // roslyn_version, dotnet_sdk_version) are read-when-present and
    // default-when-absent. The schema-version gate is present but inert.
    // ---------------------------------------------------------------------

    [Fact]
    public void DeserializeJsonString_ForwardCompatFieldsAbsent_ParsesWithDefaults()
    {
        // The minimal manifest omits SchemaVersion / RoslynVersion /
        // DotNetSdkVersion / ConditionalSymbols entirely; the reader must
        // default them rather than fail.
        XbtManifest m = Xil2CppManifestReader.DeserializeJsonString(MinimalValidJson());
        Assert.Null(m.SchemaVersion);
        Assert.Null(m.RoslynVersion);
        Assert.Null(m.DotNetSdkVersion);
    }

    [Fact]
    public void DeserializeJsonString_OneModule_ConditionalSymbolsDefaultsToNullWhenAbsent()
    {
        XbtManifest m = Xil2CppManifestReader.DeserializeJsonString(OneModuleJson());
        Assert.Null(m.Modules[0].ConditionalSymbols);
    }

    [Fact]
    public void DeserializeJsonString_ForwardCompatFieldsPresent_AreRead()
    {
        // A future XBT that appends these fields must round-trip through the
        // reader. SchemaVersion is set to the supported value so the inert
        // gate accepts it.
        string json = $$"""
            {
              "ContractVersion": "{{Xil2CppVersion.ContractVersion}}",
              "EngineVersion": "0.1.0",
              "SchemaVersion": {{Xil2CppManifestReader.SupportedSchemaVersion}},
              "RoslynVersion": "4.11.0",
              "DotNetSdkVersion": "8.0.100",
              "Target": {
                "Name": "T", "Type": "Editor", "Platform": "Win64",
                "Configuration": "Development", "Architecture": "x86_64",
                "GCRootABI": "g", "ExceptionABI": "e", "ManglingScheme": "m",
                "FipsMode": false, "SimPathConservativeRootsAllowed": false,
                "SimdLevelDefault": "SSE42", "StationRole": "None"
              },
              "RootLocalPath": "C:/repo",
              "ExternalDependenciesFile": null,
              "Modules": []
            }
            """;
        XbtManifest m = Xil2CppManifestReader.DeserializeJsonString(json);
        Assert.Equal(Xil2CppManifestReader.SupportedSchemaVersion, m.SchemaVersion);
        Assert.Equal("4.11.0", m.RoslynVersion);
        Assert.Equal("8.0.100", m.DotNetSdkVersion);
    }

    [Fact]
    public void DeserializeJsonString_SchemaVersionGate_RejectsUnsupportedVersion_XIL2CPP141()
    {
        // When SchemaVersion is present and non-zero but differs from the
        // supported value, the (otherwise inert) gate fires, anchored to the
        // catalog code XIL2CPP141 at exit 50.
        uint unsupported = Xil2CppManifestReader.SupportedSchemaVersion + 999;
        string json = $$"""
            {
              "ContractVersion": "{{Xil2CppVersion.ContractVersion}}",
              "EngineVersion": "0.1.0",
              "SchemaVersion": {{unsupported}},
              "Target": {
                "Name": "T", "Type": "Editor", "Platform": "Win64",
                "Configuration": "Development", "Architecture": "x86_64",
                "GCRootABI": "g", "ExceptionABI": "e", "ManglingScheme": "m",
                "FipsMode": false, "SimPathConservativeRootsAllowed": false,
                "SimdLevelDefault": "SSE42", "StationRole": "None"
              },
              "RootLocalPath": "C:/repo",
              "ExternalDependenciesFile": null,
              "Modules": []
            }
            """;
        ManifestMalformedException ex = Assert.Throws<ManifestMalformedException>(
            () => Xil2CppManifestReader.DeserializeJsonString(json));
        Assert.Equal(ExitCodes.ManifestMalformed, ex.ExitCode);
        Assert.Equal(DiagnosticCodes.ManifestUnsupportedSchemaVersion, ex.DiagnosticCode);
    }

    [Fact]
    public void DeserializeJsonString_SchemaVersionZero_TreatedAsAbsent()
    {
        // A literal 0 is the same as absent (inert gate accepts it).
        string json = $$"""
            {
              "ContractVersion": "{{Xil2CppVersion.ContractVersion}}",
              "EngineVersion": "0.1.0",
              "SchemaVersion": 0,
              "Target": {
                "Name": "T", "Type": "Editor", "Platform": "Win64",
                "Configuration": "Development", "Architecture": "x86_64",
                "GCRootABI": "g", "ExceptionABI": "e", "ManglingScheme": "m",
                "FipsMode": false, "SimPathConservativeRootsAllowed": false,
                "SimdLevelDefault": "SSE42", "StationRole": "None"
              },
              "RootLocalPath": "C:/repo",
              "ExternalDependenciesFile": null,
              "Modules": []
            }
            """;
        XbtManifest m = Xil2CppManifestReader.DeserializeJsonString(json);
        Assert.Equal(0u, m.SchemaVersion);
    }
}
