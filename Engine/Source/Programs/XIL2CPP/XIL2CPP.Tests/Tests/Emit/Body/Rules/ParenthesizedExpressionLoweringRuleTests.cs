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
/// Tests for <see cref="ParenthesizedExpressionLoweringRule"/>: a C#
/// parenthesized expression lowers to the identical C++ parenthesized form,
/// recursing the inner expression.
/// </summary>
public sealed class ParenthesizedExpressionLoweringRuleTests
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
        emitter.Expressions.EmitExpression(ParseParen(expression));
        return writer.Build();
    }

    private static ParenthesizedExpressionSyntax ParseParen(string expression)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText($"class C {{ object M() => {expression}; }}");
        return tree.GetRoot().DescendantNodes().OfType<ParenthesizedExpressionSyntax>().First();
    }

    [Fact]
    public void ParenAroundLiteral_PreservesParens()
        => Assert.Equal("(42)", Lower("(42)"));

    [Fact]
    public void ParenAroundBinary_WrapsBinarysOwnWrap()
        => Assert.Equal("((1 + 2))", Lower("(1 + 2)"));

    [Fact]
    public void NestedParens_PreserveEachLevel()
        => Assert.Equal("(((7)))", Lower("(((7)))"));

    [Fact]
    public void CanHandle_AcceptsParenthesized_RejectsBinary()
    {
        var rule = new ParenthesizedExpressionLoweringRule();
        Assert.True(rule.CanHandle(ParseParen("(1)")));

        SyntaxTree tree = CSharpSyntaxTree.ParseText("class C { int M() => 1 + 2; }");
        BinaryExpressionSyntax binary = tree.GetRoot().DescendantNodes()
            .OfType<BinaryExpressionSyntax>().First();
        Assert.False(rule.CanHandle(binary));
    }

    [Fact]
    public void Name_IsStableAndOrdinal()
        => Assert.Equal("Expr.Parenthesized", new ParenthesizedExpressionLoweringRule().Name);
}
