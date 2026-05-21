// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Manifest;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Manifest;

/// <summary>
/// Tests for <see cref="XbtManifestReader"/>. The reader consumes XBT's
/// <c>Manifest.json</c> per <c>/Documents/XHT.html</c> Rev 5
/// Section 9.1 + Contract Section 10.2.
/// </summary>
public class XbtManifestReaderTests : IDisposable
{
    private readonly string _tempDir;

    public XbtManifestReaderTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "XHT.Tests-XbtRdr-" + Guid.NewGuid().ToString("N"));
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
    }

    // JSON uses PascalCase keys -- matches what XBT emits today (the C# record
    // property names through System.Text.Json's default policy). XHT's reader
    // is case-insensitive, so this also exercises both forms via the same
    // test fixture.
    private static string MinimalValidJson() => """
        {
          "ContractVersion": "13.2+b04ae3cc84cdd9f3",
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

    private static string OneModuleJson() => """
        {
          "ContractVersion": "13.2+b04ae3cc84cdd9f3",
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
        XbtManifest m = XbtManifestReader.DeserializeJsonString(MinimalValidJson());
        Assert.Equal("13.2+b04ae3cc84cdd9f3", m.ContractVersion);
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
        XbtManifest m = XbtManifestReader.DeserializeJsonString(OneModuleJson());
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
            () => XbtManifestReader.DeserializeJsonString(bad));
        Assert.Equal(ExitCodes.ManifestMalformed, ex.ExitCode);
    }

    [Fact]
    public void DeserializeJsonString_EmptyPayload_ThrowsManifestMalformedException()
    {
        Assert.Throws<ManifestMalformedException>(
            () => XbtManifestReader.DeserializeJsonString(""));
    }

    [Fact]
    public void DeserializeJsonString_RejectsTrailingCommas()
    {
        const string trailing = """{ "modules": [], }""";
        Assert.Throws<ManifestMalformedException>(
            () => XbtManifestReader.DeserializeJsonString(trailing));
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
            () => XbtManifestReader.DeserializeJsonString(commented));
    }

    [Fact]
    public void DeserializeJsonString_TooDeep_ThrowsManifestMalformedException()
    {
        // 80 levels of array nesting exceeds MaxJsonDepth = 64.
        StringBuilder sb = new();
        for (int i = 0; i < 80; i++) { sb.Append('['); }
        for (int i = 0; i < 80; i++) { sb.Append(']'); }
        Assert.Throws<ManifestMalformedException>(
            () => XbtManifestReader.DeserializeJsonString(sb.ToString()));
    }

    [Fact]
    public void Read_FileDoesNotExist_ThrowsManifestMalformedException()
    {
        string missing = Path.Combine(_tempDir, "nope.json");
        ManifestMalformedException ex = Assert.Throws<ManifestMalformedException>(
            () => XbtManifestReader.Read(missing));
        Assert.Equal(ExitCodes.ManifestMalformed, ex.ExitCode);
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
        XbtManifest m = XbtManifestReader.Read(path);
        Assert.Single(m.Modules);
        Assert.Equal("XScoring", m.Modules[0].Name);
    }

    [Fact]
    public void FindModule_ReturnsModuleWhenPresent()
    {
        XbtManifest m = XbtManifestReader.DeserializeJsonString(OneModuleJson());
        XbtModule? found = XbtManifestReader.FindModule(m, "XScoring");
        Assert.NotNull(found);
        Assert.Equal("XScoring", found!.Name);
    }

    [Fact]
    public void FindModule_ReturnsNullWhenAbsent()
    {
        XbtManifest m = XbtManifestReader.DeserializeJsonString(OneModuleJson());
        XbtModule? notFound = XbtManifestReader.FindModule(m, "XNotPresent");
        Assert.Null(notFound);
    }

    [Fact]
    public void FindModule_IsCaseSensitive()
    {
        XbtManifest m = XbtManifestReader.DeserializeJsonString(OneModuleJson());
        Assert.Null(XbtManifestReader.FindModule(m, "xscoring"));
        Assert.Null(XbtManifestReader.FindModule(m, "XSCORING"));
    }

    [Fact]
    public void TryReadFbsSidecar_ReturnsNullInPhase1b()
    {
        // Phase 1b stub: FBS reader is wired in Phase 1c per XHT.html
        // Section 9.4. Returns null unconditionally for now.
        XbtManifest? m = XbtManifestReader.TryReadFbsSidecar("anything.fbs.bin");
        Assert.Null(m);
    }

    [Fact]
    public void Read_TooLargeFile_ThrowsManifestMalformedException()
    {
        // We can't easily produce 100 MB on disk in a unit test without
        // long delay. Verify via the byte-array path instead.
        byte[] huge = new byte[XbtManifestReader.MaxManifestBytes + 1];
        ManifestMalformedException ex = Assert.Throws<ManifestMalformedException>(
            () => XbtManifestReader.DeserializeJsonBytes(huge));
        Assert.Equal(ExitCodes.ManifestMalformed, ex.ExitCode);
    }
}
