// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit;

/// <summary>
/// Tests for the Pass-6 per-file driver (<see cref="Pass6Driver"/> +
/// <see cref="FileEmitter"/>) per /Documents/XIL2CPP.html Rev 4 Section 3.2
/// (Pass 6) + Section 5.1 (the .cs.h / .cs.cpp file shape): one
/// <see cref="EmitResult"/> per source file; the header carries
/// <c>#pragma once</c>; the source carries the <see cref="AbiPins"/> pin block
/// (the exact static-assert count) + the contract-frozen FClass layout-tag
/// content; and the whole emit is byte-deterministic.
/// </summary>
public sealed class Pass6DriverTests
{
    /// <summary>
    /// In-source stand-ins for the canonical XPact reflection attributes (the
    /// curated XPact.CSharp.BCL refs are absent in the emit tests; the emitter
    /// recognises the attribute by metadata name + namespace).
    /// </summary>
    private const string AttributeStubs =
        "namespace XPact.CoreXObject { "
        + "public sealed class XClassAttribute : System.Attribute { } "
        + "public sealed class XPropertyAttribute : System.Attribute { } }";

    private const string XValveSource =
        "namespace TestModule { [XPact.CoreXObject.XClassAttribute] public class XValve { "
        + "public int Health { get; set; } "
        + "public int Compute(int x) { return x; } } }";

    private static IReadOnlyList<EmitResult> Emit(params string[] sources)
    {
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(sources);
        return new Pass6Driver().Run(ctx);
    }

    [Fact]
    public void Run_ProducesOneEmitResultPerSourceFile()
    {
        // Two source files -> two EmitResults (one per parsed file).
        IReadOnlyList<EmitResult> results = Emit(
            AttributeStubs,
            XValveSource);

        Assert.Equal(2, results.Count);
        // Each EmitResult is keyed by its originating .cs path.
        Assert.All(results, r => Assert.EndsWith(".cs", r.SourceRelativePath));
        Assert.Equal(results.Select(r => r.SourceRelativePath).Distinct().Count(), results.Count);
    }

    [Fact]
    public void Header_HasPragmaOnce_AndCopyrightBanner()
    {
        IReadOnlyList<EmitResult> results = Emit(AttributeStubs, XValveSource);

        foreach (EmitResult r in results)
        {
            Assert.Contains("#pragma once", r.HeaderContent);
            Assert.StartsWith(FileEmitter.CopyrightBanner, r.HeaderContent);
        }
    }

    [Fact]
    public void Header_DeclaresTheXClass_WithStaticClassAndBackingField()
    {
        // The XValve source is the SECOND file; find its header.
        EmitResult valve = Emit(AttributeStubs, XValveSource)[1];

        // The C++ class declaration (XObject-derived) + StaticClass() decl + the
        // synthesized auto-property backing field the accessor bodies read/write.
        Assert.Contains("class XValve : public ::XCore::Reflect::XObject", valve.HeaderContent);
        Assert.Contains("static const ::XCore::Reflect::FClass* StaticClass();", valve.HeaderContent);
        Assert.Contains(PropertyEmitter.BackingFieldPrefix + "Health;", valve.HeaderContent);
        // In-class FClass-layout ABI pin (Section 5.1).
        Assert.Contains("XPACT_FCLASS_LAYOUT_TAG", valve.HeaderContent);
    }

    [Fact]
    public void Source_ContainsTheAbiPinBlock_WithExactStaticAssertCount()
    {
        EmitResult valve = Emit(AttributeStubs, XValveSource)[1];

        // The pin block: 24 layout-tag pins + 3 envelope tags + 22 sizeof pins.
        int layoutAndEnvelope = CountOccurrences(
            valve.SourceContent, "XPactDetail::CompileTimeStrEq(XPACT_");
        Assert.Equal(AbiPins.LayoutTagPinCount + 3, layoutAndEnvelope);

        int sizeofAsserts = CountOccurrences(valve.SourceContent, "sizeof(::XCore::Reflect::");
        Assert.Equal(AbiPins.SizeofPinCount, sizeofAsserts);

        // The pin-family guards are present.
        Assert.Contains("#ifdef XPACT_FNAME_LAYOUT_TAG", valve.SourceContent);
        Assert.Contains(
            "#if defined(XPACT_FCLASS_LAYOUT_TAG) && __has_include(\"Reflection/FClass.h\")",
            valve.SourceContent);
    }

    [Fact]
    public void Source_FClassLayoutTagContent_MatchesContractRev139()
    {
        EmitResult valve = Emit(AttributeStubs, XValveSource)[1];

        (string _, string fclassContent) = AbiPins.LayoutTags.Single(
            t => t.TagMacro == "XPACT_FCLASS_LAYOUT_TAG");

        // The emitted source pins the FClass layout-tag with the exact
        // contract-frozen Rev 13.9 content string.
        Assert.Contains(
            "XPactDetail::CompileTimeStrEq(XPACT_FCLASS_LAYOUT_TAG, "
            + CppWriter.EncodeCStringLiteral(fclassContent) + ")",
            valve.SourceContent);
        Assert.StartsWith("FClass-v6 (Contract Rev 13.9 extension via XCoreXObject Rev 3): 240 bytes", fclassContent);
    }

    [Fact]
    public void Source_EmitsTheXClassTypeLevelBodies()
    {
        EmitResult valve = Emit(AttributeStubs, XValveSource)[1];

        // The .cs.cpp includes the matching .cs.h + the runtime headers.
        Assert.Contains("#include \"Source1.cs.h\"", valve.SourceContent);
        Assert.Contains("#include \"XReflectionRuntime.h\"", valve.SourceContent);

        // The XClass body set (singleton getter + StaticClass + ClassConstructor
        // + lifecycle table + FClass instance) is emitted.
        Assert.Contains("Z_Construct_FClass_TestModule_XValve() noexcept {", valve.SourceContent);
        Assert.Contains("XValve::StaticClass() {", valve.SourceContent);
        Assert.Contains("auto* obj = new (memory) XValve();", valve.SourceContent);
        Assert.Contains(
            "constinit const ::XCore::Reflect::FXObjectLifecycleTable XValve_LifecycleTable = {",
            valve.SourceContent);
        Assert.Contains("constinit const ::XCore::Reflect::FClass XValve_Class = {", valve.SourceContent);
    }

    [Fact]
    public void Run_IsDeterministic_ByteIdentical_AcrossTwoRuns()
    {
        IReadOnlyList<EmitResult> first = Emit(AttributeStubs, XValveSource);
        IReadOnlyList<EmitResult> second = Emit(AttributeStubs, XValveSource);

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].SourceRelativePath, second[i].SourceRelativePath);
            Assert.Equal(first[i].HeaderContent, second[i].HeaderContent);
            Assert.Equal(first[i].SourceContent, second[i].SourceContent);
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
