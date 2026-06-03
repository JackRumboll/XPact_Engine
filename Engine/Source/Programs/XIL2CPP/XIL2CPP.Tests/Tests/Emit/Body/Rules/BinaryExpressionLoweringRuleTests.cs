// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit.Body.Rules;

/// <summary>
/// Tests for <see cref="BinaryExpressionLoweringRule"/>: each operator group
/// (arithmetic / comparison / logical / bitwise) lowers to the identical C++
/// infix operator, both operands recurse, and the whole expression is wrapped
/// in defensive parentheses.
/// </summary>
public sealed class BinaryExpressionLoweringRuleTests
{
    private static string Lower(string expression)
    {
        BodyLoweringRuleRegistry registry = new(new IBodyLoweringRule[]
        {
            new BinaryExpressionLoweringRule(),
            new UnaryExpressionLoweringRule(),
            new LiteralExpressionLoweringRule(),
            new ParenthesizedExpressionLoweringRule(),
            new ConditionalExpressionLoweringRule(),
        });
        EmitContext ctx = EmitTestHelpers.BuildEmitContext();
        CppWriter writer = new();
        StatementEmitter emitter = new(ctx, writer, registry);

        BinaryExpressionSyntax node = ParseTopBinary(expression);
        emitter.Expressions.EmitExpression(node);
        return writer.Build();
    }

    private static BinaryExpressionSyntax ParseTopBinary(string expression)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText($"class C {{ object M() => {expression}; }}");
        return tree.GetRoot().DescendantNodes().OfType<BinaryExpressionSyntax>().First();
    }

    [Theory]
    [InlineData("1 + 2", "(1 + 2)")]
    [InlineData("5 - 3", "(5 - 3)")]
    [InlineData("4 * 6", "(4 * 6)")]
    [InlineData("8 / 2", "(8 / 2)")]
    [InlineData("7 % 3", "(7 % 3)")]
    public void Arithmetic_LowersToSameOperator(string expr, string expected)
        => Assert.Equal(expected, Lower(expr));

    [Theory]
    [InlineData("1 == 2", "(1 == 2)")]
    [InlineData("1 != 2", "(1 != 2)")]
    [InlineData("1 < 2", "(1 < 2)")]
    [InlineData("1 <= 2", "(1 <= 2)")]
    [InlineData("1 > 2", "(1 > 2)")]
    [InlineData("1 >= 2", "(1 >= 2)")]
    public void Comparison_LowersToSameOperator(string expr, string expected)
        => Assert.Equal(expected, Lower(expr));

    [Theory]
    [InlineData("true && false", "(true && false)")]
    [InlineData("true || false", "(true || false)")]
    public void Logical_LowersToSameOperator(string expr, string expected)
        => Assert.Equal(expected, Lower(expr));

    [Theory]
    [InlineData("1 & 2", "(1 & 2)")]
    [InlineData("1 | 2", "(1 | 2)")]
    [InlineData("1 ^ 2", "(1 ^ 2)")]
    [InlineData("1 << 2", "(1 << 2)")]
    [InlineData("8 >> 2", "(8 >> 2)")]
    public void Bitwise_LowersToSameOperator(string expr, string expected)
        => Assert.Equal(expected, Lower(expr));

    [Fact]
    public void Nested_WrapsEachSubExpressionInParens()
    {
        // (1 + 2) * 3 -> the source parens become a parenthesized-rule wrap
        // around the inner binary's own wrap, all inside the outer multiply's
        // wrap: (((1 + 2)) * 3).
        Assert.Equal("(((1 + 2)) * 3)", Lower("(1 + 2) * 3"));
    }

    [Fact]
    public void NestedWithoutSourceParens_StillFullyParenthesized()
    {
        // 1 + 2 * 3 binds as 1 + (2 * 3) in C#; each binary wraps itself so the
        // emitted C++ pins that association explicitly.
        Assert.Equal("(1 + (2 * 3))", Lower("1 + 2 * 3"));
    }

    [Fact]
    public void CanHandle_RejectsCoalesceOperator()
    {
        // ?? is not an arithmetic/comparison/logical/bitwise operator; it is not
        // handled here so it falls to the foundation TODO marker.
        BinaryExpressionSyntax node = ParseTopBinary("null ?? \"x\"");
        Assert.False(new BinaryExpressionLoweringRule().CanHandle(node));
    }

    [Fact]
    public void Name_IsStableAndOrdinal()
        => Assert.Equal("Expr.Binary", new BinaryExpressionLoweringRule().Name);
}
