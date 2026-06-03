// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Core;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Lowers a C# <see cref="ForStatementSyntax"/> to a C++ <c>for</c> loop, per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.3 + Section 6.5 (loops + the
/// back-edge safe-point check): <c>for (&lt;init&gt;; &lt;cond&gt;; &lt;incr&gt;) {
/// XPACT_BACKEDGE_SAFEPOINT_CHECK(); &lt;body&gt; }</c>. The init / condition /
/// incrementors are recursed through the parent expression emitter; the body is
/// recursed through the parent statement emitter.
/// </summary>
/// <remarks>
/// <para>
/// <b>The 6.g back-edge safe-point check.</b> Per Section 6.5 every long-loop
/// back-edge emits an <c>XPACT_BACKEDGE_SAFEPOINT_CHECK();</c> as the FIRST body
/// line so the GC can preempt a determined-long loop. The check is OMITTED only
/// for a provably-short loop -- one the Pass-3 <see cref="LongLoopAnalyzer"/>
/// recorded a <see cref="LongLoopSite"/> for with
/// <see cref="LongLoopSite.IsLong"/> false (a statically-bounded iteration count
/// of <see cref="LongLoopAnalyzer.ShortLoopThreshold"/> or fewer). A loop with
/// NO recorded site emits the check too (the safe default), so a needed safe
/// point is never silently skipped -- see
/// <see cref="LoopBackEdgeSafepoint"/>.
/// </para>
/// <para>
/// <b>Single brace pair.</b> The rule owns the loop's brace block. When the
/// body is itself a C# block its inner statements are recursed directly (the
/// block's own braces are NOT re-emitted); a single (non-block) body statement
/// is recursed as the sole body statement -- so the C++ always has exactly one
/// brace pair after the back-edge check.
/// </para>
/// <para>
/// <b>Header clauses.</b> A C# <c>for</c> may declare its loop variable
/// (<see cref="ForStatementSyntax.Declaration"/>) or use initializer
/// expressions (<see cref="ForStatementSyntax.Initializers"/>); either form is
/// recursed in the first header clause. A missing condition / empty
/// incrementor list emits an empty clause (<c>for (init; ; )</c>), valid C++.
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> Header clauses and body
/// statements are emitted in source order; the back-edge macro and separators
/// are literal text; the short-loop decision is a pure function of the
/// Pass-3 site table.
/// </para>
/// </remarks>
public sealed class ForLoopLoweringRule : IBodyLoweringRule
{
    /// <summary>
    /// The 6.g back-edge safe-point check statement, emitted as the first body
    /// line of every lowered loop that is not provably short. Includes the
    /// trailing <c>;</c> (the macro expands to a statement).
    /// </summary>
    public const string BackEdgeCheckStatement = LoopBackEdgeSafepoint.CheckStatement;

