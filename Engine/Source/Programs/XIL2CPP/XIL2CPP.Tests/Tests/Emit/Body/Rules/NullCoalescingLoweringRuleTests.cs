// Copyright Simgenics. All Rights Reserved.

using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit.Body.Rules;

/// <summary>
/// Tests for <see cref="NullCoalescingLoweringRule"/>: <c>a ?? b</c> lowers to
/// the C++ ternary <c>(&lt;a&gt; != nullptr ? &lt;a&gt; : &lt;b&gt;)</c>, with a
/// DEFERRED note when the left operand has side effects (the simple ternary
/// double-evaluates it).
/// </summary>
public sealed class NullCoalescingLoweringRuleTests
{
    private const string Wrap =
        "class C { object M(object a, object b) { return @EXPR@; } object F() => null; }";

    private static (EmitContext, BinaryExpressionSyntax) Coalesce(string expr)
        => RuleTestHelpers.ContextAndNode<BinaryExpressionSyntax>(Wrap.Replace("@EXPR@", expr));

    [Fact]
    public void CanHandle_OnlyCoalesceBinary()
    {
        NullCoalescingLoweringRule rule = new();
        (_, BinaryExpressionSyntax coalesce) = Coalesce("a ?? b");
        Assert.True(rule.CanHandle(coalesce));

        (_, BinaryExpressionSyntax add) =
            RuleTestHelpers.ContextAndNode<BinaryExpressionSyntax>(
                "class C { int M(int a, int b) => a + b; }");
        Assert.False(rule.CanHandle(add));
    }

    [Fact]
    public void Coalesce_SideEffectFreeLeft_EmitsTernaryNoDeferred()
    {
        NullCoalescingLoweringRule rule = new();
        (EmitContext ctx, BinaryExpressionSyntax coalesce) = Coalesce("a ?? b");

        string cpp = RuleTestHelpers.EmitWith(rule, ctx, coalesce);

        Assert.StartsWith("(", cpp);
        Assert.Contains(" != nullptr ? ", cpp);
        Assert.Contains(" : ", cpp);
        Assert.EndsWith(")", cpp);
        Assert.DoesNotContain("DEFERRED", cpp);
    }

    [Fact]
    public void Coalesce_SideEffectingLeft_EmitsDeferredNote()
    {
        NullCoalescingLoweringRule rule = new();
        (EmitContext ctx, BinaryExpressionSyntax coalesce) = Coalesce("F() ?? b");

        string cpp = RuleTestHelpers.EmitWith(rule, ctx, coalesce);

        Assert.Contains("DEFERRED(6.e):", cpp);
        Assert.Contains("double-evaluates", cpp);
        Assert.Contains(" != nullptr ? ", cpp);
    }
}
