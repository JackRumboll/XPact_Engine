// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit;

/// <summary>
/// Tests for <see cref="AbiPins"/>: the Contract Rev 13.9 pin counts match the
/// exposed constants, the FClass / XObject layout-tag content matches the
/// contract-frozen strings (so the duplicated XIL2CPP copy never drifts from
/// XHT's), and <see cref="AbiPins.EmitPinBlock"/> writes one assert per pin
/// inside the expected guards.
/// </summary>
public sealed class AbiPinsTests
{
    [Fact]
    public void LayoutTags_CountMatchesContractRev139Constant()
    {
        Assert.Equal(AbiPins.LayoutTagPinCount, AbiPins.LayoutTags.Count);
        Assert.Equal(24, AbiPins.LayoutTagPinCount);
    }

    [Fact]
    public void TypeSizes_CountMatchesContractRev139Constant()
    {
        Assert.Equal(AbiPins.SizeofPinCount, AbiPins.TypeSizes.Count);
        Assert.Equal(22, AbiPins.SizeofPinCount);
    }

    [Fact]
    public void TotalPinCount_IsLayoutPlusSizeof()
    {
        Assert.Equal(46, AbiPins.TotalLayoutAndSizeofPinCount);
        Assert.Equal(
            AbiPins.LayoutTags.Count + AbiPins.TypeSizes.Count,
            AbiPins.TotalLayoutAndSizeofPinCount);
    }

    [Fact]
    public void FClassLayoutTag_ContentMatchesContractRev139()
    {
        (string macro, string content) = AbiPins.LayoutTags.Single(t => t.TagMacro == "XPACT_FCLASS_LAYOUT_TAG");
        Assert.Equal("XPACT_FCLASS_LAYOUT_TAG", macro);
        Assert.StartsWith("FClass-v6 (Contract Rev 13.9 extension via XCoreXObject Rev 3): 240 bytes", content);
        Assert.Contains("FClass-absolute offset 232", content);
    }

    [Fact]
    public void FClassSizeof_Is240Bytes()
    {
        (string type, int bytes) = AbiPins.TypeSizes.Single(t => t.TypeName == "FClass");
        Assert.Equal("FClass", type);
        Assert.Equal(240, bytes);
    }

    [Fact]
    public void XObjectSizeof_Is56Bytes()
    {
        Assert.Equal(56, AbiPins.TypeSizes.Single(t => t.TypeName == "XObject").ExpectedBytes);
    }

    [Fact]
    public void FStructAndFScriptStructSizes_MatchRev139Cascade()
    {
        Assert.Equal(120, AbiPins.TypeSizes.Single(t => t.TypeName == "FStruct").ExpectedBytes);
        Assert.Equal(136, AbiPins.TypeSizes.Single(t => t.TypeName == "FScriptStruct").ExpectedBytes);
    }

    [Fact]
    public void XGCRootSpan_PinPresent_32Bytes()
    {
        Assert.Equal(32, AbiPins.TypeSizes.Single(t => t.TypeName == "XGCRootSpan").ExpectedBytes);
        Assert.Contains(AbiPins.LayoutTags, t => t.TagMacro == "XPACT_XGC_ROOTSPAN_LAYOUT_TAG");
    }

    [Fact]
    public void EmitPinBlock_EmitsOneStaticAssertPerLayoutTagAndSizeofPin()
    {
        EmitContext ctx = EmitTestHelpers.BuildEmitContext();
        CppWriter w = new();
        AbiPins.EmitPinBlock(w, ctx);
        string output = w.Build();

        // Every layout-tag macro is asserted via CompileTimeStrEq.
        foreach ((string macro, _) in AbiPins.LayoutTags)
        {
            Assert.Contains($"XPactDetail::CompileTimeStrEq({macro},", output);
        }

        // Every sizeof pin is asserted.
        foreach ((string type, int bytes) in AbiPins.TypeSizes)
        {
            Assert.Contains($"sizeof(::XCore::Reflect::{type}) == {bytes}", output);
        }

        // The envelope tags come from the context's ABI-tag content strings.
        Assert.Contains("XPACT_GC_ROOT_ABI_TAG, \"Span-based v1\"", output);
        Assert.Contains("XPACT_EXCEPTION_ABI_TAG, \"Tier1-Shim/Tier2-Direct\"", output);
        Assert.Contains("XPACT_MANGLING_SCHEME_TAG, \"Itanium-LengthPrefixed-v1\"", output);
    }

    [Fact]
    public void EmitPinBlock_WrapsPinFamiliesInTheExpectedGuards()
    {
        EmitContext ctx = EmitTestHelpers.BuildEmitContext();
        CppWriter w = new();
        AbiPins.EmitPinBlock(w, ctx);
        string output = w.Build();

        Assert.Contains("#ifdef XPACT_FNAME_LAYOUT_TAG", output);
        Assert.Contains("#endif // XPACT_FNAME_LAYOUT_TAG", output);
        Assert.Contains("#if defined(XPACT_FCLASS_LAYOUT_TAG) && __has_include(\"Reflection/FClass.h\")", output);
        Assert.Contains("#endif // XPACT_FCLASS_LAYOUT_TAG && __has_include", output);
    }

    [Fact]
    public void EmitPinBlock_LayoutAssertCount_EqualsLayoutTagCount()
    {
        EmitContext ctx = EmitTestHelpers.BuildEmitContext();
        CppWriter w = new();
        AbiPins.EmitPinBlock(w, ctx);
        string output = w.Build();

        int layoutAsserts = CountOccurrences(output, "XPactDetail::CompileTimeStrEq(XPACT_");
        // 24 layout-tag pins + 3 envelope tags (GC root / exception / mangling).
        Assert.Equal(AbiPins.LayoutTagPinCount + 3, layoutAsserts);

        int sizeofAsserts = CountOccurrences(output, "sizeof(::XCore::Reflect::");
        Assert.Equal(AbiPins.SizeofPinCount, sizeofAsserts);
    }

    [Fact]
    public void EmitPinBlock_IsDeterministic()
    {
        EmitContext ctx = EmitTestHelpers.BuildEmitContext();

        CppWriter a = new();
        AbiPins.EmitPinBlock(a, ctx);
        CppWriter b = new();
        AbiPins.EmitPinBlock(b, ctx);

        Assert.Equal(a.Build(), b.Build());
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
