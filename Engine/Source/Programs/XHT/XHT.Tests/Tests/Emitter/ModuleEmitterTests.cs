// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Linq;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Emitter;
using Simgenics.XPact.XHT.Manifest;
using Simgenics.XPact.XHT.Resolver;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Emitter;

/// <summary>
/// Tests for <see cref="ModuleEmitter"/>. Verifies the end-to-end emit
/// pipeline produces the expected file set + manifest sections, in
/// deterministic byte-identical form per XHT.html Section 8 + Section 14.
/// </summary>
[Collection(nameof(ModuleEmitterTests))]
[CollectionDefinition(nameof(ModuleEmitterTests), DisableParallelization = true)]
public sealed class ModuleEmitterTests : IDisposable
{
    private readonly string _tempDir;

    public ModuleEmitterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "XHT.Tests-ModuleEmitter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) { Directory.Delete(_tempDir, recursive: true); } }
        catch { /* best-effort */ }
    }

    private static XbtModule TwoHeaderModule()
    {
        return EmitterTestHarness.MakeModule(
            name: "XGameFramework",
            baseDir: "Engine/Source/Runtime/XGameFramework",
            sourceFiles: new[]
            {
                new XbtSourceFile("Public/XValve.h", IsCSharp: false, IsHeader: true, IsTestOnly: false),
                new XbtSourceFile("Public/XActor.h", IsCSharp: false, IsHeader: true, IsTestOnly: false),
            });
    }

    [Fact]
    public void EmitModule_TwoClasses_TwoHeaders_ProducesExpectedFileSet()
    {
        XbtModule mod = TwoHeaderModule();
        XhtClass valve = EmitterTestHarness.MakeClass("XValve", sourcePath: "Public/XValve.h", line: 14);
        XhtClass actor = EmitterTestHarness.MakeClass("XActor", sourcePath: "Public/XActor.h", line: 8);

        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir, module: mod,
            typesToRegister: new[] { valve, actor });

        ModuleEmitter emitter = new(ctx);
        EmitResult result = emitter.EmitModule();

        Assert.Equal(2, result.GeneratedHeaderFiles.Count);
        Assert.Equal(2, result.GeneratedCppFiles.Count);
        Assert.True(File.Exists(result.ModuleInitCppFile));
        Assert.True(File.Exists(result.GenManifestFile));
        Assert.EndsWith("XGameFramework.init.gen.cpp", result.ModuleInitCppFile, StringComparison.Ordinal);
        Assert.EndsWith("XGameFramework.gen.manifest", result.GenManifestFile, StringComparison.Ordinal);
    }

    [Fact]
    public void EmitModule_GenManifest_HasInputsAndGeneratedSectionsPopulated()
    {
        XbtModule mod = TwoHeaderModule();
        XhtClass valve = EmitterTestHarness.MakeClass("XValve", sourcePath: "Public/XValve.h");
        XhtClass actor = EmitterTestHarness.MakeClass("XActor", sourcePath: "Public/XActor.h");

        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir, module: mod,
            typesToRegister: new[] { valve, actor });
        ModuleEmitter emitter = new(ctx);
        EmitResult result = emitter.EmitModule();

        GenManifest gm = GenManifestReader.Read(result.GenManifestFile);
        Assert.Equal("XGameFramework", gm.ModuleName);
        Assert.Equal(2, gm.Inputs.Length);
        // Generated has 2 .gen.h + 2 .gen.cpp + 1 .init.gen.cpp = 5 entries.
        Assert.Equal(5, gm.Generated.Length);
    }

    [Fact]
    public void EmitModule_DiagnosticsFromResolver_FlowIntoGenManifest()
    {
        XbtModule mod = TwoHeaderModule();
        XhtClass valve = EmitterTestHarness.MakeClass("XValve");
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir, module: mod,
            typesToRegister: new[] { valve });

        // Seed a diagnostic before emit; the emitter forwards it into
        // the .gen.manifest [Diagnostics] section.
        ctx.Diagnostics.Add(new DiagnosticRecord(
            DiagnosticSeverity.Warning,
            "XHT070",
            "Test warning."));

        ModuleEmitter emitter = new(ctx);
        EmitResult result = emitter.EmitModule();

        GenManifest gm = GenManifestReader.Read(result.GenManifestFile);
        Assert.Single(gm.Diagnostics);
        Assert.Equal("XHT070", gm.Diagnostics[0].Code);
        Assert.Equal("warning", gm.Diagnostics[0].Severity);
    }

    [Fact]
    public void EmitModule_EmptyModule_ProducesInitAndManifestOnly_NoHeadersOrCpp()
    {
        XbtModule mod = EmitterTestHarness.MakeModule(
            name: "Empty",
            baseDir: "Engine/Source/Runtime/Empty",
            sourceFiles: Array.Empty<XbtSourceFile>());
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir, module: mod);

        ModuleEmitter emitter = new(ctx);
        EmitResult result = emitter.EmitModule();

        Assert.Empty(result.GeneratedHeaderFiles);
        Assert.Empty(result.GeneratedCppFiles);
        Assert.True(File.Exists(result.ModuleInitCppFile));
        Assert.True(File.Exists(result.GenManifestFile));
    }

    [Fact]
    public void EmitModule_Determinism_TwoRunsProduceByteIdenticalOutputs()
    {
        XbtModule mod = TwoHeaderModule();
        XhtClass valve = EmitterTestHarness.MakeClass("XValve", sourcePath: "Public/XValve.h");

        EmitterContext ctxA = EmitterTestHarness.MakeContext(_tempDir, module: mod,
            typesToRegister: new[] { valve });
        ModuleEmitter emitterA = new(ctxA);
        EmitResult resA = emitterA.EmitModule();
        byte[] hA = File.ReadAllBytes(resA.GeneratedHeaderFiles[0]);
        byte[] cA = File.ReadAllBytes(resA.GeneratedCppFiles[0]);
        byte[] iA = File.ReadAllBytes(resA.ModuleInitCppFile);
        byte[] mA = File.ReadAllBytes(resA.GenManifestFile);

        // Clean up before second run.
        Directory.Delete(_tempDir, recursive: true);
        Directory.CreateDirectory(_tempDir);

        XhtClass valveB = EmitterTestHarness.MakeClass("XValve", sourcePath: "Public/XValve.h");
        EmitterContext ctxB = EmitterTestHarness.MakeContext(_tempDir, module: mod,
            typesToRegister: new[] { valveB });
        ModuleEmitter emitterB = new(ctxB);
        EmitResult resB = emitterB.EmitModule();
        byte[] hB = File.ReadAllBytes(resB.GeneratedHeaderFiles[0]);
        byte[] cB = File.ReadAllBytes(resB.GeneratedCppFiles[0]);
        byte[] iB = File.ReadAllBytes(resB.ModuleInitCppFile);
        byte[] mB = File.ReadAllBytes(resB.GenManifestFile);

        Assert.Equal(hA, hB);
        Assert.Equal(cA, cB);
        Assert.Equal(iA, iB);
        Assert.Equal(mA, mB);
    }

    [Fact]
    public void EmitModule_SentinelForHeaderWithNoTypes_ProducesGenHWithCopyright()
    {
        // Module declares two headers; only one has a reflected type.
        // The other gets a sentinel.
        XbtModule mod = TwoHeaderModule();
        XhtClass valve = EmitterTestHarness.MakeClass("XValve", sourcePath: "Public/XValve.h");

        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir, module: mod,
            typesToRegister: new[] { valve });
        ModuleEmitter emitter = new(ctx);
        EmitResult result = emitter.EmitModule();

        Assert.Equal(2, result.GeneratedHeaderFiles.Count);
        // Find the XActor.gen.h sentinel and assert its content.
        string? sentinelPath = null;
        foreach (string p in result.GeneratedHeaderFiles)
        {
            if (Path.GetFileName(p) == "XActor.gen.h") { sentinelPath = p; }
        }
        Assert.NotNull(sentinelPath);
        string content = File.ReadAllText(sentinelPath!);
        Assert.Contains("// Copyright Simgenics. All Rights Reserved.", content);
        Assert.Contains("no reflected types in this header", content);
    }

    [Fact]
    public void EmitModule_GenManifestContents_OrderedDeterministically()
    {
        XbtModule mod = TwoHeaderModule();
        // Register in reverse-FQN order; emitter must sort.
        XhtClass valve = EmitterTestHarness.MakeClass("ZValve", sourcePath: "Public/XValve.h");
        XhtClass actor = EmitterTestHarness.MakeClass("AActor", sourcePath: "Public/XActor.h");

        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir, module: mod,
            typesToRegister: new[] { valve, actor });
        ModuleEmitter emitter = new(ctx);
        EmitResult result = emitter.EmitModule();
        GenManifest gm = GenManifestReader.Read(result.GenManifestFile);

        // Inputs are sorted by RelativePath ordinal.
        for (int i = 1; i < gm.Inputs.Length; i++)
        {
            Assert.True(
                StringComparer.Ordinal.Compare(gm.Inputs[i - 1].RelativePath, gm.Inputs[i].RelativePath) <= 0);
        }
        for (int i = 1; i < gm.Generated.Length; i++)
        {
            Assert.True(
                StringComparer.Ordinal.Compare(gm.Generated[i - 1].RelativePath, gm.Generated[i].RelativePath) <= 0);
        }
    }

    [Fact]
    public void EmitModule_InitGenCpp_ForwardDeclaresAllSingletonGetters()
    {
        XbtModule mod = TwoHeaderModule();
        XhtClass valve = EmitterTestHarness.MakeClass("XValve", sourcePath: "Public/XValve.h");
        XhtClass actor = EmitterTestHarness.MakeClass("XActor", sourcePath: "Public/XActor.h");

        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir, module: mod,
            typesToRegister: new[] { valve, actor });
        ModuleEmitter emitter = new(ctx);
        EmitResult result = emitter.EmitModule();

        string initContent = File.ReadAllText(result.ModuleInitCppFile);
        Assert.Contains("Z_Construct_XClass_XGameFramework_XValve();", initContent);
        Assert.Contains("Z_Construct_XClass_XGameFramework_XActor();", initContent);
        // RegisterType calls.
        Assert.Contains("XReflectionRuntime::RegisterType(Z_Construct_XClass_XGameFramework_XValve());", initContent);
        Assert.Contains("XReflectionRuntime::RegisterType(Z_Construct_XClass_XGameFramework_XActor());", initContent);
    }

    [Fact]
    public void EmitModule_HashInGenManifestIsHex16()
    {
        XbtModule mod = TwoHeaderModule();
        XhtClass valve = EmitterTestHarness.MakeClass("XValve", sourcePath: "Public/XValve.h");

        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir, module: mod,
            typesToRegister: new[] { valve });
        ModuleEmitter emitter = new(ctx);
        EmitResult result = emitter.EmitModule();
        GenManifest gm = GenManifestReader.Read(result.GenManifestFile);

        foreach (GenManifestEntry e in gm.Generated)
        {
            Assert.Equal(16, e.ContentHash16.Length);
            foreach (char c in e.ContentHash16)
            {
                bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                Assert.True(isHex, $"Hash char '{c}' is not lowercase hex.");
            }
        }
    }

    [Fact]
    public void EmitModule_FilesWrittenAtomically_PersistAcrossRead()
    {
        XbtModule mod = TwoHeaderModule();
        XhtClass valve = EmitterTestHarness.MakeClass("XValve", sourcePath: "Public/XValve.h");

        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir, module: mod,
            typesToRegister: new[] { valve });
        ModuleEmitter emitter = new(ctx);
        EmitResult result = emitter.EmitModule();

        foreach (string p in result.GeneratedHeaderFiles) { Assert.True(File.Exists(p)); }
        foreach (string p in result.GeneratedCppFiles) { Assert.True(File.Exists(p)); }
        Assert.True(File.Exists(result.ModuleInitCppFile));
        Assert.True(File.Exists(result.GenManifestFile));
    }

    [Fact]
    public void EmitModule_UnsupportedManglingScheme_EmitsXht124_AndProducesNoFiles()
    {
        // Round-2 audit C1: a manifest declaring a non-Phase-1 scheme
        // must surface XHT124 and short-circuit emit. No files written;
        // diagnostics list carries the error so callers can decide
        // whether to exit-fail.
        XbtModule mod = TwoHeaderModule();
        XhtClass valve = EmitterTestHarness.MakeClass("XValve", sourcePath: "Public/XValve.h");

        // Synthesize a manifest with a non-default ManglingScheme.
        XbtTargetInfo target = new(
            Name: "TestTarget",
            Type: BuildTargetType.Editor,
            Platform: Platform.Win64,
            Configuration: BuildConfiguration.Development,
            Architecture: "x86_64",
            GCRootABI: "Span-based v1",
            ExceptionABI: "Tier1-Shim/Tier2-Direct",
            ManglingScheme: "MSVC-LengthSuffixed-vNeverGonnaShip",
            FipsMode: false,
            SimPathConservativeRootsAllowed: false,
            SimdLevelDefault: SimdLevel.SSE42,
            StationRole: StationRole.None);
        XbtManifest customManifest = new(
            ContractVersion: "13.2+test",
            EngineVersion: "0.0.0",
            Target: target,
            RootLocalPath: "C:/test",
            ExternalDependenciesFile: null,
            Modules: new[] { mod });

        EmitterContext ctx = EmitterTestHarness.MakeContext(
            _tempDir,
            module: mod,
            typesToRegister: new[] { valve },
            manifestOverride: customManifest);

        ModuleEmitter emitter = new(ctx);
        EmitResult result = emitter.EmitModule();

        // No files produced.
        Assert.Empty(result.GeneratedHeaderFiles);
        Assert.Empty(result.GeneratedCppFiles);
        Assert.Equal(string.Empty, result.ModuleInitCppFile);
        Assert.Equal(string.Empty, result.GenManifestFile);

        // Diagnostic XHT124 surfaces.
        Assert.Contains(result.Diagnostics,
            d => d.Code == DiagnosticCodes.UnsupportedManglingScheme
              && d.Severity == DiagnosticSeverity.Error);
        DiagnosticRecord xht124 = result.Diagnostics.First(
            d => d.Code == DiagnosticCodes.UnsupportedManglingScheme);
        Assert.Contains("MSVC-LengthSuffixed-vNeverGonnaShip", xht124.Message,
            StringComparison.Ordinal);
        Assert.Contains(SymbolNaming.Phase1ManglingScheme, xht124.Message,
            StringComparison.Ordinal);
    }
}
