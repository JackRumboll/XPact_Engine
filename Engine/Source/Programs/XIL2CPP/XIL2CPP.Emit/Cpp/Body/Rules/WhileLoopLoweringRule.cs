// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Analysis;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Lowers a C# <see cref="WhileStatementSyntax"/> to a C++ <c>while</c> loop,
/// per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.3 + Section 6.5 (loops +
/// the back-edge safe-point check): <c>while (&lt;cond&gt;) {
/// XPACT_BACKEDGE_SAFEPOINT_CHECK(); &lt;body&gt; }</c>. The condition is recursed
/// through the parent expression emitter; the body is recursed through the
/// parent statement emitter.
/// </summary>
/// <remarks>
/// <para>
/// <b>The 6.g back-edge safe-point check.</b> Per Section 6.5 the
/// <c>XPACT_BACKEDGE_SAFEPOINT_CHECK();</c> is emitted as the FIRST body line so
/// the GC can preempt a determined-long loop, UNLESS the loop is provably short
/// (the Pass-3 <see cref="LongLoopAnalyzer"/> recorded a short
/// <see cref="LongLoopSite"/> -- a <c>while (false)</c> guard). A loop with NO
/// recorded site emits the check too (the safe default), so a needed safe point
/// is never silently skipped -- see <see cref="LoopBackEdgeSafepoint"/>.
/// </para>
/// <para>
/// <b>Single brace pair.</b> The rule owns the loop's brace block. When the
/// body is itself a C# block its inner statements are recursed directly (the
/// block's own braces are NOT re-emitted); a single (non-block) body statement
/// is recursed as the sole body statement -- so the C++ always has exactly one
/// brace pair after the back-edge check. This mirrors
/// <see cref="ForLoopLoweringRule"/> / <see cref="IfStatementLoweringRule"/>.
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> The condition and body
/// statements are emitted in source order; the keyword + back-edge macro are
/// literal text; the short-loop decision is a pure function of the Pass-3 site
/// table.
/// </para>
/// </remarks>
public sealed class WhileLoopLoweringRule : IBodyLoweringRule
{
    /// <inheritdoc/>
    public string Name => "ControlFlow.WhileLoop";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node) => node is WhileStatementSyntax;

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        WhileStatementSyntax whileStmt = (WhileStatementSyntax)node;

        // Header: "while (" <cond> ") {" composed on one indented line.
        ControlFlowEmitHelpers.AppendIndent(writer);
        writer.Append("while (");
        parent.Expressions.EmitExpression(whileStmt.Condition);
        writer.Append(") {");
        writer.AppendLine();
        writer.Indent();

        // The 6.g back-edge safe-point check as the first body line, unless the
        // loop is provably short (a recorded short site).
        LoopBackEdgeSafepoint.EmitCheckUnlessProvablyShort(whileStmt, context, writer);

        EmitBody(whileStmt.Statement, parent);

        writer.Unindent();
        ControlFlowEmitHelpers.AppendIndent(writer);
        writer.Append("}");
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
