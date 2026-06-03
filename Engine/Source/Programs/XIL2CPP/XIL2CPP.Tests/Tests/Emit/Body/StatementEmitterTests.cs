// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Emit;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit.Body;

/// <summary>
/// Tests for <see cref="StatementEmitter"/> / <see cref="ExpressionEmitter"/>
/// / <see cref="BodyLoweringRuleRegistry"/>: with zero rules every node falls
/// to a <c>// TODO(6.e): &lt;NodeKind&gt; not yet lowered</c> comment; an
/// injected (test-only fake) rule is dispatched to; and the production
/// reflection-discovery is scoped to the <c>XIL2CPP.Emit</c> assembly (so a
/// test-only rule is never auto-discovered as a production rule).
/// </summary>
public sealed class StatementEmitterTests
{
    /// <summary>
    /// A test-only fake rule that lowers a <c>return</c> statement to a fixed
    /// marker so the test can prove dispatch happened. It lives in
    /// <c>XIL2CPP.Tests</c> so the production <see cref="BodyLoweringRuleRegistry.DiscoverRules"/>
    /// (scoped to <c>XIL2CPP.Emit</c>) never picks it up.
    /// </summary>
    private sealed class FakeReturnRule : IBodyLoweringRule
    {
        public const string Marker = "// FAKE-RULE lowered a return";

        public string Name => "Fake.Return";

        public bool CanHandle(SyntaxNode node) => node is ReturnStatementSyntax;

        public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
        {
            writer.AppendLine(Marker);
        }
    }

    [Fact]
    public void EmitStatement_NoRules_EmitsTodoComment()
    {
        EmitContext ctx = EmitTestHelpers.BuildEmitContext();
        BodyLoweringRuleRegistry registry = new(new List<IBodyLoweringRule>());
        CppWriter writer = new();
        StatementEmitter emitter = new(ctx, writer, registry);

        ReturnStatementSyntax ret = ParseReturn();
        emitter.EmitStatement(ret);

        Assert.Equal(
            $"// {StatementEmitter.TodoPrefix}{SyntaxKind.ReturnStatement} not yet lowered\n",
            writer.Build());
    }

    [Fact]
    public void EmitExpression_NoRules_EmitsTodoComment()
    {
        EmitContext ctx = EmitTestHelpers.BuildEmitContext();
        BodyLoweringRuleRegistry registry = new(new List<IBodyLoweringRule>());
        CppWriter writer = new();
        StatementEmitter emitter = new(ctx, writer, registry);

        ExpressionSyntax expr = ParseExpression("1 + 2");
        emitter.Expressions.EmitExpression(expr);

        Assert.Contains("TODO(6.e):", writer.Build());
        Assert.Contains("not yet lowered", writer.Build());
    }

    [Fact]
    public void EmitStatement_WithMatchingRule_DispatchesToRule()
    {
        EmitContext ctx = EmitTestHelpers.BuildEmitContext();
        BodyLoweringRuleRegistry registry = new(new IBodyLoweringRule[] { new FakeReturnRule() });
        CppWriter writer = new();
        StatementEmitter emitter = new(ctx, writer, registry);

        emitter.EmitStatement(ParseReturn());

        Assert.Equal(FakeReturnRule.Marker + "\n", writer.Build());
        Assert.DoesNotContain("TODO(6.e)", writer.Build());
    }

    [Fact]
    public void Registry_FindRule_ReturnsTheMatchingRuleOrNull()
    {
        BodyLoweringRuleRegistry registry = new(new IBodyLoweringRule[] { new FakeReturnRule() });

        Assert.IsType<FakeReturnRule>(registry.FindRule(ParseReturn()));
        Assert.Null(registry.FindRule(ParseExpression("1 + 2")));
    }

    [Fact]
    public void Registry_RulesSortedByNameOrdinal()
    {
        BodyLoweringRuleRegistry registry = new(new IBodyLoweringRule[]
        {
            new NamedRule("Zeta"),
            new NamedRule("Alpha"),
            new NamedRule("Mu"),
        });

        Assert.Equal(new[] { "Alpha", "Mu", "Zeta" }, registry.Rules.Select(r => r.Name));
    }

    [Fact]
    public void Discover_ProductionRegistry_DoesNotPickUpTestOnlyFakeRule()
    {
        // The production discovery is scoped to XIL2CPP.Emit; the test-only
        // FakeReturnRule lives in XIL2CPP.Tests, so it is never discovered.
        BodyLoweringRuleRegistry discovered = BodyLoweringRuleRegistry.Discover();
        Assert.DoesNotContain(discovered.Rules, r => r is FakeReturnRule);
    }

    [Fact]
    public void Discover_RegistryFallsBackToTodoForUnhandledNode()
    {
        // The production-discovered registry dispatches a node it has a rule for
        // and falls back to the // TODO(6.e) comment for a node no rule handles.
        // (Originally probed with a `return`, but once the WU-D2 control-flow
        // rules landed `return` is lowered; the expression statement that next
        // stood in for an un-lowered node is now lowered too -- the FIX-2
        // ExpressionStatementLoweringRule routes it through the inner
        // expression's rule. An EMPTY statement (`;`) still has no rule, so it
        // exercises the genuine fallback path -- preserving this test's intent
        // that the registry "still works" + degrades to the TODO marker for
        // un-lowered nodes.)
        BodyLoweringRuleRegistry discovered = BodyLoweringRuleRegistry.Discover();
        EmitContext ctx = EmitTestHelpers.BuildEmitContext();
        CppWriter writer = new();
        StatementEmitter emitter = new(ctx, writer, discovered);

        emitter.EmitStatement(ParseUnhandledStatement());
        Assert.Contains("TODO(6.e):", writer.Build());
    }

    private sealed class NamedRule : IBodyLoweringRule
    {
        public NamedRule(string name) => Name = name;

        public string Name { get; }

        public bool CanHandle(SyntaxNode node) => false;

        public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
        {
        }
    }

    private static ReturnStatementSyntax ParseReturn()
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText("class C { void M() { return; } }");
        return tree.GetRoot().DescendantNodes().OfType<ReturnStatementSyntax>().First();
    }

    /// <summary>
    /// Parse a statement that NO production body-lowering rule handles (an EMPTY
    /// statement, <c>;</c>), so the production-discovered registry exercises its
    /// TODO fallback path. (An expression statement is now lowered by the FIX-2
    /// <c>ExpressionStatementLoweringRule</c>, so it no longer falls back.)
    /// </summary>
    private static StatementSyntax ParseUnhandledStatement()
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText("class C { void M() { ; } }");
        return tree.GetRoot().DescendantNodes().OfType<EmptyStatementSyntax>().First();
    }

    private static ExpressionSyntax ParseExpression(string expr)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText($"class C {{ int M() => {expr}; }}");
        return tree.GetRoot().DescendantNodes().OfType<BinaryExpressionSyntax>().First();
    }
}
