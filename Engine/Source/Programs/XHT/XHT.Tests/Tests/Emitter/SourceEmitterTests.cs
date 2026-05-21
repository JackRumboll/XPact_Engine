// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Emitter;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Emitter;

/// <summary>
/// Tests for <see cref="SourceEmitter"/>. Verifies the .gen.cpp shape +
/// determinism + sentinel form match XHT.html Section 8.2 / 8.4.
/// </summary>
[Collection(nameof(SourceEmitterTests))]
[CollectionDefinition(nameof(SourceEmitterTests), DisableParallelization = true)]
public sealed class SourceEmitterTests : IDisposable
{
    private readonly string _tempDir;

    public SourceEmitterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "XHT.Tests-SourceEmitter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) { Directory.Delete(_tempDir, recursive: true); } }
        catch { /* best-effort */ }
    }

    [Fact]
    public void OneClass_EmitsConstInitConstantAndSingletonGetter()
    {
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        SourceEmitter emitter = new(ctx);
        XhtClass valve = EmitterTestHarness.MakeClass("AXValve");

        string content = emitter.Render("Public/XValve.h", new[] { valve });

        Assert.Contains("XCONSTINIT static const XClassDescriptor Z_ConstInit_XClass_XGameFramework_AXValve", content);
        Assert.Contains("Z_Construct_XClass_XGameFramework_AXValve", content);
        Assert.Contains("static_assert(XPACT_WITH_CONSTINIT_XOBJECT", content);
        Assert.Contains("// Copyright Simgenics. All Rights Reserved.", content);
    }

    [Fact]
    public void EmptyHeader_EmitsSentinelGenCpp_WithEmptyLinkFunction()
    {
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        SourceEmitter emitter = new(ctx);

        string content = emitter.Render("Public/Empty.h", Array.Empty<XhtTypeBase>());

        Assert.Contains("// Copyright Simgenics. All Rights Reserved.", content);
        Assert.Contains("no reflected types in this header", content);
        Assert.Contains("void EmptyLinkFunctionForGeneratedCode_Empty()", content);
        // No ConstInit symbols in the sentinel form.
        Assert.DoesNotContain("Z_ConstInit_", content);
    }

    [Fact]
    public void MultiClassHeader_EmitsBlockPerType_InOrdinalFqnOrder()
    {
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        SourceEmitter emitter = new(ctx);

        XhtClass cZ = EmitterTestHarness.MakeClass("ZFoo");
        XhtClass cA = EmitterTestHarness.MakeClass("ABar");
        string content = emitter.Render("Public/Multi.h", new XhtTypeBase[] { cZ, cA });

        int aIdx = content.IndexOf("ConstInit emit for ABar", StringComparison.Ordinal);
        int zIdx = content.IndexOf("ConstInit emit for ZFoo", StringComparison.Ordinal);
        Assert.True(aIdx > 0);
        Assert.True(zIdx > 0);
        Assert.True(aIdx < zIdx, "ABar must come before ZFoo by ordinal FQN sort.");
    }

    [Fact]
    public void ClassWithProperties_EmitsCommentLinePerProperty()
    {
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        SourceEmitter emitter = new(ctx);

        XhtProperty p1 = EmitterTestHarness.MakeProperty("OpenFraction", "float");
        XhtProperty p2 = EmitterTestHarness.MakeProperty("Inlet", "XActor*");
        XhtClass valve = EmitterTestHarness.MakeClass("AXValve", properties: new[] { p1, p2 });

        string content = emitter.Render("Public/XValve.h", new[] { valve });
        Assert.Contains("STAGE-B: OpenFraction: float ->", content);
        Assert.Contains("STAGE-B: Inlet: XActor* ->", content);
    }

    [Fact]
    public void ClassWithFunctions_EmitsCommentLinePerFunction()
    {
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        SourceEmitter emitter = new(ctx);
        XhtFunction f1 = EmitterTestHarness.MakeFunction("Open", "void");
        XhtFunction f2 = EmitterTestHarness.MakeFunction("Close", "bool");
        XhtClass valve = EmitterTestHarness.MakeClass("AXValve", functions: new[] { f1, f2 });

        string content = emitter.Render("Public/XValve.h", new[] { valve });
        Assert.Contains("Open() -> void", content);
        Assert.Contains("Close() -> bool", content);
    }

    [Fact]
    public void Determinism_TwoRunsOverSameInput_ProduceByteIdenticalOutput()
    {
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        SourceEmitter emitter = new(ctx);
        XhtClass valve = EmitterTestHarness.MakeClass("AXValve");

        string a = emitter.Render("Public/XValve.h", new[] { valve });
        string b = emitter.Render("Public/XValve.h", new[] { valve });
        Assert.Equal(a, b);
    }

    [Fact]
    public void EmitForHeader_WritesUnderOutputDirectory_WithLfOnlyEndings()
    {
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        SourceEmitter emitter = new(ctx);
        XhtClass valve = EmitterTestHarness.MakeClass("AXValve");

        string path = emitter.EmitForHeader("Public/XValve.h", new[] { valve });
        Assert.True(File.Exists(path));
        Assert.StartsWith(_tempDir, path, StringComparison.Ordinal);
        Assert.EndsWith("XValve.gen.cpp", path, StringComparison.Ordinal);

        byte[] bytes = File.ReadAllBytes(path);
        foreach (byte b in bytes)
        {
            Assert.NotEqual((byte)'\r', b);
        }
    }

    [Fact]
    public void StructEmit_UsesXStructDescriptorAndCorrectGetter()
    {
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        SourceEmitter emitter = new(ctx);
        XhtStruct s = EmitterTestHarness.MakeStruct("FVec");

        string content = emitter.Render("Public/Vec.h", new XhtTypeBase[] { s });
        Assert.Contains("Z_Construct_XStruct_XGameFramework_FVec", content);
        Assert.Contains("Z_ConstInit_XStruct_XGameFramework_FVec", content);
        Assert.Contains("XStructDescriptor", content);
    }

    [Fact]
    public void EnumEmit_IncludesEnumValueDescriptorTable()
    {
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        SourceEmitter emitter = new(ctx);
        XhtEnumValue v1 = new(
            Name: "Red", Value: 0,
            Specifiers: Array.Empty<Simgenics.XPact.XHT.AST.Specifier>(),
            Span: new SourceSpan("X.h", 1, 1, 3));
        XhtEnumValue v2 = new(
            Name: "Green", Value: 1,
            Specifiers: Array.Empty<Simgenics.XPact.XHT.AST.Specifier>(),
            Span: new SourceSpan("X.h", 2, 1, 5));
        XhtEnum e = EmitterTestHarness.MakeEnum("EColor", values: new[] { v1, v2 });

        string content = emitter.Render("Public/EColor.h", new XhtTypeBase[] { e });
        Assert.Contains("s_values_EColor[]", content);
        Assert.Contains("/* STAGE-B: Red */", content);
        Assert.Contains("/* STAGE-B: Green */", content);
    }

    [Fact]
    public void DeriveGenSourceFileName_StripsExtensionAndDirectories()
    {
        Assert.Equal("XValve.gen.cpp", SourceEmitter.DeriveGenSourceFileName("Public/XValve.h"));
        Assert.Equal("XValve.gen.cpp", SourceEmitter.DeriveGenSourceFileName(@"Public\XValve.hpp"));
    }
}
