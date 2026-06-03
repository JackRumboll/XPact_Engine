// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// The shared recognizer for the <c>XObject.New&lt;T&gt;(outer, name, flags)</c>
/// static factory intrinsic, used by both
/// <see cref="XObjectNewLoweringRule"/> (which lowers it) and
/// <see cref="InvocationLoweringRule"/> (which DEFERS to it, so the general
/// invocation rule never lowers the factory to a non-existent mangled body).
/// Centralizing the recognition in one place keeps the two rules' notion of
/// "is this an XObject.New call" byte-identical, per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Recognition is symbol-driven, not name-driven.</b> The factory is
/// identified by its resolved <see cref="IMethodSymbol"/>: a generic method
/// named <c>New</c> (arity 1) whose containing type is named <c>XObject</c>.
/// Matching on the bound symbol (rather than the textual receiver) means an
/// aliased or fully-qualified spelling of the call resolves identically.
/// </para>
/// <para>
/// <b>Determinism.</b> The render is a pure function of the symbol (ordinal
/// component ordering, invariant rendering, no clock / culture / random) per
/// gate X-IL2CPP-CSPATH-DET.
/// </para>
/// </remarks>
public static class XObjectNewRecognizer
{
    /// <summary>The C# factory method's simple name (<c>XObject.New&lt;T&gt;</c>).</summary>
    public const string FactoryMethodName = "New";

    /// <summary>The C# factory method's containing type's simple name.</summary>
    public const string FactoryContainingTypeName = "XObject";

    /// <summary>The XObject runtime C++ type-rename prefix (a C# <c>HealthPickup</c> becomes <c>XHealthPickup</c>).</summary>
    public const string XTypePrefix = "X";

    /// <summary>
    /// True iff <paramref name="invocation"/> binds to the
    /// <c>XObject.New&lt;T&gt;</c> static factory.
    /// </summary>
    /// <param name="invocation">The invocation node to test. Must not be null.</param>
    /// <param name="model">The semantic model for the invocation's tree. Must not be null.</param>
    /// <returns>True iff the invocation is the factory.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="invocation"/> or <paramref name="model"/> is null.</exception>
    public static bool IsXObjectNew(InvocationExpressionSyntax invocation, SemanticModel model)
        => TryMatch(invocation, model, out _);

    /// <summary>
    /// Try to bind <paramref name="invocation"/> to the
    /// <c>XObject.New&lt;T&gt;</c> static factory, returning the resolved
    /// generic method symbol (with its single type argument) on success.
    /// </summary>
    /// <param name="invocation">The invocation node to test. Must not be null.</param>
    /// <param name="model">The semantic model for the invocation's tree. Must not be null.</param>
    /// <param name="method">Receives the bound factory method symbol on success; null otherwise.</param>
    /// <returns>True iff the invocation is the factory.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="invocation"/> or <paramref name="model"/> is null.</exception>
    public static bool TryMatch(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        out IMethodSymbol? method)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(model);

        method = null;

        SymbolInfo info = model.GetSymbolInfo(invocation);
        if (info.Symbol is not IMethodSymbol candidate)
        {
            return false;
        }

        // A generic static method named New, arity 1, on a type named XObject.
        if (!candidate.IsStatic
            || !candidate.IsGenericMethod
            || candidate.TypeArguments.Length != 1
            || !StringComparer.Ordinal.Equals(candidate.Name, FactoryMethodName)
            || candidate.ContainingType is null
            || !StringComparer.Ordinal.Equals(candidate.ContainingType.Name, FactoryContainingTypeName))
        {
            return false;
        }

        method = candidate;
        return true;
    }

    /// <summary>
    /// Render the runtime C++ type name for an XObject-derived C# type argument
    /// <paramref name="type"/>: the fully-qualified <c>::</c>-separated
    /// namespace + nested-type path with the XObject <see cref="XTypePrefix"/>
    /// on the leaf type name (so <c>Game.HealthPickup</c> renders
    /// <c>::Game::XHealthPickup</c>).
    /// </summary>
    /// <param name="type">The C# type argument. Must not be null.</param>
    /// <returns>The fully-qualified, X-prefixed C++ type name.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="type"/> is null.</exception>
    public static string RenderXType(ITypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);

        // A type parameter (e.g. a generic method's own T) cannot be renamed to
        // a closed runtime type at this layer; render its name with the prefix
        // so the form is still well-shaped and deterministic.
        if (type is ITypeParameterSymbol typeParameter)
        {
            return XTypePrefix + typeParameter.Name;
        }

        var sb = new StringBuilder();

        // Namespace path (outermost first), each segment :: separated, leading ::.
        INamespaceSymbol? ns = type.ContainingNamespace;
        List<string> nsParts = new();
        for (INamespaceSymbol? cursor = ns;
             cursor is not null && !cursor.IsGlobalNamespace;
             cursor = cursor.ContainingNamespace)
        {
            nsParts.Add(cursor.Name);
        }
        for (int i = nsParts.Count - 1; i >= 0; i--)
        {
            sb.Append("::");
            sb.Append(nsParts[i]);
        }

        // Containing-type chain (outermost first), each :: separated. Only the
        // LEAF (the type itself) carries the X rename prefix; enclosing types
        // keep their C++ names verbatim (they are the C++ scope, not the
        // constructed type).
        List<INamedTypeSymbol> typeChain = new();
        for (INamedTypeSymbol? t = type.ContainingType; t is not null; t = t.ContainingType)
        {
            typeChain.Add(t);
        }
        for (int i = typeChain.Count - 1; i >= 0; i--)
        {
            sb.Append("::");
            sb.Append(typeChain[i].Name);
        }

        sb.Append("::");
        sb.Append(XTypePrefix);
        sb.Append(type.Name);

        return sb.ToString();
    }
}
