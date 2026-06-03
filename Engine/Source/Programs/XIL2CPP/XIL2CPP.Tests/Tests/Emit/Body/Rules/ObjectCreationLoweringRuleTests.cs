// Copyright Simgenics. All Rights Reserved.

using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit.Body.Rules;

/// <summary>
/// Tests for <see cref="ObjectCreationLoweringRule"/>: a NON-XObject
/// <c>new T(args)</c> lowers to brace init <c>T{args}</c> for a value type and
/// to a constructor call <c>T(args)</c> for a reference type; an XObject-derived
/// <c>new</c> (XIL2CPP001 territory) emits a DEFERRED comment instead of a
/// fabricated construction.
/// </summary>
public sealed class ObjectCreationLoweringRuleTests
{
    private const string XObjectStub = """
        namespace XPact.CoreXObject
        {
            public abstract class XObject { }
        }
        """;

    [Fact]
    public void CanHandle_OnlyObjectCreation()
    {
        ObjectCreationLoweringRule rule = new();
        (_, ObjectCreationExpressionSyntax creation) =
            RuleTestHelpers.ContextAndNode<ObjectCreationExpressionSyntax>(
                "namespace G { struct V { public V(int x) { } } class C { void M() { var v = new V(1); } } }");

        Assert.True(rule.CanHandle(creation));
    }

    [Fact]
    public void ValueType_EmitsBraceInit()
    {
        ObjectCreationLoweringRule rule = new();
        (EmitContext ctx, ObjectCreationExpressionSyntax creation) =
            RuleTestHelpers.ContextAndNode<ObjectCreationExpressionSyntax>(
                "namespace G { struct V { public V(int x) { } } class C { void M(int n) { var v = new V(n); } } }");

        string cpp = RuleTestHelpers.EmitWith(rule, ctx, creation);

        Assert.StartsWith("::G::V{", cpp);
        Assert.EndsWith("}", cpp);
        Assert.DoesNotContain("DEFERRED", cpp);
    }

    [Fact]
    public void ReferenceType_EmitsConstructorCall()
    {
        ObjectCreationLoweringRule rule = new();
        (EmitContext ctx, ObjectCreationExpressionSyntax creation) =
            RuleTestHelpers.ContextAndNode<ObjectCreationExpressionSyntax>(
                "namespace G { class R { public R(int x) { } } class C { void M(int n) { var r = new R(n); } } }");

        string cpp = RuleTestHelpers.EmitWith(rule, ctx, creation);

        Assert.StartsWith("::G::R(", cpp);
        Assert.EndsWith(")", cpp);
        Assert.DoesNotContain("DEFERRED", cpp);
    }

    [Fact]
    public void NoArgs_EmitsEmptyConstructor()
    {
        ObjectCreationLoweringRule rule = new();
        (EmitContext ctx, ObjectCreationExpressionSyntax creation) =
            RuleTestHelpers.ContextAndNode<ObjectCreationExpressionSyntax>(
                "namespace G { class R { } class C { void M() { var r = new R(); } } }");

        string cpp = RuleTestHelpers.EmitWith(rule, ctx, creation);

        Assert.Equal("::G::R()", cpp);
    }

    [Fact]
    public void XObjectDerived_EmitsDeferred()
    {
        ObjectCreationLoweringRule rule = new();
        (EmitContext ctx, ObjectCreationExpressionSyntax creation) =
            RuleTestHelpers.ContextAndNode<ObjectCreationExpressionSyntax>(
                XObjectStub
                + "namespace G { using XPact.CoreXObject; public class Actor : XObject { } "
                + "public class C { public void M() { var a = new Actor(); } } }");

        string cpp = RuleTestHelpers.EmitWith(rule, ctx, creation);

        Assert.Contains("DEFERRED(6.e):", cpp);
        Assert.Contains("XIL2CPP001", cpp);
        Assert.Contains("Actor", cpp);
    }
}
