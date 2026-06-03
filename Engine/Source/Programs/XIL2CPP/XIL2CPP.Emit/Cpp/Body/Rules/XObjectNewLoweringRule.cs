// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Lowers the <c>XObject.New&lt;T&gt;(outer, name, flags)</c> static factory
/// call to the C++ reflection-runtime construction call per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.3 (object creation for
/// XObject-derived):
/// <c>::XCore::Reflect::NewObject&lt;X&lt;T&gt;&gt;(outer, name, flags)</c>.
/// On a sim-path module (<see cref="EmitContext.IsSimPath"/>) the lowering
/// emits the inline belt-and-suspenders guard immediately before the call:
/// <c>XPACT_CHECK_SL(::XCore::HAL::IsSimPathThread() || !::XCore::HAL::IsSimPathTU(), "...");</c>
/// (per Section 1.4 + Section 5.1's per-call obligation list).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a dedicated rule (not the general invocation rule).</b> The general
/// <see cref="InvocationLoweringRule"/> lowers a method call to its mangled
/// linker symbol. The <c>XObject.New&lt;T&gt;</c> factory has NO transpiled
/// body of its own; it is a BCL-surface intrinsic the spec maps directly to the
/// runtime's <c>NewObject&lt;T'&gt;</c> template. Both rules match an
/// <see cref="InvocationExpressionSyntax"/>, so <see cref="InvocationLoweringRule"/>
/// explicitly defers to <see cref="XObjectNewRecognizer.IsXObjectNew"/> (the
/// dispatch order is by <see cref="Name"/> ordinal, where
/// <c>"InvocationLowering"</c> sorts before <c>"XObjectNewLowering"</c>, so the
/// general rule MUST exclude this shape itself rather than rely on ordering).
/// </para>
/// <para>
/// <b>The <c>X&lt;T&gt;</c> rename.</b> The runtime C++ type for a C# XObject
/// type is the type's name with the XObject <c>X</c> prefix (the spec writes
/// the closed instantiation as <c>NewObject&lt;XHealthPickup&gt;</c> for a C#
/// <c>HealthPickup</c>). <see cref="XObjectNewRecognizer.RenderXType"/> renders
/// the fully-qualified <c>::</c>-separated C++ type name with that prefix on the
/// leaf type name, deterministically (ordinal ordering; invariant culture; no
/// ambient state) per gate X-IL2CPP-CSPATH-DET.
/// </para>
/// </remarks>
public sealed class XObjectNewLoweringRule : IBodyLoweringRule
{
    /// <summary>The sim-path inline construction guard condition (verbatim per Section 5.3).</summary>
    public const string SimPathGuardCondition =
        "::XCore::HAL::IsSimPathThread() || !::XCore::HAL::IsSimPathTU()";

    /// <summary>The sim-path inline construction guard message (verbatim per Section 5.3).</summary>
    public const string SimPathGuardMessage =
        "sim-path NewObject called from non-SimPathSerialExecutor thread";

    /// <summary>The fully-qualified reflection-runtime construction template (Section 5.3).</summary>
    public const string NewObjectTemplate = "::XCore::Reflect::NewObject";

    /// <inheritdoc/>
    public string Name => "XObjectNewLowering";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node is InvocationExpressionSyntax;
    }

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        var invocation = (InvocationExpressionSyntax)node;
        SemanticModel model = context.GetSemanticModel(invocation.SyntaxTree);

        if (!XObjectNewRecognizer.TryMatch(invocation, model, out IMethodSymbol? method)
            || method is null)
        {
            // Not actually XObject.New<T>: this can only happen if the rule is
            // dispatched directly to a non-matching invocation (the registry
            // would normally pick InvocationLoweringRule). Fall back to the
            // explicit gap marker rather than emit something wrong.
            parent.EmitTodo(node);
            return;
        }

        // The single generic argument T (the C# XObject-derived type).
        ITypeSymbol typeArg = method.TypeArguments[0];
        string xType = XObjectNewRecognizer.RenderXType(typeArg);

        // Sim-path TU: emit the inline guard on its own preceding line so the
        // assertion fires before the construction call (Section 5.1 / 5.3).
        if (context.IsSimPath)
        {
            writer.Append("XPACT_CHECK_SL(");
            writer.Append(SimPathGuardCondition);
            writer.Append(", ");
            writer.Append(CppWriter.EncodeCStringLiteral(SimPathGuardMessage));
            writer.Append(");");
            writer.Append("\n");
        }

        // ::XCore::Reflect::NewObject<X<T>>(outer, name, flags)
        writer.Append(NewObjectTemplate);
        writer.Append("<");
        writer.Append(xType);
        writer.Append(">(");

        ArgumentListSyntax argList = invocation.ArgumentList;
        for (int i = 0; i < argList.Arguments.Count; i++)
        {
            if (i > 0)
            {
                writer.Append(", ");
            }
            parent.Expressions.EmitExpression(argList.Arguments[i].Expression);
        }

        writer.Append(")");
    }
}
