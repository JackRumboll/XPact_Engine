// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Emit;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit.Body.Rules;

/// <summary>
/// Tests for the WU-D2 control-flow lowering rules
/// (<see cref="IfStatementLoweringRule"/>, <see cref="ReturnStatementLoweringRule"/>,
/// <see cref="BlockLoweringRule"/>, <see cref="ForLoopLoweringRule"/>). Each
/// rule recurses child statements / expressions through the parent emitter; to
/// exercise the genuine inline C++ shape (rather than the foundation's
/// zero-rule TODO fallback) the registry under test pairs the control-flow
/// rules with a deterministic test-only inline-expression rule that renders an
/// expression as its verbatim source text -- standing in for the real
/// expression-lowering rules that arrive in a separate wave.
/// </summary>
public sealed class ControlFlowLoweringRuleTests
{
    /// <summary>
    /// A test-only inline-expression rule: renders any non-statement
    /// expression / declaration node as its trimmed source text via the raw
    /// <see cref="CppWriter.Append(string)"/> (NO indentation, NO trailing
    /// newline), modelling how the real expression rules emit inline fragments
    /// so the control-flow header / return composition can be asserted at its
    /// intended shape. Lives in XIL2CPP.Tests so production discovery never
    /// picks it up.
    /// </summary>
    private sealed class InlineSourceExpressionRule : IBodyLoweringRule
    {
        public string Name => "Test.InlineSourceExpression";

        public bool CanHandle(SyntaxNode node)
            => node is ExpressionSyntax or VariableDeclarationSyntax;

        public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
            => writer.Append(node.ToString());
    }

    private static BodyLoweringRuleRegistry ControlFlowRegistryWithInlineExpressions()
        => new(new IBodyLoweringRule[]
        {
            new IfStatementLoweringRule(),
            new ReturnStatementLoweringRule(),
            new BlockLoweringRule(),
            new ForLoopLoweringRule(),
            new InlineSourceExpressionRule(),
        });

    private static BodyLoweringRuleRegistry ControlFlowRegistryOnly()
        => new(new IBodyLoweringRule[]
        {
            new IfStatementLoweringRule(),
            new ReturnStatementLoweringRule(),
            new BlockLoweringRule(),
            new ForLoopLoweringRule(),
        });

    private static string Emit(BodyLoweringRuleRegistry registry, SyntaxNode node)
    {
        EmitContext ctx = EmitTestHelpers.BuildEmitContext();
        CppWriter writer = new();
        StatementEmitter emitter = new(ctx, writer, registry);
        emitter.EmitStatement(node);
        return writer.Build();
    }

