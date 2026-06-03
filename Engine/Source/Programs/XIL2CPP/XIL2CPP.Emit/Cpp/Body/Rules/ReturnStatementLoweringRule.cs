// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Lowers a C# <see cref="ReturnStatementSyntax"/> to a C++ <c>return</c>
/// statement, per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5 (statement
/// mapping). A value return (<c>return &lt;expr&gt;;</c>) recurses the returned
/// expression through the parent expression emitter; a void return
/// (<c>return;</c>) emits the bare keyword and terminator.
/// </summary>
/// <remarks>
/// <para>
/// The terminating <c>;</c> is emitted on the same line as the keyword (and the
/// recursed expression). Because the shared <see cref="CppWriter"/>'s
/// <see cref="CppWriter.AppendLine(string)"/> closes the line, the statement
/// occupies exactly one line at the current indent.
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> The keyword and terminator
/// are literal text; the only variability is the recursed expression, which is
/// itself deterministic.
/// </para>
/// </remarks>
public sealed class ReturnStatementLoweringRule : IBodyLoweringRule
{
    /// <inheritdoc/>
    public string Name => "ControlFlow.Return";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node) => node is ReturnStatementSyntax;

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        ReturnStatementSyntax ret = (ReturnStatementSyntax)node;

        if (ret.Expression is null)
        {
            writer.AppendLine("return;");
            return;
        }

        // "return " then the recursed expression then the ";" terminator, all
        // composed onto one line via the raw Append escape hatch. Append does
        // not inject indentation, so the current indent is replicated by hand
        // (CppWriter exposes no indent-only primitive) to keep the statement
        // aligned at its scope depth.
        ControlFlowEmitHelpers.AppendIndent(writer);
        writer.Append("return ");
        parent.Expressions.EmitExpression(ret.Expression);
        writer.Append(";");
        writer.AppendLine();
    }
}
