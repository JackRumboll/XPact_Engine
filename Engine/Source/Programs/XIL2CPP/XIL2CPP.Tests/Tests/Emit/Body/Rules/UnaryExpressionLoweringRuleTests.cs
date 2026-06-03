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
/// Tests for <see cref="UnaryExpressionLoweringRule"/>: prefix
/// (<c>++ -- ! - + ~</c>) and postfix (<c>++ --</c>) unary forms lower to the
/// identical C++ unary operator, the operand recurses, and the whole expression
/// is wrapped in defensive parentheses.
/// </summary>
public sealed class UnaryExpressionLoweringRuleTests
{
    private static string Lower(string statementBody)
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

        SyntaxNode node = ParseUnary(statementBody);
        emitter.Expressions.EmitExpression(node);
        return writer.Build();
    }

    private static SyntaxNode ParseUnary(string expression)
    {
        // Parse inside a method body so pre/post ++/-- on a local is legal.
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            $"class C {{ void M() {{ int x = 0; var r = {expression}; }} }}");
        SyntaxNode root = tree.GetRoot();
        SyntaxNode? prefix = root.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>().FirstOrDefault();
        if (prefix is not null)
        {
            return prefix;
        }
        return root.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>().First();
    }

    [Theory]
    [InlineData("!true", "(!true)")]
    [InlineData("-1", "(-1)")]
    [InlineData("+1", "(+1)")]
    [InlineData("~1", "(~1)")]
    public void Prefix_ValueOperators_LowerToSameOperator(string expr, string expected)
        => Assert.Equal(expected, Lower(expr));

    [Theory]
    [InlineData("++x", "++")] // x recurses to a TODO marker since identifiers have no rule.
    [InlineData("--x", "--")]
    public void Prefix_IncrementDecrement_LowerToSameOperator(string expr, string op)
    {
        // The operand 'x' has no lowering rule, so it falls to a TODO comment;
        // the prefix operator is emitted before it, all inside the wrap.
        string result = Lower(expr);
        Assert.StartsWith("(" + op, result);
        Assert.Contains("TODO(6.e):", result);
        Assert.EndsWith(")", result.TrimEnd('\n'));
    }

    [Theory]
    [InlineData("x++", "++")]
    [InlineData("x--", "--")]
    public void Postfix_IncrementDecrement_LowerToSameOperator(string expr, string op)
    {
        // The operand 'x' has no lowering rule (-> TODO comment); the postfix
        // operator follows it and the closing wrap paren follows the operator.
        string result = Lower(expr);
        Assert.StartsWith("(", result);
        Assert.Contains("TODO(6.e):", result);
        Assert.EndsWith(op + ")", result.TrimEnd('\n'));
    }

    [Fact]
    public void NegationOfLiteral_IsFullyParenthesized()
        => Assert.Equal("(-42)", Lower("-42"));

    [Fact]
    public void DoubleNegation_KeepsTokensSeparate()
    {
        // -(-1): the source parens lower via the parenthesized rule and the
        // inner unary wraps itself, so the two '-' never fuse into a single
        // '--' token in the C++ lexer.
        string result = Lower("-(-1)");
        Assert.Equal("(-((-1)))", result);
    }

    [Fact]
    public void NotOfComparison_BindsTightlyAroundOperand()
    {
        // !(1 == 2): the source parens lower via the parenthesized rule, which
        // wraps the binary's own wrap.
        Assert.Equal("(!((1 == 2)))", Lower("!(1 == 2)"));
    }

    [Fact]
    public void CanHandle_AcceptsPrefixAndPostfix_RejectsBinary()
    {
        var rule = new UnaryExpressionLoweringRule();
        Assert.True(rule.CanHandle(ParseUnary("-1")));
        Assert.True(rule.CanHandle(ParseUnary("x++")));

        SyntaxTree tree = CSharpSyntaxTree.ParseText("class C { int M() => 1 + 2; }");
        BinaryExpressionSyntax binary = tree.GetRoot().DescendantNodes()
            .OfType<BinaryExpressionSyntax>().First();
        Assert.False(rule.CanHandle(binary));
    }

    [Fact]
    public void Name_IsStableAndOrdinal()
        => Assert.Equal("Expr.Unary", new UnaryExpressionLoweringRule().Name);
}
