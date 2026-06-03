// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;

namespace Simgenics.XPact.XIL2CPP.Analysis;

/// <summary>
/// Shared, stateless helpers multiple Pass-3 analyzers need per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2: XObject-derivation
/// detection (the <c>XIL2CPP001</c> / container / write-barrier analyzers
/// all need it) and deterministic enumeration of the unit's member
/// declarations + their owning trees.
/// </summary>
public static class AnalyzerHelpers
{
    /// <summary>
    /// The fully-qualified name of the engine root reference type, as
    /// <see cref="ISymbol.ToDisplayString(SymbolDisplayFormat)"/> renders it
    /// with <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/>.
    /// </summary>
    private const string XObjectFullyQualifiedName = "global::XPact.CoreXObject.XObject";

    /// <summary>The metadata name of the engine root reference type.</summary>
    private const string XObjectMetadataName = "XObject";

    /// <summary>The containing namespace of the engine root reference type.</summary>
    private const string XObjectNamespace = "XPact.CoreXObject";

    /// <summary>
    /// Return true iff <paramref name="type"/> transitively derives from the
    /// engine root reference type <c>XPact.CoreXObject.XObject</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Walks the <see cref="INamedTypeSymbol"/> base-type chain upward. A base matches
    /// when EITHER its
    /// <see cref="ISymbol.ToDisplayString(SymbolDisplayFormat)"/> with
    /// <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/> equals
    /// <c>"global::XPact.CoreXObject.XObject"</c> OR its
    /// <see cref="ISymbol.MetadataName"/> is <c>"XObject"</c> AND its
    /// containing namespace is <c>"XPact.CoreXObject"</c>. The metadata-name
    /// fallback makes the check work even when the curated XObject BCL ref is
    /// absent (Phase 6.b tests bind against
    /// <c>Basic.Reference.Assemblies.Net80</c> with a locally-declared
    /// <c>abstract class XObject</c>, NOT the real engine BCL).
    /// </para>
    /// <para>The type itself being <c>XObject</c> does NOT count as derived.</para>
    /// </remarks>
    /// <param name="type">The type to test. May be null (returns false).</param>
    /// <returns>True iff a transitive base type is the engine <c>XObject</c>.</returns>
    public static bool IsXObjectDerived(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        for (INamedTypeSymbol? baseType = type.BaseType;
             baseType is not null;
             baseType = baseType.BaseType)
        {
            if (IsXObjectType(baseType))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Return true iff <paramref name="type"/> IS the engine root reference
    /// type <c>XPact.CoreXObject.XObject</c> itself (not a derived type).
    /// Uses the same dual fully-qualified / metadata-name match
    /// <see cref="IsXObjectDerived"/> uses for its base-walk.
    /// </summary>
    /// <param name="type">The type to test. May be null (returns false).</param>
    /// <returns>True iff the type is the engine <c>XObject</c>.</returns>
    public static bool IsXObjectType(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        if (type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == XObjectFullyQualifiedName)
        {
            return true;
        }

        // Metadata-name fallback: works when the real BCL ref is absent and
        // the type is a locally-declared stand-in.
        return type.MetadataName == XObjectMetadataName
            && type.ContainingNamespace is { IsGlobalNamespace: false } ns
            && ns.ToDisplayString() == XObjectNamespace;
    }

    /// <summary>
    /// Enumerate every <see cref="MemberDeclarationSyntax"/> across the
    /// unit's parsed trees in a deterministic order (tree order -- the
    /// Pass-1 parsed-file canonical ordinal order -- then document order
    /// within each tree, parents before nested children). Multiple Pass-3
    /// analyzers (container-site, reference-store, lambda-capture, etc.)
    /// walk member declarations; centralising the walk keeps the order
    /// identical across analyzers and across machines.
    /// </summary>
    /// <param name="unit">The normalized unit to enumerate. Must not be null.</param>
    /// <returns>A lazily-evaluated, deterministically-ordered member-declaration sequence.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="unit"/> is null.</exception>
    public static IEnumerable<MemberDeclarationSyntax> EnumerateMemberDeclarations(NormalizedUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);

        foreach (ModuleParser.ParsedFile parsed in unit.Pass1.ParsedFiles)
        {
            SyntaxNode root = parsed.Tree.GetRoot();
            foreach (SyntaxNode node in root.DescendantNodesAndSelf())
            {
                if (node is MemberDeclarationSyntax member)
                {
                    yield return member;
                }
            }
        }
    }

    /// <summary>
    /// Enumerate every named type declared in the unit's parsed trees in a
    /// deterministic order (tree order then document order), pairing each
    /// declaration with the resolved <see cref="INamedTypeSymbol"/> from the
    /// authoritative Pass-1 semantic model. Declarations whose symbol cannot
    /// be resolved (e.g. an error type) are skipped. The
    /// <c>XIL2CPP001</c> / layout / container analyzers all start from the
    /// declared types.
    /// </summary>
    /// <param name="unit">The normalized unit to enumerate. Must not be null.</param>
    /// <returns>A lazily-evaluated, deterministically-ordered (declaration, symbol) sequence.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="unit"/> is null.</exception>
    public static IEnumerable<(TypeDeclarationSyntax Declaration, INamedTypeSymbol Symbol)>
        EnumerateTypeDeclarations(NormalizedUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);

        Pass1Result pass1 = unit.Pass1;
        foreach (ModuleParser.ParsedFile parsed in pass1.ParsedFiles)
        {
            SemanticModel model = pass1.GetSemanticModel(parsed.Tree);
            SyntaxNode root = parsed.Tree.GetRoot();
            foreach (SyntaxNode node in root.DescendantNodesAndSelf())
            {
                if (node is TypeDeclarationSyntax decl
                    && model.GetDeclaredSymbol(decl) is INamedTypeSymbol symbol)
                {
                    yield return (decl, symbol);
                }
            }
        }
    }
}
