// Copyright Simgenics. All Rights Reserved.

using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit.Body.Rules;

/// <summary>
/// Tests for <see cref="NullConditionalLoweringRule"/>: a simple
/// null-conditional member access <c>a?.b</c> lowers to
/// <c>(&lt;a&gt; != nullptr ? &lt;a&gt;-&gt;b : nullptr)</c>; a chained / invoked
/// continuation emits a DEFERRED comment.
/// </summary>
public sealed class NullConditionalLoweringRuleTests
{
    private static (EmitContext, ConditionalAccessExpressionSyntax) Access(string expr)
        => RuleTestHelpers.ContextAndNode<ConditionalAccessExpressionSyntax>(
            "class N { public int b; public N c; public int M2() => 0; } "
            + "class C { object M(N a) { var x = " + expr + "; return x; } }");

    [Fact]
    public void CanHandle_OnlyConditionalAccess()
    {
        NullConditionalLoweringRule rule = new();
        (_, ConditionalAccessExpressionSyntax access) = Access("a?.b");
        Assert.True(rule.CanHandle(access));
        Assert.False(rule.CanHandle(access.Expression));
    }

    [Fact]
    public void MemberAccess_EmitsTernaryArrowMember()
    {
        NullConditionalLoweringRule rule = new();
        (EmitContext ctx, ConditionalAccessExpressionSyntax access) = Access("a?.b");

        string cpp = RuleTestHelpers.EmitWith(rule, ctx, access);

        Assert.StartsWith("(", cpp);
        Assert.Contains(" != nullptr ? ", cpp);
        Assert.Contains("->b : nullptr)", cpp);
        Assert.DoesNotContain("DEFERRED", cpp);
    }

    [Fact]
    public void InvokedContinuation_EmitsDeferred()
    {
        NullConditionalLoweringRule rule = new();
        (EmitContext ctx, ConditionalAccessExpressionSyntax access) = Access("a?.M2()");

        string cpp = RuleTestHelpers.EmitWith(rule, ctx, access);

        Assert.Contains("DEFERRED(6.e):", cpp);
        Assert.Contains("continuation", cpp);
        Assert.DoesNotContain("->", cpp);
    }
}