    /// <inheritdoc/>
    public string Name => "ControlFlow.ForLoop";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node) => node is ForStatementSyntax;

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        ForStatementSyntax forStmt = (ForStatementSyntax)node;

        // Header: "for (" <init> "; " <cond> "; " <incr> ") {" composed on one
        // indented line (the recursed clauses sit inline in the header).
        ControlFlowEmitHelpers.AppendIndent(writer);
        writer.Append("for (");
        EmitInit(forStmt, context, writer, parent);
        writer.Append("; ");
        if (forStmt.Condition is not null)
        {
            parent.Expressions.EmitExpression(forStmt.Condition);
        }
        writer.Append("; ");
        EmitIncrementors(forStmt, writer, parent);
        writer.Append(") {");
        writer.AppendLine();
        writer.Indent();

        // The 6.g back-edge safe-point check as the first body line, unless the
        // loop is provably short (a recorded short site).
        LoopBackEdgeSafepoint.EmitCheckUnlessProvablyShort(forStmt, context, writer);

        EmitBody(forStmt.Statement, parent);

        writer.Unindent();
        ControlFlowEmitHelpers.AppendIndent(writer);
        writer.Append("}");
        writer.AppendLine();
    }

    /// <summary>
    /// Emit the first <c>for</c> header clause: the variable declaration when
    /// present, otherwise the (comma-separated) initializer expressions. An
    /// empty clause emits nothing (a leading <c>for ( ;</c>).
    /// </summary>
    /// <remarks>
    /// FIX 3: a for-init that is a <see cref="VariableDeclarationSyntax"/>
    /// (<c>for (int i = 0; ...)</c>) is NOT an expression, so dispatching it
    /// through the expression emitter found no rule and emitted a
    /// <c>// TODO(6.e)</c> comment INSIDE the <c>for(...)</c> header
    /// &#8211; ill-formed C++. It is now lowered to an INLINE C++ declaration
    /// (<c>int i = 0</c>) by the shared
    /// <see cref="LocalDeclarationLoweringRule.EmitInlineDeclaration"/>, which
    /// reuses the same type-spelling + initializer logic the local-declaration
    /// statement uses (so the for-init declaration spelling stays consistent with
    /// a statement-position declaration, including the XObject pointer spelling).
    /// </remarks>
    private static void EmitInit(
        ForStatementSyntax forStmt, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        if (forStmt.Declaration is not null)
        {
            LocalDeclarationLoweringRule.EmitInlineDeclaration(
                forStmt.Declaration, context, writer, parent);
            return;
        }

        EmitCommaSeparated(forStmt.Initializers, writer, parent);
    }

    /// <summary>
    /// Emit the third <c>for</c> header clause: the (comma-separated)
    /// incrementor expressions. An empty list emits nothing.
    /// </summary>
    private static void EmitIncrementors(ForStatementSyntax forStmt, CppWriter writer, StatementEmitter parent)
        => EmitCommaSeparated(forStmt.Incrementors, writer, parent);

    /// <summary>
    /// Recurse each expression in <paramref name="expressions"/> inline,
    /// separated by <c>", "</c>, in source order.
    /// </summary>
    private static void EmitCommaSeparated(
        SeparatedSyntaxList<ExpressionSyntax> expressions,
        CppWriter writer,
        StatementEmitter parent)
    {
        for (int i = 0; i < expressions.Count; i++)
        {
            if (i > 0)
            {
                writer.Append(", ");
            }
            parent.Expressions.EmitExpression(expressions[i]);
        }
    }

    /// <summary>
    /// Emit the loop body: if it is a C# block, recurse its inner statements
    /// directly (the rule already owns the surrounding braces); otherwise
    /// recurse the single statement.
    /// </summary>
    private static void EmitBody(StatementSyntax body, StatementEmitter parent)
    {
        if (body is BlockSyntax block)
        {
            foreach (StatementSyntax statement in block.Statements)
            {
                parent.EmitStatement(statement);
            }
            return;
        }

        parent.EmitStatement(body);
    }
}

