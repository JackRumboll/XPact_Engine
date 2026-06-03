// Copyright Simgenics. All Rights Reserved.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Body-lowering rule for an explicit C# cast expression
/// (<see cref="CastExpressionSyntax"/>, e.g. <c>(Foo)x</c>) per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5 (expression mapping). It
/// lowers to a C++ <c>static_cast&lt;Target&gt;(&lt;e&gt;)</c>: the operand is
/// recursed through the parent expression emitter so its own lowering applies,
/// and the target type is rendered through <see cref="CppTypeName"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>XObject-derived target.</b> An engine reference type is a raw pointer in
/// the C++ object model, so the target renders as <c>::Ns::T*</c> (via
/// <see cref="CppTypeName.RenderReference"/>) and the cast stays a
/// <c>static_cast</c> -- an up/down-cast within the single-inheritance XObject
/// hierarchy is statically valid; a checked dynamic test is the
/// <c>is</c>-pattern rule's job, not the unconditional-cast rule's.
/// </para>
/// <para>
/// <b>Value / primitive target.</b> Renders the fixed-width C++ spelling
/// (<c>int32_t</c>, <c>float</c>, ...) and a by-value <c>static_cast</c>.
/// </para>
/// <para>
/// <b>Determinism.</b> Pure structural lowering; no clock / culture / random
/// (gate X-IL2CPP-CSPATH-DET).
/// </para>
/// </remarks>
public sealed class CastLoweringRule : IBodyLoweringRule
{
    /// <inheritdoc/>
    public string Name => "Cast";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node) => node is CastExpressionSyntax;

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        System.ArgumentNullException.ThrowIfNull(node);
        System.ArgumentNullException.ThrowIfNull(context);
        System.ArgumentNullException.ThrowIfNull(writer);
        System.ArgumentNullException.ThrowIfNull(parent);

        CastExpressionSyntax cast = (CastExpressionSyntax)node;

        SemanticModel model = context.GetSemanticModel(cast.SyntaxTree);
        ITypeSymbol? targetType = model.GetTypeInfo(cast.Type).Type;
        string target = CppTypeName.RenderReference(targetType);

        writer.Append("static_cast<");
        writer.Append(target);
        writer.Append(">(");
        parent.Expressions.EmitExpression(cast.Expression);
        writer.Append(")");
    }
}
