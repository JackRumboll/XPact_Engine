// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Lowers a C# <see cref="ForStatementSyntax"/> to a C++ <c>for</c> loop, per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.3 + Section 6.5 (loops + the
/// back-edge safe-point check): <c>for (&lt;init&gt;; &lt;cond&gt;; &lt;incr&gt;) {
/// /* back-edge hook */ &lt;body&gt; }</c>. The init / condition / incrementors
/// are recursed through the parent expression emitter; the body is recursed
/// through the parent statement emitter.
/// </summary>
/// <remarks>
/// <para>
/// <b>The 6.g back-edge safe-point hook.</b> Per Section 6.5 every long-loop
/// back-edge inserts an <c>XPACT_BACKEDGE_SAFEPOINT_CHECK()</c> so the GC can
/// preempt determined-long loops. The actual hook (and the static loop-length
/// threshold gate that decides whether to emit it) lands in wave 6.g; this rule
/// emits the hook as the FIRST body line as a deliberate
/// <c>// TODO(6.g): XPACT_BACKEDGE_SAFEPOINT_CHECK();</c> marker so the seam is
/// visible and the body shape is already correct for 6.g to fill in.
/// </para>
/// <para>
/// <b>Single brace pair.</b> The rule owns the loop's brace block. When the
/// body is itself a C# block its inner statements are recursed directly (the
/// block's own braces are NOT re-emitted); a single (non-block) body statement
/// is recursed as the sole body statement -- so the C++ always has exactly one
/// brace pair after the back-edge hook.
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
/// statements are emitted in source order; the hook marker and separators are
/// literal text.
/// </para>
/// </remarks>
public sealed class ForLoopLoweringRule : IBodyLoweringRule
{
    /// <summary>The 6.g back-edge safe-point hook, emitted as the first body line of every lowered loop.</summary>
    public const string BackEdgeHookComment = "TODO(6.g): XPACT_BACKEDGE_SAFEPOINT_CHECK();";

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
        EmitInit(forStmt, writer, parent);
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

        // The 6.g back-edge safe-point hook as the first body line.
        writer.AppendComment(BackEdgeHookComment);

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
    private static void EmitInit(ForStatementSyntax forStmt, CppWriter writer, StatementEmitter parent)
    {
        if (forStmt.Declaration is not null)
        {
            parent.Expressions.EmitExpression(forStmt.Declaration);
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
