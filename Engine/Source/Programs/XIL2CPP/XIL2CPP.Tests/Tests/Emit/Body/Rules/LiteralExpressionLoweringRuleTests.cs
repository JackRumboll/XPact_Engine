// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit.Body.Rules;

/// <summary>
/// Tests for <see cref="LiteralExpressionLoweringRule"/>: each literal kind
/// (int / uint / long / ulong / float / double / bool / null / char / string)
/// lowers to the correct C++ literal form with the correct suffix, and a string
/// literal lowers to the interned-FString reference with the 16-hex SHA-256
/// symbol suffix.
/// </summary>
public sealed class LiteralExpressionLoweringRuleTests
{
    private static string Render(string literalSource)
    {
        LiteralExpressionSyntax literal = ParseLiteral(literalSource);
        return LiteralExpressionLoweringRule.Render(literal);
    }

    private static LiteralExpressionSyntax ParseLiteral(string literalSource)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText($"class C {{ object M() => {literalSource}; }}");
        return tree.GetRoot().DescendantNodes().OfType<LiteralExpressionSyntax>().First();
    }

    private static string EmitThroughRule(string literalSource)
    {
        BodyLoweringRuleRegistry registry =
            new(new IBodyLoweringRule[] { new LiteralExpressionLoweringRule() });
        EmitContext ctx = EmitTestHelpers.BuildEmitContext();
        CppWriter writer = new();
        StatementEmitter emitter = new(ctx, writer, registry);
        emitter.Expressions.EmitExpression(ParseLiteral(literalSource));
        return writer.Build();
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("42", "42")]
    [InlineData("2147483647", "2147483647")]
    [InlineData("0x1F", "31")]      // hex spelling normalizes to decimal.
    [InlineData("1_000", "1000")]   // digit separators normalize away.
    public void Int_LowersToBareDecimal(string src, string expected)
        => Assert.Equal(expected, Render(src));

    [Theory]
    [InlineData("42u", "42u")]
    [InlineData("4000000000", "4000000000u")] // exceeds int range -> uint default type.
    public void UInt_LowersWithUSuffix(string src, string expected)
        => Assert.Equal(expected, Render(src));

    [Theory]
    [InlineData("42L", "42ll")]
    [InlineData("9999999999", "9999999999ll")] // exceeds uint range -> long default type.
    public void Long_LowersWithLlSuffix(string src, string expected)
        => Assert.Equal(expected, Render(src));

    [Theory]
    [InlineData("42UL", "42ull")]
    [InlineData("18446744073709551615", "18446744073709551615ull")] // ulong.MaxValue.
    public void ULong_LowersWithUllSuffix(string src, string expected)
        => Assert.Equal(expected, Render(src));

    [Theory]
    [InlineData("1.5f", "1.5f")]
    [InlineData("2f", "2.0f")]      // forces a decimal point.
    [InlineData("0.0f", "0.0f")]
    public void Float_LowersWithFSuffixAndDecimalPoint(string src, string expected)
        => Assert.Equal(expected, Render(src));

    [Theory]
    [InlineData("1.5", "1.5")]
    [InlineData("2.0", "2.0")]
    [InlineData("3d", "3.0")]       // 'd' suffix is a double; forces a decimal point.
    public void Double_LowersWithDecimalPointNoSuffix(string src, string expected)
        => Assert.Equal(expected, Render(src));

    [Theory]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    public void Bool_LowersToCppKeyword(string src, string expected)
        => Assert.Equal(expected, Render(src));

    [Fact]
    public void Null_LowersToNullptr()
        => Assert.Equal("nullptr", Render("null"));

    [Theory]
    [InlineData("'a'", "'a'")]
    [InlineData("'Z'", "'Z'")]
    [InlineData("'\\n'", "'\\n'")]
    [InlineData("'\\''", "'\\''")]
    [InlineData("'\\\\'", "'\\\\'")]
    [InlineData("'\\t'", "'\\t'")]
    public void Char_LowersToCppCharLiteral(string src, string expected)
        => Assert.Equal(expected, Render(src));

    [Fact]
    public void Char_NonPrintable_HexEscapes()
    {
        // '' (DEL) is non-printable ASCII -> hex escape.
        Assert.Equal("'\\x7f'", Render("'\\u007f'"));
    }

    [Theory]
    [InlineData("\"hello\"", "2cf24dba5fb0a30e")]
    [InlineData("\"\"", "e3b0c44298fc1c14")]
    [InlineData("\"abc\"", "ba7816bf8f01cfea")]
    public void String_LowersToInternedFStringReference(string src, string expectedHash16)
    {
        string expected = $"::XCore::Container::FString{{&_String_{expectedHash16}}}";
        Assert.Equal(expected, Render(src));
    }

    [Fact]
    public void ComputeStringHash16_IsFirst16LowercaseHexOfSha256Utf8()
    {
        const string value = "hello";
        byte[] full = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        string fullHex = string.Concat(full.Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
        string expected = fullHex.Substring(0, 16);

        Assert.Equal(expected, LiteralExpressionLoweringRule.ComputeStringHash16(value));
        Assert.Equal(16, LiteralExpressionLoweringRule.ComputeStringHash16(value).Length);
    }

    [Fact]
    public void ComputeStringHash16_IsDeterministic()
        => Assert.Equal(
            LiteralExpressionLoweringRule.ComputeStringHash16("repeatable"),
            LiteralExpressionLoweringRule.ComputeStringHash16("repeatable"));

    [Fact]
    public void RenderStringReference_UsesExposedConstants()
    {
        string hash16 = LiteralExpressionLoweringRule.ComputeStringHash16("world");
        Assert.Equal(
            $"{LiteralExpressionLoweringRule.FStringType}{{&{LiteralExpressionLoweringRule.StringSymbolPrefix}{hash16}}}",
            LiteralExpressionLoweringRule.RenderStringReference("world"));
    }

    [Fact]
    public void Emit_ThroughRule_WritesSameTextAsRender()
    {
        // The rule's Emit path appends exactly what Render produces (no
        // trailing newline -- it is an expression fragment).
        Assert.Equal("42", EmitThroughRule("42"));
        Assert.Equal("nullptr", EmitThroughRule("null"));
        Assert.Equal(
            "::XCore::Container::FString{&_String_2cf24dba5fb0a30e}",
            EmitThroughRule("\"hello\""));
    }

    [Fact]
    public void CanHandle_AcceptsLiterals_RejectsOthers()
    {
        var rule = new LiteralExpressionLoweringRule();
        Assert.True(rule.CanHandle(ParseLiteral("1")));

        SyntaxTree tree = CSharpSyntaxTree.ParseText("class C { int M() => 1 + 2; }");
        BinaryExpressionSyntax binary = tree.GetRoot().DescendantNodes()
            .OfType<BinaryExpressionSyntax>().First();
        Assert.False(rule.CanHandle(binary));
    }

    [Fact]
    public void Name_IsStableAndOrdinal()
        => Assert.Equal("Expr.Literal", new LiteralExpressionLoweringRule().Name);
}
