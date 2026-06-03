// Copyright Simgenics. All Rights Reserved.

using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit;

/// <summary>
/// Tests for <see cref="CppWriter"/>: determinism (identical calls produce a
/// byte-identical <see cref="CppWriter.Build"/>), the four-space indent model,
/// and the static-assert / extern-"C" / comment shapes.
/// </summary>
public sealed class CppWriterTests
{
    [Fact]
    public void Build_TwoIdenticalCallSequences_ProduceByteIdenticalOutput()
    {
        static string Drive()
        {
            CppWriter w = new();
            w.AppendComment("Copyright header line");
            w.AppendLine("#pragma once");
            w.AppendLine();
            w.BeginBlock("namespace Simgenics::XPact::Game");
            w.BeginBlock("class XHealthPickup : public XObject");
            w.AppendLine("int32_t HealthAmount = 25;");
            w.EndBlock(";");
            w.EndBlock();
            return w.Build();
        }

        Assert.Equal(Drive(), Drive());
    }

    [Fact]
    public void Build_IsStableAcrossRepeatedCalls()
    {
        CppWriter w = new();
        w.AppendLine("int x = 0;");
        string first = w.Build();
        string second = w.Build();
        Assert.Equal(first, second);
    }

    [Fact]
    public void AppendLine_UsesLfNewlinesOnly()
    {
        CppWriter w = new();
        w.AppendLine("a");
        w.AppendLine("b");
        Assert.Equal("a\nb\n", w.Build());
        Assert.DoesNotContain("\r", w.Build());
    }

    [Fact]
    public void BeginEndBlock_IndentsBodyByFourSpacesPerLevel()
    {
        CppWriter w = new();
        w.BeginBlock("void Foo()");
        w.AppendLine("return;");
        w.EndBlock();

        Assert.Equal(
            "void Foo() {\n"
            + "    return;\n"
            + "}\n",
            w.Build());
    }

    [Fact]
    public void NestedBlocks_IndentCumulatively()
    {
        CppWriter w = new();
        w.BeginBlock("a");
        w.BeginBlock("b");
        w.AppendLine("c;");
        w.EndBlock();
        w.EndBlock();

        Assert.Equal(
            "a {\n"
            + "    b {\n"
            + "        c;\n"
            + "    }\n"
            + "}\n",
            w.Build());
    }

    [Fact]
    public void IndentUnindent_AdjustDepthDirectly()
    {
        CppWriter w = new();
        w.AppendLine("top;");
        w.Indent();
        w.AppendLine("one;");
        w.Indent();
        w.AppendLine("two;");
        w.Unindent();
        w.AppendLine("one;");
        w.Unindent();
        w.AppendLine("top;");

        Assert.Equal(
            "top;\n"
            + "    one;\n"
            + "        two;\n"
            + "    one;\n"
            + "top;\n",
            w.Build());
    }

    [Fact]
    public void Unindent_FlooredAtZero()
    {
        CppWriter w = new();
        w.Unindent();
        w.Unindent();
        Assert.Equal(0, w.Depth);
        w.AppendLine("x;");
        Assert.Equal("x;\n", w.Build());
    }

    [Fact]
    public void AppendStaticAssert_RendersConditionAndEncodedMessage()
    {
        CppWriter w = new();
        w.AppendStaticAssert("sizeof(Foo) == 8", "Foo must be 8 bytes");
        Assert.Equal("static_assert(sizeof(Foo) == 8, \"Foo must be 8 bytes\");\n", w.Build());
    }

    [Fact]
    public void AppendStaticAssert_EscapesQuotesInMessage()
    {
        CppWriter w = new();
        w.AppendStaticAssert("X", "has \"quotes\" inside");
        Assert.Equal("static_assert(X, \"has \\\"quotes\\\" inside\");\n", w.Build());
    }

    [Fact]
    public void AppendExternC_NoexceptTrue_AppendsNoexcept()
    {
        CppWriter w = new();
        w.AppendExternC("void", "_v1__Foo__Bar", "::Foo* self, float x", noexcept: true);
        Assert.Equal("extern \"C\" void _v1__Foo__Bar(::Foo* self, float x) noexcept;\n", w.Build());
    }

    [Fact]
    public void AppendExternC_NoexceptFalse_OmitsNoexcept()
    {
        CppWriter w = new();
        w.AppendExternC("void", "_v1__Foo__Bar", "", noexcept: false);
        Assert.Equal("extern \"C\" void _v1__Foo__Bar();\n", w.Build());
    }

    [Fact]
    public void AppendComment_PrefixesDoubleSlashSpace()
    {
        CppWriter w = new();
        w.AppendComment("hello");
        Assert.Equal("// hello\n", w.Build());
    }

    [Fact]
    public void AppendComment_RespectsIndent()
    {
        CppWriter w = new();
        w.Indent();
        w.AppendComment("indented");
        Assert.Equal("    // indented\n", w.Build());
    }

    [Fact]
    public void Append_DoesNotInjectIndentationOrNewline()
    {
        CppWriter w = new();
        w.Indent();
        w.Append("a");
        w.Append("b");
        Assert.Equal("ab", w.Build());
    }

    [Fact]
    public void EncodeCStringLiteral_NullEncodesAsNullptr()
    {
        Assert.Equal("nullptr", CppWriter.EncodeCStringLiteral(null));
    }

    [Fact]
    public void EncodeCStringLiteral_EscapesBackslashNewlineTab()
    {
        Assert.Equal("\"a\\\\b\\nc\\td\"", CppWriter.EncodeCStringLiteral("a\\b\nc\td"));
    }
}
