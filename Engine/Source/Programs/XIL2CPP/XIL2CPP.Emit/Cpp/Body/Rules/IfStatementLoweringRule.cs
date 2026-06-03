// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Lowers a C# <see cref="IfStatementSyntax"/> to a C++ <c>if</c> statement,
/// per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5 (statement mapping):
/// <c>if (&lt;cond&gt;) { &lt;then&gt; } else { &lt;else&gt; }</c>. The condition is
/// recursed through the parent expression emitter; the then-branch and
/// (optional) else-branch are recursed through the parent statement emitter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single brace pair per branch.</b> The rule owns the brace block for each
/// branch (it emits the opening brace on the header line and the matching
/// closing brace). When a branch body is itself a C# block its inner statements
/// are recursed directly (the block's own braces are NOT re-emitted) so the C++
/// has exactly one brace pair per branch; a single (non-block) branch statement
/// is recursed as the sole body statement. This keeps the emitted shape
/// canonical regardless of whether the C# author braced the branch.
/// </para>
/// <para>
/// <b><c>else if</c> chaining.</b> When the else clause is itself an
/// <see cref="IfStatementSyntax"/> (a C# <c>else if</c>) it continues on the
/// same line as the closing brace (<c>} else if (...) {</c>), matching
/// idiomatic C++; a non-<c>if</c> else body is brace-wrapped
/// (<c>} else { ... }</c>).
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> Branch order and statement
/// order are preserved verbatim; the keyword text is literal.
/// </para>
/// </remarks>
public sealed class IfStatementLoweringRule : IBodyLoweringRule
{
    /// <inheritdoc/>
    public string Name => "ControlFlow.If";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node) => node is IfStatementSyntax;

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        // Open the (indented) header line, then emit the chain whose header
        // continuation ("if (" ... ") {") is already started for it.
        ControlFlowEmitHelpers.AppendIndent(writer);
        EmitIfChain((IfStatementSyntax)node, writer, parent);
    }

    /// <summary>
    /// Emit one link of an <c>if</c> / <c>else if</c> chain. The caller has
    /// already emitted the leading indentation (for the first link) or the
    /// trailing <c>"} else "</c> (for a continuation link), so this method
    /// starts at <c>"if ("</c> and recurses the condition, body, and any else
    /// clause, leaving the writer at the chain's indent depth with the line
    /// closed.
    /// </summary>
    private static void EmitIfChain(IfStatementSyntax ifStmt, CppWriter writer, StatementEmitter parent)
    {
        writer.Append("if (");
        parent.Expressions.EmitExpression(ifStmt.Condition);
        writer.Append(") {");
        writer.AppendLine();
        writer.Indent();
        EmitBranchBody(ifStmt.Statement, parent);
        writer.Unindent();

        if (ifStmt.Else is null)
        {
            ControlFlowEmitHelpers.AppendIndent(writer);
            writer.Append("}");
            writer.AppendLine();
            return;
        }

        StatementSyntax elseBody = ifStmt.Else.Statement;
        if (elseBody is IfStatementSyntax nested)
        {
            // "} else if (...) {" -- continue the chain on the same line.
            ControlFlowEmitHelpers.AppendIndent(writer);
            writer.Append("} else ");
            EmitIfChain(nested, writer, parent);
            return;
        }

        // "} else { ... }"
        ControlFlowEmitHelpers.AppendIndent(writer);
        writer.Append("} else {");
        writer.AppendLine();
        writer.Indent();
        EmitBranchBody(elseBody, parent);
        writer.Unindent();
        ControlFlowEmitHelpers.AppendIndent(writer);
        writer.Append("}");
        writer.AppendLine();
    }

    /// <summary>
    /// Emit a branch body: if it is a C# block, recurse its inner statements
    /// directly (the rule already owns the surrounding braces); otherwise
    /// recurse the single statement.
    /// </summary>
    private static void EmitBranchBody(StatementSyntax body, StatementEmitter parent)
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
