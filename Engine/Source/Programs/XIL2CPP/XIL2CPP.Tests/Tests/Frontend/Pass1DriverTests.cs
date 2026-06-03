// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Manifest;
using Xunit;
using XilSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Frontend;

/// <summary>
/// End-to-end tests for <see cref="Pass1Driver"/>: manifest -&gt; module ->
/// ordered sources -&gt; parse -&gt; resolve refs -&gt; compilation ->
/// diagnostics, plus the determinism guarantee (two runs over identical
/// inputs produce identical diagnostics) per /Documents/XIL2CPP.html Rev 4
/// Section 3.2 + 9.9.
/// </summary>
public sealed class Pass1DriverTests : IDisposable
{
    private readonly string _root;

    public Pass1DriverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "XIL2CPP-Pass1Driver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private void WriteSource(string relativeUnderRoot, string content)
    {
        string full = Path.Combine(_root, relativeUnderRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private Pass1Driver.Pass1Options OptionsNoDeps() => new(
        FrontendTestHelpers.BclReferences(),
        _ => null);

    [Fact]
    public void RunForModule_CleanSource_NoErrors_ParsesAllFiles()
    {
        WriteSource("Mod/A.cs", "namespace Mod; public class A { public int F() => 1; }");
        WriteSource("Mod/B.cs", "namespace Mod; public class B { public int G() => 2; }");

        XbtModule module = FrontendTestHelpers.Module(
            "Mod", "Mod", new[] { "A.cs", "B.cs" });
        XbtManifest manifest = FrontendTestHelpers.Manifest(_root, module);

        Pass1Result result = Pass1Driver.RunForModule(manifest, module, OptionsNoDeps());

        Assert.Equal("Mod", result.ModuleName);
        Assert.Equal(2, result.ParsedFiles.Count);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void RunForModule_FilesAreOrdinalOrdered()
    {
        // Written out of order; the driver must enumerate ordinal-sorted.
        WriteSource("Mod/Zeta.cs", "namespace Mod; public class Zeta { }");
        WriteSource("Mod/Alpha.cs", "namespace Mod; public class Alpha { }");

        XbtModule module = FrontendTestHelpers.Module(
            "Mod", "Mod", new[] { "Zeta.cs", "Alpha.cs" });
        XbtManifest manifest = FrontendTestHelpers.Manifest(_root, module);

        Pass1Result result = Pass1Driver.RunForModule(manifest, module, OptionsNoDeps());

        string[] parsedRelativeNames = result.ParsedFiles
            .Select(p => Path.GetFileName(p.AbsolutePath))
            .ToArray();
        Assert.Equal(new[] { "Alpha.cs", "Zeta.cs" }, parsedRelativeNames);
    }

    [Fact]
    public void RunForModule_SemanticModelIsAvailableAndCached()
    {
        WriteSource("Mod/A.cs", "namespace Mod; public class A { public int F() => 1; }");

        XbtModule module = FrontendTestHelpers.Module("Mod", "Mod", new[] { "A.cs" });
        XbtManifest manifest = FrontendTestHelpers.Manifest(_root, module);

        Pass1Result result = Pass1Driver.RunForModule(manifest, module, OptionsNoDeps());

        SyntaxTree tree = result.ParsedFiles[0].Tree;
        SemanticModel m1 = result.GetSemanticModel(tree);
        SemanticModel m2 = result.GetSemanticModel(tree);

        Assert.NotNull(m1);
        Assert.Same(m1, m2); // cached
    }

    [Fact]
    public void RunForModule_SyntaxError_SetsHasErrors_WithLocation()
    {
        WriteSource("Mod/Bad.cs", "namespace Mod; public class Bad { int x = 1 }"); // missing ;

        XbtModule module = FrontendTestHelpers.Module("Mod", "Mod", new[] { "Bad.cs" });
        XbtManifest manifest = FrontendTestHelpers.Manifest(_root, module);

        Pass1Result result = Pass1Driver.RunForModule(manifest, module, OptionsNoDeps());

        Assert.True(result.HasErrors);
        DiagnosticRecord error = result.Diagnostics.First(d => d.Severity == XilSeverity.Error);
        Assert.NotNull(error.File);
        Assert.NotNull(error.Line);
        Assert.Equal("Mod", error.Module);
    }

    [Fact]
    public void RunForModule_DeterministicDiagnostics_TwoRunsIdentical()
    {
        WriteSource("Mod/A.cs", "namespace Mod; public class A { public Undefined1 F() => null; }");
        WriteSource("Mod/B.cs", "namespace Mod; public class B { public Undefined2 G() => null; }");

        XbtModule module = FrontendTestHelpers.Module("Mod", "Mod", new[] { "A.cs", "B.cs" });
        XbtManifest manifest = FrontendTestHelpers.Manifest(_root, module);

        Pass1Result first = Pass1Driver.RunForModule(manifest, module, OptionsNoDeps());
        Pass1Result second = Pass1Driver.RunForModule(manifest, module, OptionsNoDeps());

        string[] firstFormatted = first.Diagnostics.Select(d => d.FormatMsBuild()).ToArray();
        string[] secondFormatted = second.Diagnostics.Select(d => d.FormatMsBuild()).ToArray();

        Assert.Equal(firstFormatted, secondFormatted);
        Assert.True(first.HasErrors);
    }

    [Fact]
    public void Run_ManifestOnDisk_ModuleNotPresent_Throws50()
    {
        // Write a manifest JSON missing the requested module; RequireModule
        // throws ManifestMalformedException (the caller maps to exit 50).
        string manifestPath = Path.Combine(_root, "Manifest.json");
        File.WriteAllText(manifestPath, OneModuleJson("Present"), new UTF8Encoding(false));

        ManifestMalformedException ex = Assert.Throws<ManifestMalformedException>(
            () => Pass1Driver.Run(manifestPath, "Absent", OptionsNoDeps()));
        Assert.Equal(ExitCodes.ManifestMalformed, ex.ExitCode);
    }

    [Fact]
    public void Run_ManifestOnDisk_CleanModule_Binds()
    {
        WriteSource("Mod/A.cs", "namespace Mod; public class A { public int F() => 1; }");
        string manifestPath = Path.Combine(_root, "Manifest.json");
        File.WriteAllText(manifestPath, OneModuleJson("Mod"), new UTF8Encoding(false));

        Pass1Result result = Pass1Driver.Run(manifestPath, "Mod", OptionsNoDeps());

        Assert.Equal("Mod", result.ModuleName);
        Assert.Single(result.ParsedFiles);
        Assert.False(result.HasErrors);
    }

    private string OneModuleJson(string moduleName)
    {
        // RootLocalPath points at the scratch root so the driver resolves
        // sources under it. ContractVersion is the compile-time pin.
        string rootForwardSlash = _root.Replace('\\', '/');
        return $$"""
            {
              "ContractVersion": "{{Xil2CppVersion.ContractVersion}}",
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
              "RootLocalPath": "{{rootForwardSlash}}",
              "ExternalDependenciesFile": null,
              "Modules": [
                {
                  "Name": "{{moduleName}}",
                  "Tier": "Engine",
                  "ModuleType": "Runtime",
                  "Languages": "CSharp",
                  "BaseDirectory": "Mod",
                  "SourceFiles": [],
                  "PublicHeaders": [],
                  "PrivateHeaders": [],
                  "InternalHeaders": [],
                  "CSharpSources": [ "A.cs" ],
                  "IncludePaths": [],
                  "PublicDefines": [],
                  "ModuleDependencies": [],
                  "GeneratedCPPFilenameBase": "{{moduleName}}",
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
    }
}
