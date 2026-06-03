// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Analysis;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Body-lowering rule for a C# object-creation expression
/// (<see cref="ObjectCreationExpressionSyntax"/>, e.g. <c>new T(args)</c>) on a
/// NON-XObject type per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5. A value
/// type lowers to brace initialization <c>T{args}</c>; a non-XObject reference
/// type lowers to the constructor call <c>T(args)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>XObject-derived <c>new</c> is NOT lowered here.</b> Constructing an
/// XObject-derived type with <c>new</c> is banned by Locked Commitment 3 and is
/// XIL2CPP001 territory (the <see cref="NewExpressionAnalyzer"/> raised it in
/// Pass 3). If such a node still reaches this rule, it emits a deliberate
/// <c>// DEFERRED(6.e): ...</c> comment rather than fabricating a construction;
/// the only sanctioned construction site is the <c>XObject.New&lt;T&gt;</c>
/// factory.
/// </para>
/// <para>
/// <b>Value vs reference target.</b> A value type (<c>struct</c>, enum, the
/// primitive value types) renders <c>T{args}</c> (brace initialization, which is
/// well-defined for both aggregate and constructor cases in C++). A non-XObject
/// reference type renders the constructor call <c>T(args)</c>. The element /
/// object initializer (<c>new T { X = 1 }</c>) is a richer lowering owned by a
/// later wave; when present the rule emits a DEFERRED note and the bare
/// construction.
/// </para>
/// <para>
/// <b>Determinism.</b> Pure structural lowering; arguments recurse through the
/// parent expression emitter in source order; no clock / culture / random
/// (gate X-IL2CPP-CSPATH-DET).
/// </para>
/// </remarks>
public sealed class ObjectCreationLoweringRule : IBodyLoweringRule
{
    /// <inheritdoc/>
    public string Name => "ObjectCreation";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node) => node is ObjectCreationExpressionSyntax;

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        ObjectCreationExpressionSyntax creation = (ObjectCreationExpressionSyntax)node;

        SemanticModel model = context.GetSemanticModel(creation.SyntaxTree);
        ITypeSymbol? createdType = model.GetTypeInfo(creation).Type;

        if (createdType is INamedTypeSymbol named && AnalyzerHelpers.IsXObjectDerived(named))
        {
            // Locked Commitment 3: XObject construction goes through the factory.
            // This should have been XIL2CPP001 in Pass 3; never fabricate a ctor.
            writer.AppendComment(
                $"DEFERRED(6.e): new on XObject-derived type '{named.Name}' is XIL2CPP001 territory; use XObject.New<{named.Name}>");
            return;
        }

        if (creation.Initializer is not null)
        {
            writer.AppendComment(
                "DEFERRED(6.e): object / collection initializer not yet lowered; emitting bare construction");
        }

        string target = CppTypeName.Render(createdType);
        bool isValueType = createdType is { IsValueType: true };

        // Value type -> brace init 'T{args}'; reference type -> ctor call 'T(args)'.
        writer.Append(target);
        writer.Append(isValueType ? "{" : "(");
        EmitArguments(creation.ArgumentList, parent);
        writer.Append(isValueType ? "}" : ")");
    }

    /// <summary>
    /// Emit the comma-separated argument list (each argument's expression
    /// recursed through the parent emitter in source order). A null /
    /// empty list emits nothing (an empty <c>{}</c> / <c>()</c>).
    /// </summary>
    private static void EmitArguments(ArgumentListSyntax? argumentList, StatementEmitter parent)
    {
        if (argumentList is null)
        {
            return;
        }

        bool first = true;
        foreach (ArgumentSyntax argument in argumentList.Arguments)
        {
            if (!first)
            {
                parent.Writer.Append(", ");
            }
            first = false;
            parent.Expressions.EmitExpression(argument.Expression);
        }
    }
}
