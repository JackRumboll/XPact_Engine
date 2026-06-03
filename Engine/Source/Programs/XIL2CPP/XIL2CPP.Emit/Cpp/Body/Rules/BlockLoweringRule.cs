// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Lowers a C# <see cref="BlockSyntax"/> (a brace-delimited statement list) to
/// a C++ brace block, per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5
/// (statement mapping). The block opens a brace, recurses each contained
/// statement through the parent <see cref="StatementEmitter"/> (so nesting and
/// indentation thread the shared <see cref="CppWriter"/>), then closes the
/// brace.
/// </summary>
/// <remarks>
/// <para>
/// A C# block maps one-to-one to a C++ compound statement: the brace scope and
/// statement order are preserved verbatim. An empty block emits an empty
/// <c>{ }</c> pair (open then immediate close), which is valid C++ and keeps
/// the source-to-source shape faithful.
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> Statements are emitted in
/// declaration order with no ambient state, so two builds over the same tree
/// are byte-identical.
/// </para>
/// </remarks>
public sealed class BlockLoweringRule : IBodyLoweringRule
{
    /// <inheritdoc/>
    public string Name => "ControlFlow.Block";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node) => node is BlockSyntax;

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        BlockSyntax block = (BlockSyntax)node;

        writer.BeginBlock();
        foreach (StatementSyntax statement in block.Statements)
        {
            parent.EmitStatement(statement);
        }
        writer.EndBlock();
    }
}
