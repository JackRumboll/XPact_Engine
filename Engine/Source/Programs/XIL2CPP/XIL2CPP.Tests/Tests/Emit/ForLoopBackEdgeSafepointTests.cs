// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Simgenics.XPact.XIL2CPP.Tiering;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit;

/// <summary>
/// Tests for the WU-6G-BACKEDGE loop back-edge safe-point emission per
/// /Documents/XIL2CPP.html Rev 4 Section 6.5: the
/// <see cref="ForLoopLoweringRule"/> / <see cref="WhileLoopLoweringRule"/> /
/// <see cref="DoWhileLoopLoweringRule"/> emit a real
/// <c>XPACT_BACKEDGE_SAFEPOINT_CHECK();</c> as the first body line UNLESS the
/// Pass-3 <see cref="LongLoopAnalyzer"/> classed the loop provably short. The
/// tests drive the FULL pipeline (with the LongLoopAnalyzer wired in) so the
/// rule's span-keyed consult of the recorded <see cref="LongLoopSite"/> table
/// is exercised end-to-end, and additionally drive a no-site context (default
/// emit, the fail-safe) and a hand-built short-site context (omit).
/// </summary>
public sealed class ForLoopBackEdgeSafepointTests
{
    private const string BackEdgeCheck = "XPACT_BACKEDGE_SAFEPOINT_CHECK();";

    /// <summary>
    /// A test-only inline-expression rule: renders any non-statement
    /// expression / declaration node as its verbatim source text via the raw
    /// <see cref="CppWriter.Append(string)"/>, standing in for the real
    /// expression-lowering rules so the loop header / body composition is
    /// assertable at its intended shape.
    /// </summary>
    private sealed class InlineSourceExpressionRule : IBodyLoweringRule
    {
        public string Name => "Test.InlineSourceExpression";

        public bool CanHandle(SyntaxNode node)
            => node is ExpressionSyntax or VariableDeclarationSyntax;

        public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
            => writer.Append(node.ToString());
    }

    /// <summary>
    /// A test-only expression-statement rule: lowers an
    /// <see cref="ExpressionStatementSyntax"/> to an indented
    /// <c>&lt;expr&gt;;</c> line (recursing the inner expression through the
    /// parent emitter), so a realistic loop body (e.g. <c>xs[i] = i;</c>) lowers
    /// to a statement instead of the foundation's TODO fallback. The real
    /// expression-statement rule arrives in a separate wave; this stand-in keeps
    /// the back-edge assertions over genuine loop bodies.
    /// </summary>
    private sealed class InlineExpressionStatementRule : IBodyLoweringRule
    {
        public string Name => "Test.InlineExpressionStatement";

        public bool CanHandle(SyntaxNode node) => node is ExpressionStatementSyntax;

        public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
        {
            ExpressionStatementSyntax stmt = (ExpressionStatementSyntax)node;
            for (int i = 0; i < writer.Depth; i++)
            {
                writer.Append(CppWriter.IndentUnit);
            }
            parent.Expressions.EmitExpression(stmt.Expression);
            writer.Append(";");
            writer.AppendLine();
        }
    }

    private static BodyLoweringRuleRegistry LoopRegistry()
        => new(new IBodyLoweringRule[]
        {
            new ForLoopLoweringRule(),
            new WhileLoopLoweringRule(),
            new DoWhileLoopLoweringRule(),
            new BlockLoweringRule(),
            new ReturnStatementLoweringRule(),
            new InlineExpressionStatementRule(),
            new InlineSourceExpressionRule(),
        });

    /// <summary>
    /// Build an <see cref="EmitContext"/> over <paramref name="source"/> running
    /// the supplied <paramref name="analyzers"/> (so a test can include or
    /// exclude the <see cref="LongLoopAnalyzer"/>), and return it with the first
    /// node of type <typeparamref name="TNode"/> from the context's tree.
    /// </summary>
    private static (EmitContext Context, TNode Node) ContextAndNode<TNode>(
        string source,
        params ISemanticAnalyzer[] analyzers)
        where TNode : SyntaxNode
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(isSimPath: false, source);
        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        Pass3Result pass3 = Pass3Driver.Run(unit, analyzers);
        TierTable tierTable = Pass4Driver.Run(unit, pass3);
        ManglingTable manglingTable = Pass5Driver.Run(unit, tierTable, EmitTestHelpers.ContractVersionTag);