    private static TNode ParseFirst<TNode>(string methodBody)
        where TNode : SyntaxNode
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            $"class C {{ void M() {{ {methodBody} }} }}");
        return tree.GetRoot().DescendantNodes().OfType<TNode>().First();
    }

    /// <summary>
    /// Parse <paramref name="methodBody"/> and return the FIRST block nested
    /// inside the method body (skipping the method body block itself), so the
    /// block written verbatim in the test is the one under assertion.
    /// </summary>
    private static BlockSyntax ParseBlock(string methodBody)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            $"class C {{ void M() {{ {methodBody} }} }}");
        return tree.GetRoot().DescendantNodes().OfType<BlockSyntax>().Skip(1).First();
    }

    // ---- ReturnStatementLoweringRule ----------------------------------------

    [Fact]
    public void Return_Void_EmitsBareReturn()
    {
        ReturnStatementSyntax ret = ParseFirst<ReturnStatementSyntax>("return;");
        string cpp = Emit(ControlFlowRegistryWithInlineExpressions(), ret);
        Assert.Equal("return;\n", cpp);
    }

    [Fact]
    public void Return_Value_RecursesExpression()
    {
        ReturnStatementSyntax ret = ParseFirst<ReturnStatementSyntax>("return x + 1;");
        string cpp = Emit(ControlFlowRegistryWithInlineExpressions(), ret);
        Assert.Equal("return x + 1;\n", cpp);
    }

    [Fact]
    public void Return_CanHandle_OnlyReturnStatements()
    {
        IBodyLoweringRule rule = new ReturnStatementLoweringRule();
        Assert.True(rule.CanHandle(ParseFirst<ReturnStatementSyntax>("return;")));
        Assert.False(rule.CanHandle(ParseFirst<BlockSyntax>("{ }")));
        Assert.Equal("ControlFlow.Return", rule.Name);
    }

    // ---- BlockLoweringRule --------------------------------------------------

    [Fact]
    public void Block_Empty_EmitsBracePair()
    {
        BlockSyntax block = ParseBlock("{ }");
        string cpp = Emit(ControlFlowRegistryWithInlineExpressions(), block);
        Assert.Equal("{\n}\n", cpp);
    }

    [Fact]
    public void Block_RecursesAndIndentsEachStatement()
    {
        BlockSyntax block = ParseBlock("{ return a; return b; }");
        string cpp = Emit(ControlFlowRegistryWithInlineExpressions(), block);
        Assert.Equal(
            "{\n"
            + "    return a;\n"
            + "    return b;\n"
            + "}\n",
            cpp);
    }

    [Fact]
    public void Block_Nested_IndentsInnerScope()
    {
        // The OUTERMOST nested block is the method body's first child block.
        BlockSyntax outer = ParseBlock("{ { return a; } return b; }");
        string cpp = Emit(ControlFlowRegistryWithInlineExpressions(), outer);
        Assert.Equal(
            "{\n"
            + "    {\n"
            + "        return a;\n"
            + "    }\n"
            + "    return b;\n"
            + "}\n",
            cpp);
    }

    [Fact]
    public void Block_CanHandle_OnlyBlocks()
    {
        IBodyLoweringRule rule = new BlockLoweringRule();
        Assert.True(rule.CanHandle(ParseFirst<BlockSyntax>("{ }")));
        Assert.False(rule.CanHandle(ParseFirst<ReturnStatementSyntax>("return;")));
        Assert.Equal("ControlFlow.Block", rule.Name);
    }

    // ---- IfStatementLoweringRule --------------------------------------------

    [Fact]
    public void If_NoElse_EmitsBracedThen()
    {
        IfStatementSyntax ifStmt = ParseFirst<IfStatementSyntax>("if (x > 0) { return a; }");
        string cpp = Emit(ControlFlowRegistryWithInlineExpressions(), ifStmt);
        Assert.Equal(
            "if (x > 0) {\n"
            + "    return a;\n"
            + "}\n",
            cpp);
    }

    [Fact]
    public void If_WithElse_EmitsBothBranches()
    {
        IfStatementSyntax ifStmt = ParseFirst<IfStatementSyntax>(
            "if (x > 0) { return a; } else { return b; }");
        string cpp = Emit(ControlFlowRegistryWithInlineExpressions(), ifStmt);
        Assert.Equal(
            "if (x > 0) {\n"
            + "    return a;\n"
            + "} else {\n"
            + "    return b;\n"
            + "}\n",
            cpp);
    }

    [Fact]
    public void If_UnbracedThen_StillEmitsSingleBracePair()
    {
        IfStatementSyntax ifStmt = ParseFirst<IfStatementSyntax>("if (x > 0) return a;");
        string cpp = Emit(ControlFlowRegistryWithInlineExpressions(), ifStmt);
        Assert.Equal(
            "if (x > 0) {\n"
            + "    return a;\n"
            + "}\n",
            cpp);
    }

    [Fact]
    public void If_ElseIfChain_ContinuesOnClosingBraceLine()
    {
        IfStatementSyntax ifStmt = ParseFirst<IfStatementSyntax>(
            "if (a) { return x; } else if (b) { return y; } else { return z; }");
        string cpp = Emit(ControlFlowRegistryWithInlineExpressions(), ifStmt);
        Assert.Equal(
            "if (a) {\n"
            + "    return x;\n"
            + "} else if (b) {\n"
            + "    return y;\n"
            + "} else {\n"
            + "    return z;\n"
            + "}\n",
            cpp);
    }

    [Fact]
    public void If_NestedInsideBlock_IndentsCorrectly()
    {
        BlockSyntax block = ParseBlock("{ if (x > 0) { return a; } }");
        string cpp = Emit(ControlFlowRegistryWithInlineExpressions(), block);
        Assert.Equal(
            "{\n"
            + "    if (x > 0) {\n"
            + "        return a;\n"
            + "    }\n"
            + "}\n",
            cpp);
    }

    [Fact]
    public void If_CanHandle_OnlyIfStatements()
    {
        IBodyLoweringRule rule = new IfStatementLoweringRule();
        Assert.True(rule.CanHandle(ParseFirst<IfStatementSyntax>("if (x) { }")));
        Assert.False(rule.CanHandle(ParseFirst<BlockSyntax>("{ }")));
        Assert.Equal("ControlFlow.If", rule.Name);
    }

    // ---- ForLoopLoweringRule ------------------------------------------------

    [Fact]
    public void For_EmitsBackEdgeHookAsFirstBodyLine()
    {
        ForStatementSyntax forStmt = ParseFirst<ForStatementSyntax>(
            "for (int i = 0; i < n; i++) { return a; }");
        string cpp = Emit(ControlFlowRegistryWithInlineExpressions(), forStmt);
        Assert.Equal(
            "for (int i = 0; i < n; i++) {\n"
            + "    // " + ForLoopLoweringRule.BackEdgeHookComment + "\n"
            + "    return a;\n"
            + "}\n",
            cpp);
    }

    [Fact]
    public void For_HookComment_NamesThe6gWave()
    {
        Assert.Equal("TODO(6.g): XPACT_BACKEDGE_SAFEPOINT_CHECK();", ForLoopLoweringRule.BackEdgeHookComment);
    }

    [Fact]
    public void For_UnbracedBody_StillEmitsHookAndSingleBracePair()
    {
        ForStatementSyntax forStmt = ParseFirst<ForStatementSyntax>(
            "for (int i = 0; i < n; i++) return a;");
        string cpp = Emit(ControlFlowRegistryWithInlineExpressions(), forStmt);
        Assert.Equal(
            "for (int i = 0; i < n; i++) {\n"
            + "    // " + ForLoopLoweringRule.BackEdgeHookComment + "\n"
            + "    return a;\n"
            + "}\n",
            cpp);
    }

    [Fact]
    public void For_MultipleIncrementors_AreCommaSeparated()
    {
        ForStatementSyntax forStmt = ParseFirst<ForStatementSyntax>(
            "for (int i = 0; i < n; i++, j--) { return a; }");
        string cpp = Emit(ControlFlowRegistryWithInlineExpressions(), forStmt);
        Assert.Equal(
            "for (int i = 0; i < n; i++, j--) {\n"
            + "    // " + ForLoopLoweringRule.BackEdgeHookComment + "\n"
            + "    return a;\n"
            + "}\n",
            cpp);
    }

    [Fact]
    public void For_NestedInsideBlock_HookIndentsWithBody()
    {
        BlockSyntax block = ParseBlock(
            "{ for (int i = 0; i < n; i++) { return a; } }");
        string cpp = Emit(ControlFlowRegistryWithInlineExpressions(), block);
        Assert.Equal(
            "{\n"
            + "    for (int i = 0; i < n; i++) {\n"
            + "        // " + ForLoopLoweringRule.BackEdgeHookComment + "\n"
            + "        return a;\n"
            + "    }\n"
            + "}\n",
            cpp);
    }

    [Fact]
    public void For_CanHandle_OnlyForStatements()
    {
        IBodyLoweringRule rule = new ForLoopLoweringRule();
        Assert.True(rule.CanHandle(ParseFirst<ForStatementSyntax>("for (;;) { }")));
        Assert.False(rule.CanHandle(ParseFirst<BlockSyntax>("{ }")));
        Assert.Equal("ControlFlow.ForLoop", rule.Name);
    }

    // ---- Determinism --------------------------------------------------------

    [Fact]
    public void Emit_IsByteDeterministicAcrossTwoRuns()
    {
        IfStatementSyntax ifStmt = ParseFirst<IfStatementSyntax>(
            "if (a) { for (int i = 0; i < n; i++) { return x; } } else { return y; }");
        string first = Emit(ControlFlowRegistryWithInlineExpressions(), ifStmt);
        string second = Emit(ControlFlowRegistryWithInlineExpressions(), ifStmt);
        Assert.Equal(first, second);
    }

    // ---- Foundation fallback (control-flow rules only, no expression rule) ---

    [Fact]
    public void If_WithoutExpressionRule_ConditionFallsToTodoComment()
    {
        // With NO expression rule the recursed condition + the return value
        // fall to the foundation's TODO comment; the control-flow STRUCTURE
        // (the "if (" header, the opened "{" block, the recursed "return"
        // keyword, and the closing brace) is still emitted. This documents the
        // recursion seam before expression rules land.
        IfStatementSyntax ifStmt = ParseFirst<IfStatementSyntax>("if (x > 0) { return a; }");
        string cpp = Emit(ControlFlowRegistryOnly(), ifStmt);
        Assert.StartsWith("if (", cpp, System.StringComparison.Ordinal);
        Assert.Contains("TODO(6.e):", cpp);
        Assert.Contains(") {\n", cpp);
        Assert.Contains("return ", cpp);
        Assert.EndsWith("}\n", cpp, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Discovery_PicksUpAllFourControlFlowRules()
    {
        IReadOnlyList<IBodyLoweringRule> discovered = BodyLoweringRuleRegistry.DiscoverRules();
        Assert.Contains(discovered, r => r is IfStatementLoweringRule);
        Assert.Contains(discovered, r => r is ReturnStatementLoweringRule);
        Assert.Contains(discovered, r => r is BlockLoweringRule);
        Assert.Contains(discovered, r => r is ForLoopLoweringRule);
    }
}
