// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Entry;
using Simgenics.XPact.XIL2CPP.Entry.Modes;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Entry;

/// <summary>
/// Tests for <see cref="TranspileModuleMode"/> -- the Phase 6.b Pass 1-3 CLI
/// mode. Drives the mode through <see cref="Program.Main"/> and directly via
/// <see cref="IToolMode.ExecuteAsync"/>. Per /Documents/XIL2CPP.html Rev 4
/// Section 15 + Section 3.2.
/// </summary>
/// <remarks>
/// <para>
/// The mode wires the curated BCL reference set as empty by default (the
/// XPact.CSharp.BCL ref DLL is a later sub-phase), so a module that declares
/// any type binds with predefined-type errors. The exit-0 clean path is
/// therefore exercised with a declaration-free source (an empty compilation
/// unit binds clean with no references). Logger state is process-global, so
/// this collection serialises against the other Logger-touching collections.
/// </para>
/// <para>
/// The Phase 6.b end-to-end pipeline tests install a pinned in-package .NET 8
/// reference set via the mode's internal
/// <c>__SetBclReferencesForTesting</c> seam so a module whose sources
/// reference BCL types binds, exercising the full Pass 1-3 pipeline through
/// the real CLI mode. The seam is reset to null in <see cref="Dispose"/>.
/// </para>
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
        TranspileModuleMode.__SetBclReferencesForTesting(null);

        _root = Path.Combine(Path.GetTempPath(), "XIL2CPP-TranspileMode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Logger.DisableJsonChannel();
        Logger.ResetCounters();
        Logger.__SetStderrForTesting(null);
        TranspileModuleMode.__SetBclReferencesForTesting(null);
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

    /// <summary>
    /// Write a manifest for a single module with an explicit sim-path flag
    /// and an arbitrary list of module-relative C# source paths. Used by the
    /// Phase 6.b end-to-end pipeline tests (which need a sim-path module and
    /// multiple sources, including the XObject stub).
    /// </summary>
    private string WriteManifestEx(
        string moduleName, string baseDir, bool simPath, params string[] sourceRelatives)
    {
        string manifestPath = Path.Combine(_root, "Manifest.json");
        string rootForwardSlash = _root.Replace('\\', '/');
        string sourcesJson = string.Join(
            ", ",
            sourceRelatives.Select(s => "\"" + s.Replace('\\', '/') + "\""));
        string simPathJson = simPath ? "true" : "false";
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
                  "CSharpSources": [ {{sourcesJson}} ],
                  "IncludePaths": [],
                  "PublicDefines": [],
                  "ModuleDependencies": [],
                  "GeneratedCPPFilenameBase": "{{moduleName}}",
                  "SimPath": {{simPathJson}},
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
    public void Mode_Description_SaysPass1Through7_EmitsWhenOutputDirGiven()
    {
        IToolMode mode = ToolModeRegistry.Resolve("transpile-module")!;
        // Phase 6.e: the mode now runs the full Pass 1-7 pipeline (parse + bind
        // + normalize + analyze + tier-classify + mangle + C++ emit + output
        // write) and emits .cs.cpp / .cs.h when --OutputDir is given.
        Assert.Contains("Pass 1-7", mode.Description, StringComparison.Ordinal);
        Assert.Contains("normalize", mode.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("analyze", mode.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tier-classify", mode.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TierTable", mode.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".cs.cpp", mode.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".cs.h", mode.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--OutputDir", mode.Description, StringComparison.Ordinal);
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
        // Phase 6.c: a declaration-free source binds clean through Pass 1 and
        // runs cleanly through Pass 2 + Pass 3 + Pass 4 (no normalizer /
        // analyzer fires on an empty unit, and Pass 4 classifies zero
        // functions), so the mode reports the full pipeline clean.
        Assert.Contains("Pass 1-4 clean", sw.ToString(), StringComparison.Ordinal);
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

    // =================================================================
    // Phase 6.b: end-to-end Pass 1-3 through the real CLI mode. These
    // install a pinned .NET 8 BCL reference set so binding succeeds and the
    // normalizers + analyzers actually fire.
    // =================================================================

    /// <summary>
    /// A locally declared stand-in for the engine root reference type plus
    /// its <c>New</c> factory, mirroring the per-analyzer fixtures so an
    /// <c>XObject</c>-derived <c>new</c> binds + surfaces XIL2CPP001.
    /// </summary>
    private const string XObjectStub = """
        namespace XPact.CoreXObject
        {
            public abstract class XObject
            {
                public static T New<T>(XObject outer, string name, int flags) => default!;
            }
        }
        """;

    private static IReadOnlyList<MetadataReference> Net80Bcl()
        => Basic.Reference.Assemblies.Net80.References.All
            .Cast<MetadataReference>()
            .ToList();

    [Fact]
    public async Task Main_TranspileModule_CleanModule_ReturnsSuccess_NoErrorDiagnostics()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);
        TranspileModuleMode.__SetBclReferencesForTesting(Net80Bcl());

        // A clean, fully-bound non-sim-path module: no banned construct, no
        // XObject-derived new. Binds against the real BCL -> Pass 1-3 clean.
        WriteSource("Mod/Clean.cs",
            "namespace Mod { public class C { public int Add(int a, int b) => a + b; } }");
        string manifest = WriteManifestEx("Mod", "Mod", simPath: false, "Clean.cs");

        int exit = await Program.Main(new[]
        {
            "transpile-module", $"-Manifest={manifest}", "-Module=Mod",
        });

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(0, Logger.ErrorCount);
        Assert.Contains("Pass 1-4 clean", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_TranspileModule_CleanModule_WritesPartialTierTable()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);
        TranspileModuleMode.__SetBclReferencesForTesting(Net80Bcl());

        // Create an Engine/ sibling so the mode's ResolveIntermediateRoot
        // resolves the intermediate root to <_root>/Intermediate/Build/XIL2CPP
        // (it walks up from the manifest looking for an Engine/ sibling).
        Directory.CreateDirectory(Path.Combine(_root, "Engine"));

        // A clean module with one exported method (Tier 1) + one private method
        // (Tier 2) so the table has both kinds.
        WriteSource("Mod/Clean.cs",
            "namespace Mod { public class C { public int Add(int a, int b) => a + b; private int H() => 1; } }");
        string manifest = WriteManifestEx("Mod", "Mod", simPath: false, "Clean.cs");

        int exit = await Program.Main(new[]
        {
            "transpile-module", $"-Manifest={manifest}", "-Module=Mod",
        });

        Assert.Equal(ExitCodes.Success, exit);

        string tablePath = Path.Combine(
            _root, "Intermediate", "Build", "XIL2CPP", "TierTable.partial.Mod.json");
        Assert.True(File.Exists(tablePath), $"Expected TierTable at '{tablePath}'.");

        string json = File.ReadAllText(tablePath);
        Assert.Contains("\"module\": \"Mod\"", json, StringComparison.Ordinal);
        Assert.Contains("\"$schema\"", json, StringComparison.Ordinal);
        // The exported Add is Tier 1; the private H is Tier 2.
        Assert.Contains("\"tier\": \"Tier1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"tier\": \"Tier2\"", json, StringComparison.Ordinal);
        Assert.Contains("wrote partial TierTable", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_TranspileModule_AnalysisError_DoesNotWriteTierTable()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);
        TranspileModuleMode.__SetBclReferencesForTesting(Net80Bcl());

        Directory.CreateDirectory(Path.Combine(_root, "Engine"));

        // A module that fails Pass-3 analysis (new on an XObject-derived type ->
        // XIL2CPP001). A module that failed analysis must NOT publish a partial
        // tier table (it never reaches C++ emit).
        WriteSource("Mod/Stub.cs", XObjectStub);
        WriteSource("Mod/Use.cs",
            "namespace Mod { using XPact.CoreXObject; public sealed class Widget : XObject { } "
            + "public class C { public Widget Make() => new Widget(); } }");
        string manifest = WriteManifestEx("Mod", "Mod", simPath: false, "Stub.cs", "Use.cs");

        int exit = await Program.Main(new[]
        {
            "transpile-module", $"-Manifest={manifest}", "-Module=Mod",
        });

        Assert.Equal(ExitCodes.Xil2CppInternalFailure, exit);

        string tablePath = Path.Combine(
            _root, "Intermediate", "Build", "XIL2CPP", "TierTable.partial.Mod.json");
        Assert.False(File.Exists(tablePath),
            "A module that failed analysis must not publish a partial TierTable.");
    }

    [Fact]
    public async Task Main_TranspileModule_SimPathBannedApi_EmitsXIL2CPP040_AndExit41()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);
        TranspileModuleMode.__SetBclReferencesForTesting(Net80Bcl());

        // A SIM-PATH module calling a banned API (DateTime.Now) -> XIL2CPP040
        // (sim-path banned-API check failure) -> exit 41.
        WriteSource("Mod/Sim.cs",
            "using System; namespace Mod { public class C { public long F() => DateTime.Now.Ticks; } }");
        string manifest = WriteManifestEx("Mod", "Mod", simPath: true, "Sim.cs");

        int exit = await Program.Main(new[]
        {
            "transpile-module", $"-Manifest={manifest}", "-Module=Mod",
        });

        Assert.Equal(ExitCodes.SimPathBannedApiOrManifestEnvelope, exit);
        // The XIL2CPP040 diagnostic was actually emitted on the channel.
        Assert.Contains(DiagnosticCodes.SimPathBannedApiCall, sw.ToString(), StringComparison.Ordinal);
        Assert.True(Logger.ErrorCount >= 1);
    }

    // =================================================================
    // Phase 6.e: --OutputDir drives the full Pass 1-7 emit. With the flag
    // absent the mode behaves as Pass 1-4 (no .cs.cpp / .cs.h written); with
    // it present + Pass 1-3 clean, the emit driver writes the .cs.cpp / .cs.h
    // pair at the expected Transpiled/ paths and the mode still exits 0.
    // =================================================================

    /// <summary>
    /// A locally declared stand-in for the XClass attribute so the emit treats
    /// the test type as an [XClass] (the curated XPact.CSharp.BCL refs are
    /// absent; the emitter recognises the attribute by metadata name +
    /// namespace).
    /// </summary>
    private const string XClassAttributeStub = """
        namespace XPact.CoreXObject
        {
            public sealed class XClassAttribute : System.Attribute { }
        }
        """;

    [Fact]
    public async Task Main_TranspileModule_WithOutputDir_WritesCsCppAndCsH_AndExit0()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);
        TranspileModuleMode.__SetBclReferencesForTesting(Net80Bcl());

        Directory.CreateDirectory(Path.Combine(_root, "Engine"));

        // A small [XClass] module: one auto-property + one method.
        WriteSource("Mod/Attr.cs", XClassAttributeStub);
        WriteSource("Mod/Valve.cs",
            "namespace Mod { [XPact.CoreXObject.XClassAttribute] public class XValve { "
            + "public int Health { get; set; } public int Compute(int x) { return x; } } }");
        string manifest = WriteManifestEx("Mod", "Mod", simPath: false, "Attr.cs", "Valve.cs");

        string outputDir = Path.Combine(_root, "EmitOut");

        int exit = await Program.Main(new[]
        {
            "transpile-module", $"-Manifest={manifest}", "-Module=Mod",
            "--OutputDir", outputDir,
        });

        Assert.Equal(ExitCodes.Success, exit);

        // The .cs.cpp + .cs.h pair for the Valve source lands at the expected
        // Transpiled/ path (the stem derives from the absolute source path).
        string transpiled = Path.Combine(outputDir, "Transpiled");
        Assert.True(Directory.Exists(transpiled), $"Expected Transpiled/ under '{outputDir}'.");

        string[] headers = Directory.GetFiles(transpiled, "*.cs.h", SearchOption.AllDirectories);
        string[] sources = Directory.GetFiles(transpiled, "*.cs.cpp", SearchOption.AllDirectories);
        Assert.NotEmpty(headers);
        Assert.NotEmpty(sources);

        // The Valve .cs.cpp emits the XClass type-level bodies.
        string valveCpp = sources.Select(File.ReadAllText)
            .First(c => c.Contains("XValve::StaticClass()", StringComparison.Ordinal));
        Assert.Contains("Z_Construct_FClass_Mod_XValve() noexcept {", valveCpp);
        Assert.Contains("auto* obj = new (memory) XValve();", valveCpp);

        Assert.Contains("emitted", sw.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Main_TranspileModule_WithoutOutputDir_DoesNotEmitCpp()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);
        TranspileModuleMode.__SetBclReferencesForTesting(Net80Bcl());

        Directory.CreateDirectory(Path.Combine(_root, "Engine"));

        WriteSource("Mod/Attr.cs", XClassAttributeStub);
        WriteSource("Mod/Valve.cs",
            "namespace Mod { [XPact.CoreXObject.XClassAttribute] public class XValve { "
            + "public int Compute(int x) { return x; } } }");
        string manifest = WriteManifestEx("Mod", "Mod", simPath: false, "Attr.cs", "Valve.cs");

        // No --OutputDir -> the emit step is skipped; the mode behaves as today.
        int exit = await Program.Main(new[]
        {
            "transpile-module", $"-Manifest={manifest}", "-Module=Mod",
        });

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Pass 1-4 clean", sw.ToString(), StringComparison.Ordinal);

        // No Transpiled/ tree under the root (the emit step did not run).
        string[] anyCpp = Directory.GetFiles(_root, "*.cs.cpp", SearchOption.AllDirectories);
        Assert.Empty(anyCpp);
    }

    [Fact]
    public async Task Main_TranspileModule_NewXObject_EmitsXIL2CPP001_AndExit63()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);
        TranspileModuleMode.__SetBclReferencesForTesting(Net80Bcl());

        // A module with `new SomeXObject()` -> XIL2CPP001 (Locked Commitment
        // 3). XIL2CPP001 is NOT a sim-path banned-API code -> exit 63.
        WriteSource("Mod/Stub.cs", XObjectStub);
        WriteSource("Mod/Use.cs",
            "namespace Mod { using XPact.CoreXObject; public sealed class Widget : XObject { } "
            + "public class C { public Widget Make() => new Widget(); } }");
        string manifest = WriteManifestEx("Mod", "Mod", simPath: false, "Stub.cs", "Use.cs");

        int exit = await Program.Main(new[]
        {
            "transpile-module", $"-Manifest={manifest}", "-Module=Mod",
        });

        Assert.Equal(ExitCodes.Xil2CppInternalFailure, exit);
        Assert.Contains(DiagnosticCodes.NewExpressionOnXObjectDerived, sw.ToString(), StringComparison.Ordinal);
        Assert.True(Logger.ErrorCount >= 1);
    }
}
