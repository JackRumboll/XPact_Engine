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
/// Tests for <see cref="ConditionalExpressionLoweringRule"/>: a C# ternary
/// lowers to the identical C++ ternary
/// (<c>(&lt;c&gt; ? &lt;a&gt; : &lt;b&gt;)</c>), recursing all three operands and
/// wrapping the whole expression in defensive parentheses.
/// </summary>
public sealed class ConditionalExpressionLoweringRuleTests
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
        emitter.Expressions.EmitExpression(ParseConditional(expression));
        return writer.Build();
    }

    private static ConditionalExpressionSyntax ParseConditional(string expression)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText($"class C {{ object M() => {expression}; }}");
        return tree.GetRoot().DescendantNodes().OfType<ConditionalExpressionSyntax>().First();
    }

    [Fact]
    public void SimpleTernary_LowersToCppTernary()
        => Assert.Equal("(true ? 1 : 2)", Lower("true ? 1 : 2"));

    [Fact]
    public void ConditionWithComparison_RecursesCondition()
        => Assert.Equal("((1 < 2) ? 10 : 20)", Lower("1 < 2 ? 10 : 20"));

    [Fact]
    public void NestedTernaryInBranch_RecursesBranches()
        => Assert.Equal("(true ? 1 : (false ? 2 : 3))", Lower("true ? 1 : false ? 2 : 3"));

    [Fact]
    public void CanHandle_AcceptsConditional_RejectsBinary()
    {
        var rule = new ConditionalExpressionLoweringRule();
        Assert.True(rule.CanHandle(ParseConditional("true ? 1 : 2")));

        SyntaxTree tree = CSharpSyntaxTree.ParseText("class C { int M() => 1 + 2; }");
        BinaryExpressionSyntax binary = tree.GetRoot().DescendantNodes()
            .OfType<BinaryExpressionSyntax>().First();
        Assert.False(rule.CanHandle(binary));
    }

    [Fact]
    public void Name_IsStableAndOrdinal()
        => Assert.Equal("Expr.Conditional", new ConditionalExpressionLoweringRule().Name);
}