/// <summary>
/// Shared back-edge safe-point emission for the loop-lowering rules
/// (<see cref="ForLoopLoweringRule"/>, <see cref="WhileLoopLoweringRule"/>,
/// <see cref="DoWhileLoopLoweringRule"/>) per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 6.5. Centralises the
/// "emit the <c>XPACT_BACKEDGE_SAFEPOINT_CHECK();</c> at the loop back-edge
/// UNLESS the loop is provably short" decision so every loop form applies the
/// identical, deterministic rule.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fail-safe default.</b> The Pass-3 <see cref="LongLoopAnalyzer"/>
/// records a <see cref="LongLoopSite"/> per loop, keyed by source span, with
/// <see cref="LongLoopSite.IsLong"/> false ONLY when the loop is provably short.
/// This helper emits the check when the matching site is long, and -- crucially
/// -- ALSO when NO matching site is found. A missing record (e.g. the analyzer
/// did not run for this build, or the span could not be matched) therefore
/// defaults to EMITTING the safe point, never silently skipping it. The only
/// path that omits the check is an explicit, recorded, provably-short site.
/// </para>
/// <para>
/// <b>Span matching.</b> The site's <see cref="SourceSpan"/> is the analyzer's
/// 1-based line span of the loop statement; this helper recomputes the same
/// span from the node being lowered. Both operate on the same Roslyn
/// <c>SyntaxTree</c> (the same <c>FilePath</c>) and the same node, so the spans
/// are byte-equal and the lookup is exact.
/// </para>
/// </remarks>
internal static class LoopBackEdgeSafepoint
{
    /// <summary>
    /// The back-edge safe-point check statement (the C++ macro invocation with
    /// its trailing semicolon). Per Section 6.5 the macro
    /// <c>XPACT_BACKEDGE_SAFEPOINT_CHECK()</c> polls the collector at the loop
    /// back-edge so a determined-long loop can be preempted.
    /// </summary>
    public const string CheckStatement = "XPACT_BACKEDGE_SAFEPOINT_CHECK();";

    /// <summary>
    /// Emit the back-edge safe-point check (an indented
    /// <c>XPACT_BACKEDGE_SAFEPOINT_CHECK();</c> line) into
    /// <paramref name="writer"/> at the writer's current indent depth, UNLESS
    /// <paramref name="loop"/> matches a Pass-3 <see cref="LongLoopSite"/> that
    /// is provably short. A loop with no matching site emits the check (the
    /// safe default).
    /// </summary>
    /// <param name="loop">The loop statement being lowered. Must not be null.</param>
    /// <param name="context">The emit context (carries the Pass-3 site table). Must not be null.</param>
    /// <param name="writer">The writer to emit the check into. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    public static void EmitCheckUnlessProvablyShort(
        SyntaxNode loop,
        EmitContext context,
        CppWriter writer)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);

        if (ShouldEmitCheck(loop, context))
        {
            writer.AppendLine(CheckStatement);
        }
    }

    /// <summary>
    /// The back-edge decision: true (emit the check) unless the loop matches a
    /// recorded <see cref="LongLoopSite"/> whose <see cref="LongLoopSite.IsLong"/>
    /// is false. A loop with no matching recorded site returns true.
    /// </summary>
    /// <param name="loop">The loop statement being lowered. Must not be null.</param>
    /// <param name="context">The emit context. Must not be null.</param>
    /// <returns>True iff the back-edge safe-point check should be emitted.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="loop"/> or <paramref name="context"/> is null.</exception>
    public static bool ShouldEmitCheck(SyntaxNode loop, EmitContext context)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(context);

        SourceSpan loopSpan = ToSpan(loop);
        foreach (LongLoopSite site in context.Pass3.GetAll<LongLoopSite>())
        {
            if (site.Span.Equals(loopSpan))
            {
                // Provably-short site -> omit; otherwise (long) -> emit.
                return site.IsLong;
            }
        }

        // No matching record: emit the check (the fail-safe default per
        // Section 6.5 -- never silently skip a needed safe point).
        return true;
    }

    /// <summary>
    /// Build a 1-based <see cref="SourceSpan"/> covering <paramref name="node"/>
    /// from its Roslyn line span, byte-identical to the span
    /// <see cref="LongLoopAnalyzer"/> records for the same node.
    /// </summary>
    private static SourceSpan ToSpan(SyntaxNode node)
    {
        FileLinePositionSpan lineSpan = node.GetLocation().GetLineSpan();
        Microsoft.CodeAnalysis.Text.LinePosition start = lineSpan.StartLinePosition;
        Microsoft.CodeAnalysis.Text.LinePosition end = lineSpan.EndLinePosition;
        return new SourceSpan(
            lineSpan.Path,
            start.Line + 1,
            start.Character + 1,
            end.Line + 1,
            end.Character + 1,
            validate: true);
    }
}
