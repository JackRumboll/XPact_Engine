// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Emitter;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Emitter;

/// <summary>
/// Tests for <see cref="ModuleInitEmitter"/>. Verifies the per-module
/// aggregator shape + ordering + empty form match XHT.html Section 8.3 /
/// 8.4.
/// </summary>
[Collection(nameof(ModuleInitEmitterTests))]
[CollectionDefinition(nameof(ModuleInitEmitterTests), DisableParallelization = true)]
public sealed class ModuleInitEmitterTests : IDisposable
{
    private readonly string _tempDir;

    public ModuleInitEmitterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "XHT.Tests-ModuleInitEmitter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) { Directory.Delete(_tempDir, recursive: true); } }
        catch { /* best-effort */ }
    }

    [Fact]
    public void OneType_EmitsForwardDeclAndRegisterTypeCall()
    {
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        ModuleInitEmitter emitter = new(ctx);
        XhtClass valve = EmitterTestHarness.MakeClass("XValve");

        string content = emitter.Render("XGameFramework", new[] { valve });

        Assert.Contains("extern \"C\" const struct XClass* Z_Construct_XClass_XGameFramework_XValve();", content);
        Assert.Contains("XReflectionRuntime::RegisterType(Z_Construct_XClass_XGameFramework_XValve());", content);
        Assert.Contains("XGameFramework_AutoRegister", content);
    }

    [Fact]
    public void MultiType_EmitsInOrdinalFqnOrder()
    {
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        ModuleInitEmitter emitter = new(ctx);

        XhtClass cZ = EmitterTestHarness.MakeClass("ZType");
        XhtClass cA = EmitterTestHarness.MakeClass("AType");
        string content = emitter.Render("XGameFramework", new XhtTypeBase[] { cZ, cA });

        int aIdx = content.IndexOf("RegisterType(Z_Construct_XClass_XGameFramework_AType()", StringComparison.Ordinal);
        int zIdx = content.IndexOf("RegisterType(Z_Construct_XClass_XGameFramework_ZType()", StringComparison.Ordinal);
        Assert.True(aIdx > 0);
        Assert.True(zIdx > 0);
        Assert.True(aIdx < zIdx);
    }

    [Fact]
    public void EmptyModule_EmitsValidFile_WithNoRegisterTypeCalls()
    {
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        ModuleInitEmitter emitter = new(ctx);

        string content = emitter.Render("XGameFramework", Array.Empty<XhtTypeBase>());

        Assert.Contains("// Copyright Simgenics. All Rights Reserved.", content);
        Assert.Contains("XGameFramework_AutoRegister", content);
        Assert.Contains("module has no reflected types", content);
        Assert.DoesNotContain("RegisterType(", content);
    }

    [Fact]
    public void Determinism_TwoRunsOverSameInput_ProduceByteIdenticalOutput()
    {
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        ModuleInitEmitter emitter = new(ctx);
        XhtClass valve = EmitterTestHarness.MakeClass("XValve");

        string a = emitter.Render("XGameFramework", new[] { valve });
        string b = emitter.Render("XGameFramework", new[] { valve });
        Assert.Equal(a, b);
    }

    [Fact]
    public void EmitForModule_WritesUnderOutputDirectory_WithLfOnly()
    {
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        ModuleInitEmitter emitter = new(ctx);
        XhtClass valve = EmitterTestHarness.MakeClass("XValve");

        string path = emitter.EmitForModule("XGameFramework", new[] { valve });
        Assert.True(File.Exists(path));
        Assert.StartsWith(_tempDir, path, StringComparison.Ordinal);
        Assert.EndsWith("XGameFramework.init.gen.cpp", path, StringComparison.Ordinal);

        byte[] bytes = File.ReadAllBytes(path);
        foreach (byte b in bytes)
        {
            Assert.NotEqual((byte)'\r', b);
        }
    }

    [Fact]
    public void DeriveModuleInitFileName_UsesGeneratedCPPFilenameBaseWhenSet()
    {
        var m = EmitterTestHarness.MakeModule(name: "ModA");
        Assert.Equal("ModA.init.gen.cpp", ModuleInitEmitter.DeriveModuleInitFileName(m));
    }
}
