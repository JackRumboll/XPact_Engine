// Copyright Simgenics. All Rights Reserved.

using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit.Body.Rules;

/// <summary>
/// Tests for <see cref="CastLoweringRule"/>: an explicit C# cast
/// (<c>(T)x</c>) lowers to <c>static_cast&lt;Target&gt;(&lt;e&gt;)</c>, with the
/// fixed-width C++ type for a primitive target, the <c>::</c>-qualified name for
/// a named target, and a pointer target for an XObject-derived type.
/// </summary>
public sealed class CastLoweringRuleTests
{
    private const string XObjectStub = """
        namespace XPact.CoreXObject
        {
            public abstract class XObject { }
        }
        """;

    [Fact]
    public void CanHandle_OnlyCastExpression()
    {
        CastLoweringRule rule = new();
        (_, CastExpressionSyntax cast) =
            RuleTestHelpers.ContextAndNode<CastExpressionSyntax>(
                "class C { int M(long x) { return (int)x; } }");

        Assert.True(rule.CanHandle(cast));
        Assert.False(rule.CanHandle(cast.Expression));
    }

    [Fact]
    public void Cast_ToPrimitive_EmitsStaticCastFixedWidth()
    {
        CastLoweringRule rule = new();
        (EmitContext ctx, CastExpressionSyntax cast) =
            RuleTestHelpers.ContextAndNode<CastExpressionSyntax>(
                "class C { int M(long x) { return (int)x; } }");

        string cpp = RuleTestHelpers.EmitWith(rule, ctx, cast);

        Assert.Equal(
            "static_cast<int32_t>(// TODO(6.e): IdentifierName not yet lowered\n)",
            cpp);
    }

    [Fact]
    public void Cast_ToNamedType_EmitsQualifiedName()
    {
        CastLoweringRule rule = new();
        (EmitContext ctx, CastExpressionSyntax cast) =
            RuleTestHelpers.ContextAndNode<CastExpressionSyntax>(
                "namespace Game { struct Vec { } class C { void M(object o) { var v = (Vec)o; } } }");

        string cpp = RuleTestHelpers.EmitWith(rule, ctx, cast);

        Assert.StartsWith("static_cast<::Game::Vec>(", cpp);
    }

    [Fact]
    public void Cast_ToXObjectDerived_EmitsPointerStaticCast()
    {
        CastLoweringRule rule = new();
        (EmitContext ctx, CastExpressionSyntax cast) =
            RuleTestHelpers.ContextAndNode<CastExpressionSyntax>(
                XObjectStub
                + "namespace Game { using XPact.CoreXObject; public class Actor : XObject { } "
                + "public class C { public void M(XObject o) { var a = (Actor)o; } } }");

        string cpp = RuleTestHelpers.EmitWith(rule, ctx, cast);

        Assert.StartsWith("static_cast<::Game::Actor*>(", cpp);
    }
}
