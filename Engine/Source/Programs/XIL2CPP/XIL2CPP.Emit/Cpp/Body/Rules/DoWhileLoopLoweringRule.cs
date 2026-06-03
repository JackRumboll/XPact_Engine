// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Analysis;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Lowers a C# <see cref="DoStatementSyntax"/> to a C++ <c>do</c> / <c>while</c>
/// loop, per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.3 + Section 6.5
/// (loops + the back-edge safe-point check): <c>do {
/// XPACT_BACKEDGE_SAFEPOINT_CHECK(); &lt;body&gt; } while (&lt;cond&gt;);</c>. The
/// condition is recursed through the parent expression emitter; the body is
/// recursed through the parent statement emitter.
/// </summary>
/// <remarks>
/// <para>
/// <b>The 6.g back-edge safe-point check.</b> Per Section 6.5 the
/// <c>XPACT_BACKEDGE_SAFEPOINT_CHECK();</c> is emitted as the FIRST body line so
/// the GC can preempt a determined-long loop, UNLESS the loop is provably short
/// (the Pass-3 <see cref="LongLoopAnalyzer"/> recorded a short
/// <see cref="LongLoopSite"/> -- a <c>do { } while (false)</c> guard that runs
/// the body exactly once). A loop with NO recorded site emits the check too (the
/// safe default), so a needed safe point is never silently skipped -- see
/// <see cref="LoopBackEdgeSafepoint"/>. The check sits at the top of the body so
/// it polls on each re-entry through the back-edge, matching the
/// <c>for</c> / <c>while</c> placement.
/// </para>
/// <para>
/// <b>Trailing condition.</b> A C# <c>do</c> closes with <c>} while (cond);</c>
/// on the closing-brace line -- the trailing semicolon is required (unlike the
/// <c>while</c> / <c>for</c> head forms). The condition is recursed inline on
/// that line.
/// </para>
/// <para>
/// <b>Single brace pair.</b> The rule owns the loop's brace block. When the
/// body is itself a C# block its inner statements are recursed directly (the
/// block's own braces are NOT re-emitted); a single (non-block) body statement
/// is recursed as the sole body statement.
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> The body statements and the
/// trailing condition are emitted in source order; the keywords + back-edge
/// macro are literal text; the short-loop decision is a pure function of the
/// Pass-3 site table.
/// </para>
/// </remarks>
public sealed class DoWhileLoopLoweringRule : IBodyLoweringRule
{
    /// <inheritdoc/>
    public string Name => "ControlFlow.DoWhileLoop";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node) => node is DoStatementSyntax;

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        DoStatementSyntax doStmt = (DoStatementSyntax)node;

        // Header: "do {" on its own indented line.
        ControlFlowEmitHelpers.AppendIndent(writer);
        writer.Append("do {");
        writer.AppendLine();
        writer.Indent();

        // The 6.g back-edge safe-point check as the first body line, unless the
        // loop is provably short (a recorded short site).
        LoopBackEdgeSafepoint.EmitCheckUnlessProvablyShort(doStmt, context, writer);

        EmitBody(doStmt.Statement, parent);

        writer.Unindent();

        // Footer: "} while (" <cond> ");" -- the trailing semicolon is required.
        ControlFlowEmitHelpers.AppendIndent(writer);
        writer.Append("} while (");
        parent.Expressions.EmitExpression(doStmt.Condition);
        writer.Append(");");
        writer.AppendLine();
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
