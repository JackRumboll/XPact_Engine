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
        XhtClass valve = EmitterTestHarness.MakeClass("XValve");

        string content = emitter.Render("Public/XValve.h", new[] { valve });

        Assert.Contains("XCONSTINIT static const XClassDescriptor Z_ConstInit_XClass_XGameFramework_XValve", content);
        Assert.Contains("Z_Construct_XClass_XGameFramework_XValve", content);
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
    public void ClassWithProperties_EmitsRealDescriptorRecord()
    {
        // Per C5 audit: the descriptor table emits real XPropertyDescriptor
        // records (Name + TypeName strings) -- not stub comment lines.
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        SourceEmitter emitter = new(ctx);

        XhtProperty p1 = EmitterTestHarness.MakeProperty("OpenFraction", "float");
        XhtProperty p2 = EmitterTestHarness.MakeProperty("Inlet", "XActor*");
        XhtClass valve = EmitterTestHarness.MakeClass("XValve", properties: new[] { p1, p2 });

        string content = emitter.Render("Public/XValve.h", new[] { valve });
        // Real C identifier names + C-string literals in the descriptor.
        Assert.Contains("static const XPropertyDescriptor s_props_XValve[]", content);
        Assert.Contains("\"OpenFraction\"", content);
        Assert.Contains("\"float\"", content);
        Assert.Contains("\"Inlet\"", content);
        Assert.Contains("\"XActor*\"", content);
    }

    [Fact]
    public void ClassWithFunctions_EmitsRealFunctionDescriptors()
    {
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        SourceEmitter emitter = new(ctx);
        XhtFunction f1 = EmitterTestHarness.MakeFunction("Open", "void");
        XhtFunction f2 = EmitterTestHarness.MakeFunction("Close", "bool");
        XhtClass valve = EmitterTestHarness.MakeClass("XValve", functions: new[] { f1, f2 });

        string content = emitter.Render("Public/XValve.h", new[] { valve });
        Assert.Contains("static const XFunctionDescriptor s_funcs_XValve[]", content);
        Assert.Contains("\"Open\"", content);
        Assert.Contains("\"Close\"", content);
        Assert.Contains("\"void\"", content);
        Assert.Contains("\"bool\"", content);
    }

    [Fact]
    public void Determinism_TwoRunsOverSameInput_ProduceByteIdenticalOutput()
    {
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        SourceEmitter emitter = new(ctx);
        XhtClass valve = EmitterTestHarness.MakeClass("XValve");

        string a = emitter.Render("Public/XValve.h", new[] { valve });
        string b = emitter.Render("Public/XValve.h", new[] { valve });
        Assert.Equal(a, b);
    }

    [Fact]
    public void EmitForHeader_WritesUnderOutputDirectory_WithLfOnlyEndings()
    {
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        SourceEmitter emitter = new(ctx);
        XhtClass valve = EmitterTestHarness.MakeClass("XValve");

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
        // Real XEnumValueDescriptor initializers per C5 audit.
        Assert.Contains("\"Red\"", content);
        Assert.Contains("\"Green\"", content);
        Assert.Contains("static_cast<int64_t>(0LL)", content);
        Assert.Contains("static_cast<int64_t>(1LL)", content);
    }

    [Fact]
    public void DeriveGenSourceFileName_StripsExtensionAndDirectories()
    {
        Assert.Equal("XValve.gen.cpp", SourceEmitter.DeriveGenSourceFileName("Public/XValve.h"));
        Assert.Equal("XValve.gen.cpp", SourceEmitter.DeriveGenSourceFileName(@"Public\XValve.hpp"));
    }

    [Fact]
    public void EmittedGenCpp_PinsGCRootABI_ViaStaticAssert()
    {
        // Round-2 audit C1: the emitted .gen.cpp must static_assert that
        // the manifest's GCRootABI matches the runtime's XPACT_GC_ROOT_ABI_TAG.
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        SourceEmitter emitter = new(ctx);
        XhtClass valve = EmitterTestHarness.MakeClass("XValve");

        string content = emitter.Render("Public/XValve.h", new[] { valve });

        Assert.Contains("XPACT_GC_ROOT_ABI_TAG", content);
        Assert.Contains("\"Span-based v1\"", content);
        Assert.Contains("GC root ABI mismatch between manifest and runtime", content);
    }

    [Fact]
    public void EmittedGenCpp_PinsExceptionABI_ViaStaticAssert()
    {
        // Round-2 audit C1: parallel to GCRootABI for ExceptionABI.
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        SourceEmitter emitter = new(ctx);
        XhtClass valve = EmitterTestHarness.MakeClass("XValve");

        string content = emitter.Render("Public/XValve.h", new[] { valve });

        Assert.Contains("XPACT_EXCEPTION_ABI_TAG", content);
        Assert.Contains("\"Tier1-Shim/Tier2-Direct\"", content);
        Assert.Contains("Exception ABI mismatch between manifest and runtime", content);
    }

    [Fact]
    public void EmittedGenCpp_PinsManglingScheme_ViaStaticAssert()
    {
        // Round-2 audit C1: parallel to GCRootABI for the ManglingScheme.
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        SourceEmitter emitter = new(ctx);
        XhtClass valve = EmitterTestHarness.MakeClass("XValve");

        string content = emitter.Render("Public/XValve.h", new[] { valve });

        Assert.Contains("XPACT_MANGLING_SCHEME_TAG", content);
        Assert.Contains("\"Itanium-LengthPrefixed-v1\"", content);
        Assert.Contains("Mangling scheme mismatch between manifest and runtime", content);
    }

    [Fact]
    public void EmittedGenCpp_PinsPropertyAccessorSurface_ViaStaticAssert()
    {
        // Round-2 audit M-XIL2CPP-Accessor: the emitted .gen.cpp must
        // static_assert XPACT_PROPERTY_HAS_ACCESSORS == 1 so a runtime
        // rebuilt without the new XPropertyDescriptor layout fails the
        // compile.
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        SourceEmitter emitter = new(ctx);
        XhtClass valve = EmitterTestHarness.MakeClass("XValve");

        string content = emitter.Render("Public/XValve.h", new[] { valve });

        Assert.Contains("XPACT_PROPERTY_HAS_ACCESSORS == 1", content);
    }

    [Fact]
    public void PropertyDescriptor_EmitsGetterAndSetterNullptrSlots()
    {
        // Round-2 audit M-XIL2CPP-Accessor: every XPropertyDescriptor
        // initializer carries the new Getter / Setter slots (nullptr
        // in Phase 1; XIL2CPP Phase 2 wires up thunks).
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        SourceEmitter emitter = new(ctx);

        XhtProperty p = EmitterTestHarness.MakeProperty("Health", "int32");
        XhtClass valve = EmitterTestHarness.MakeClass("XValve", properties: new[] { p });

        string content = emitter.Render("Public/XValve.h", new[] { valve });

        Assert.Contains("/* .Getter      = */ nullptr", content);
        Assert.Contains("/* .Setter      = */ nullptr", content);
    }

    // --- XCore-4b Phase 4b.7 / Contract Rev 13.8 Stage B addendum pins ---

    [Fact]
    public void EmittedGenCpp_PinsEveryXCore4bLayoutTag_ViaStaticAssert()
    {
        // XCore-4b Rev 4 §11.6 + §9.4: every XHT-emitted .gen.cpp must
        // carry a CompileTimeStrEq static_assert for each
        // XPACT_*_LAYOUT_TAG so a patch DLL compiled against a
        // different ABI fails to link with a clean compile-time error.
        // The full set lives in AbiLayoutPins.LayoutTags; verify each
        // macro name appears in the emit.
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        SourceEmitter emitter = new(ctx);
        XhtClass valve = EmitterTestHarness.MakeClass("XValve");

        string content = emitter.Render("Public/XValve.h", new[] { valve });

        foreach ((string macro, string tagContent) in AbiLayoutPins.LayoutTags)
        {
            Assert.Contains(macro, content);
            // The TagContent must appear verbatim as a C-string literal
            // (with internal characters preserved per EncodeCStringLiteral).
            // The shortest discriminator we can assert on cheaply is the
            // type-version prefix at the head of every tag string.
            string headDiscriminator = tagContent[..System.Math.Min(40, tagContent.Length)];
            Assert.Contains(headDiscriminator, content);
        }
        // The guard ensures legacy .gen.cpp TUs without the runtime
        // macros still compile cleanly.
        Assert.Contains("#ifdef XPACT_FNAME_LAYOUT_TAG", content);
        Assert.Contains("#endif // XPACT_FNAME_LAYOUT_TAG", content);
    }

    [Fact]
    public void EmittedGenCpp_PinsEveryXCore4bTypeSize_ViaStaticAssert()
    {
        // XCore-4b Rev 4 §11.2 / §11.3 + §9.4: every XHT-emitted .gen.cpp
        // must carry a sizeof(...) static_assert for each frozen
        // reflection-runtime type so a runtime header edit that changes
        // a sizeof without a contract bump fails the compile at every
        // consumer TU.
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        SourceEmitter emitter = new(ctx);
        XhtClass valve = EmitterTestHarness.MakeClass("XValve");

        string content = emitter.Render("Public/XValve.h", new[] { valve });

        foreach ((string type, int bytes) in AbiLayoutPins.TypeSizes)
        {
            // Match the exact emit shape: sizeof(::XCore::Reflect::TypeName) == N
            string expectedFragment = "sizeof(::XCore::Reflect::"
                + type
                + ") == "
                + bytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Contains(expectedFragment, content);
        }
        // The guard makes the block opt-in based on the runtime macro
        // sentinel + the reflection-header presence; verify the guard
        // shape is preserved.
        Assert.Contains("#if defined(XPACT_FCLASS_LAYOUT_TAG) && __has_include(\"Reflection/FClass.h\")", content);
        Assert.Contains("#  include \"Reflection/FName.h\"", content);
        Assert.Contains("#  include \"Reflection/FClass.h\"", content);
    }

    [Fact]
    public void EmittedGenCpp_PinsAreEnumeratedInDeclaredOrder()
    {
        // Determinism: the pin block emits LayoutTags in their declared
        // order, then the TypeSizes block in its declared order. This
        // is part of the byte-identical-output contract per Section 14.
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);
        SourceEmitter emitter = new(ctx);
        XhtClass valve = EmitterTestHarness.MakeClass("XValve");

        string content = emitter.Render("Public/XValve.h", new[] { valve });

        // Verify FName tag appears before FProperty tag (both layout
        // and sizeof entries) in the emit -- catches a regression where
        // a future refactor sorts or reorders the pin list.
        int fnameIdx = content.IndexOf("XPACT_FNAME_LAYOUT_TAG", StringComparison.Ordinal);
        int fpropertyIdx = content.IndexOf("XPACT_FPROPERTY_LAYOUT_TAG", StringComparison.Ordinal);
        Assert.True(fnameIdx > 0 && fpropertyIdx > 0 && fnameIdx < fpropertyIdx,
            "XPACT_FNAME_LAYOUT_TAG must precede XPACT_FPROPERTY_LAYOUT_TAG in the emit (declared-order invariant).");

        int fnameSizeIdx = content.IndexOf("sizeof(::XCore::Reflect::FName)", StringComparison.Ordinal);
        int fclassSizeIdx = content.IndexOf("sizeof(::XCore::Reflect::FClass)", StringComparison.Ordinal);
        Assert.True(fnameSizeIdx > 0 && fclassSizeIdx > 0 && fnameSizeIdx < fclassSizeIdx,
            "sizeof(FName) pin must precede sizeof(FClass) pin (declared-order invariant).");
    }
}