        EmitContext context = new(
            unit,
            pass3,
            tierTable,
            manglingTable,
            EmitTestHelpers.ContractVersionTag,
            EmitTestHelpers.GCRootAbi,
            EmitTestHelpers.ExceptionAbi,
            EmitTestHelpers.ManglingSchemeTag);

        SyntaxTree tree = context.Unit.Pass1.ParsedFiles[0].Tree;
        TNode node = tree.GetRoot().DescendantNodes().OfType<TNode>().First();
        return (context, node);
    }

    private static string Emit(EmitContext context, SyntaxNode node)
    {
        CppWriter writer = new();
        StatementEmitter emitter = new(context, writer, LoopRegistry());
        emitter.EmitStatement(node);
        return writer.Build();
    }

    private static string Wrap(string methodBody)
        => "namespace M; public class A { public void F(int n, int[] xs) { " + methodBody + " } }";

    // =================================================================
    // for: emit the real macro for a long loop; omit for a short loop.
    // =================================================================

    [Fact]
    public void For_LongLoop_EmitsRealBackEdgeMacroAsFirstBodyLine()
    {
        (EmitContext ctx, ForStatementSyntax node) =
            ContextAndNode<ForStatementSyntax>(
                Wrap("for (int i = 0; i < n; i++) { xs[i] = i; }"),
                new LongLoopAnalyzer());

        string cpp = Emit(ctx, node);

        Assert.Contains(BackEdgeCheck, cpp);
        // FIX 3: the for-init declaration is lowered inline by the shared
        // LocalDeclarationLoweringRule.EmitInlineDeclaration, so `int` resolves
        // to the fixed-width `int32_t` (the genuine C++ type spelling), not the
        // verbatim source `int`.
        Assert.Equal(
            "for (int32_t i = 0; i < n; i++) {\n"
            + "    " + BackEdgeCheck + "\n"
            + "    xs[i] = i;\n"
            + "}\n",
            cpp);
    }

    [Fact]
    public void For_ProvablyShortLoop_OmitsBackEdgeMacro()
    {
        (EmitContext ctx, ForStatementSyntax node) =
            ContextAndNode<ForStatementSyntax>(
                Wrap("for (int i = 0; i < 8; i++) { xs[i] = i; }"),
                new LongLoopAnalyzer());

        string cpp = Emit(ctx, node);

        Assert.DoesNotContain(BackEdgeCheck, cpp);
        Assert.Equal(
            "for (int32_t i = 0; i < 8; i++) {\n"
            + "    xs[i] = i;\n"
            + "}\n",
            cpp);
    }

    [Fact]
    public void For_NoLongLoopSiteRecorded_EmitsMacro_FailSafeDefault()
    {
        // With NO LongLoopAnalyzer in the analyzer set there is no recorded
        // site; the rule must default to EMITTING the safe point (never silently
        // skip a needed back-edge check), even for a syntactically short loop.
        (EmitContext ctx, ForStatementSyntax node) =
            ContextAndNode<ForStatementSyntax>(
                Wrap("for (int i = 0; i < 8; i++) { xs[i] = i; }"));

        string cpp = Emit(ctx, node);

        Assert.Contains(BackEdgeCheck, cpp);
    }

    // =================================================================
    // while: lowering + back-edge placement.
    // =================================================================

    [Fact]
    public void While_LongLoop_LowersToCppWhile_WithBackEdgeMacroFirst()
    {
        (EmitContext ctx, WhileStatementSyntax node) =
            ContextAndNode<WhileStatementSyntax>(
                Wrap("while (n > 0) { xs[0] = n; n = n - 1; }"),
                new LongLoopAnalyzer());

        string cpp = Emit(ctx, node);

        Assert.Equal(
            "while (n > 0) {\n"
            + "    " + BackEdgeCheck + "\n"
            + "    xs[0] = n;\n"
            + "    n = n - 1;\n"
            + "}\n",
            cpp);
    }

    [Fact]
    public void While_ConstantFalse_IsShort_OmitsBackEdgeMacro()
    {
        (EmitContext ctx, WhileStatementSyntax node) =
            ContextAndNode<WhileStatementSyntax>(
                Wrap("while (false) { xs[0] = 1; }"),
                new LongLoopAnalyzer());

        string cpp = Emit(ctx, node);

        Assert.DoesNotContain(BackEdgeCheck, cpp);
        Assert.Equal(
            "while (false) {\n"
            + "    xs[0] = 1;\n"
            + "}\n",
            cpp);
    }

    [Fact]
    public void While_UnbracedBody_StillEmitsCheckAndSingleBracePair()
    {
        (EmitContext ctx, WhileStatementSyntax node) =
            ContextAndNode<WhileStatementSyntax>(
                Wrap("while (n > 0) n = n - 1;"),
                new LongLoopAnalyzer());

        string cpp = Emit(ctx, node);

        Assert.Equal(
            "while (n > 0) {\n"
            + "    " + BackEdgeCheck + "\n"
            + "    n = n - 1;\n"
            + "}\n",
            cpp);
    }

    [Fact]
    public void While_CanHandle_OnlyWhileStatements()
    {
        IBodyLoweringRule rule = new WhileLoopLoweringRule();
        Assert.Equal("ControlFlow.WhileLoop", rule.Name);
    }

    // =================================================================
    // do/while: lowering + back-edge placement + trailing condition.
    // =================================================================

    [Fact]
    public void DoWhile_LongLoop_LowersToCppDoWhile_WithBackEdgeMacroFirst()
    {
        (EmitContext ctx, DoStatementSyntax node) =
            ContextAndNode<DoStatementSyntax>(
                Wrap("do { xs[0] = n; n = n - 1; } while (n > 0);"),
                new LongLoopAnalyzer());

        string cpp = Emit(ctx, node);

        Assert.Equal(
            "do {\n"
            + "    " + BackEdgeCheck + "\n"
            + "    xs[0] = n;\n"
            + "    n = n - 1;\n"
            + "} while (n > 0);\n",
            cpp);
    }

    [Fact]
    public void DoWhile_ConstantFalse_IsShort_OmitsBackEdgeMacro()
    {
        (EmitContext ctx, DoStatementSyntax node) =
            ContextAndNode<DoStatementSyntax>(
                Wrap("do { xs[0] = 1; } while (false);"),
                new LongLoopAnalyzer());

        string cpp = Emit(ctx, node);

        Assert.DoesNotContain(BackEdgeCheck, cpp);
        Assert.Equal(
            "do {\n"
            + "    xs[0] = 1;\n"
            + "} while (false);\n",
            cpp);
    }

    [Fact]
    public void DoWhile_UnbracedBody_StillEmitsCheckAndTrailingCondition()
    {
        (EmitContext ctx, DoStatementSyntax node) =
            ContextAndNode<DoStatementSyntax>(
                Wrap("do n = n - 1; while (n > 0);"),
                new LongLoopAnalyzer());

        string cpp = Emit(ctx, node);

        Assert.Equal(
            "do {\n"
            + "    " + BackEdgeCheck + "\n"
            + "    n = n - 1;\n"
            + "} while (n > 0);\n",
            cpp);
    }

    [Fact]
    public void DoWhile_CanHandle_OnlyDoStatements()
    {
        IBodyLoweringRule rule = new DoWhileLoopLoweringRule();
        Assert.Equal("ControlFlow.DoWhileLoop", rule.Name);
    }

    // =================================================================
    // Discovery + determinism.
    // =================================================================

    [Fact]
    public void Discovery_PicksUpAllThreeLoopRules()
    {
        IReadOnlyList<IBodyLoweringRule> discovered = BodyLoweringRuleRegistry.DiscoverRules();
        Assert.Contains(discovered, r => r is ForLoopLoweringRule);
        Assert.Contains(discovered, r => r is WhileLoopLoweringRule);
        Assert.Contains(discovered, r => r is DoWhileLoopLoweringRule);
    }

    [Fact]
    public void Emit_IsByteDeterministicAcrossTwoRuns()
    {
        (EmitContext ctx, WhileStatementSyntax node) =
            ContextAndNode<WhileStatementSyntax>(
                Wrap("while (n > 0) { n = n - 1; }"),
                new LongLoopAnalyzer());

        Assert.Equal(Emit(ctx, node), Emit(ctx, node));
    }
}
