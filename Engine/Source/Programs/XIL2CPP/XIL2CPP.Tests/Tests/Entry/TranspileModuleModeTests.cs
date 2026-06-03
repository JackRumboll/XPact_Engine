// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Entry;
using Simgenics.XPact.XIL2CPP.Entry.Modes;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Entry;

/// <summary>
/// Tests for <see cref="TranspileModuleMode"/> -- the Phase 6.a Pass-1 CLI
/// mode. Drives the mode through <see cref="Program.Main"/> and directly via
/// <see cref="IToolMode.ExecuteAsync"/>. Per /Documents/XIL2CPP.html Rev 4
/// Section 15 + Section 3.2.
/// </summary>
/// <remarks>
/// Phase 6.a wires the curated BCL reference set as empty (the
/// XPact.CSharp.BCL ref DLL is a later sub-phase), so a module that declares
/// any type binds with predefined-type errors. The exit-0 clean path is
/// therefore exercised with a declaration-free source (an empty compilation
/// unit binds clean with no references). Logger state is process-global, so
/// this collection serialises against the other Logger-touching collections.
/// </remarks>
[Collection(nameof(TranspileModuleModeTests))]
[CollectionDefinition(nameof(TranspileModuleModeTests), DisableParallelization = true)]
public sealed class TranspileModuleModeTests : IDisposable
{
    private readonly string _root;

    public TranspileModuleModeTests()
    {
        Logger.DisableJsonChannel();
        Logger.ResetCounters();
        Logger.__SetStderrForTesting(null);

        _root = Path.Combine(Path.GetTempPath(), "XIL2CPP-TranspileMode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Logger.DisableJsonChannel();
        Logger.ResetCounters();
        Logger.__SetStderrForTesting(null);
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

    private string WriteManifest(string moduleName, string baseDir, string sourceRelative)
    {
        string manifestPath = Path.Combine(_root, "Manifest.json");
        string rootForwardSlash = _root.Replace('\\', '/');
        string json = $$"""
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
                  "BaseDirectory": "{{baseDir}}",
                  "SourceFiles": [],
                  "PublicHeaders": [],
                  "PrivateHeaders": [],
                  "InternalHeaders": [],
                  "CSharpSources": [ "{{sourceRelative}}" ],
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
        File.WriteAllText(manifestPath, json, new UTF8Encoding(false));
        return manifestPath;
    }

    [Fact]
    public void Mode_IsRegistered_UnderTranspileModuleName()
    {
        IToolMode? mode = ToolModeRegistry.Resolve("transpile-module");
        Assert.NotNull(mode);
        Assert.IsType<TranspileModuleMode>(mode);
    }

    [Fact]
    public void Mode_Description_SaysPass1Only()
    {
        IToolMode mode = ToolModeRegistry.Resolve("transpile-module")!;
        Assert.Contains("Pass 1", mode.Description, StringComparison.Ordinal);
        Assert.Contains("no C++ emit", mode.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_MissingFlags_ReturnsCliArgumentError()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        IToolMode mode = new TranspileModuleMode();
        int exit = await mode.ExecuteAsync(Array.Empty<string>(), CancellationToken.None);

        Assert.Equal(ExitCodes.CliArgumentError, exit);
    }

    [Fact]
    public async Task Main_TranspileModule_ManifestNotFound_ReturnsManifestMalformed()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        string missing = Path.Combine(_root, "does-not-exist.json");
        int exit = await Program.Main(new[]
        {
            "transpile-module", $"-Manifest={missing}", "-Module=Whatever",
        });

        Assert.Equal(ExitCodes.ManifestMalformed, exit);
    }

    [Fact]
    public async Task Main_TranspileModule_ModuleNotInManifest_ReturnsManifestMalformed()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        WriteSource("Mod/A.cs", "namespace Mod;");
        string manifest = WriteManifest("Present", "Mod", "A.cs");

        int exit = await Program.Main(new[]
        {
            "transpile-module", $"-Manifest={manifest}", "-Module=Absent",
        });

        Assert.Equal(ExitCodes.ManifestMalformed, exit);
    }

    [Fact]
    public async Task Main_TranspileModule_DeclarationFreeSource_ReturnsSuccess()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        // A declaration-free compilation unit binds clean even with the
        // empty Phase-6.a BCL reference set (no predefined type is required).
        WriteSource("Mod/Empty.cs", "// only a comment; no type declarations\n");
        string manifest = WriteManifest("Mod", "Mod", "Empty.cs");

        int exit = await Program.Main(new[]
        {
            "transpile-module", $"-Manifest={manifest}", "-Module=Mod",
        });

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Pass 1 clean", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_TranspileModule_TypeDeclarationWithoutBcl_ReturnsInternalFailure()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        // A type declaration requires System.Object, which the empty
        // Phase-6.a BCL set does not provide -> Pass-1 binder error ->
        // exit 63 (the canonical XIL2CPP analysis-failure code).
        WriteSource("Mod/C.cs", "namespace Mod; public class C { }");
        string manifest = WriteManifest("Mod", "Mod", "C.cs");

        int exit = await Program.Main(new[]
        {
            "transpile-module", $"-Manifest={manifest}", "-Module=Mod",
        });

        Assert.Equal(ExitCodes.Xil2CppInternalFailure, exit);
    }

    [Fact]
    public async Task ExecuteAsync_Cancelled_ReturnsCancelled()
    {
        WriteSource("Mod/A.cs", "namespace Mod;");
        string manifest = WriteManifest("Mod", "Mod", "A.cs");

        using CancellationTokenSource cts = new();
        cts.Cancel();

        IToolMode mode = new TranspileModuleMode();
        int exit = await mode.ExecuteAsync(
            new[] { $"-Manifest={manifest}", "-Module=Mod" },
            cts.Token);

        Assert.Equal(ExitCodes.Cancelled, exit);
    }
}
