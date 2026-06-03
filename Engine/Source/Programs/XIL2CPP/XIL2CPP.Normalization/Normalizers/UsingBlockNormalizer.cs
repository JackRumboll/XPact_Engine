// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Frontend;

namespace Simgenics.XPact.XIL2CPP.Normalization;

/// <summary>
/// Pass-2 normalizer that lowers C# <c>using</c> sugar to its canonical
/// try/finally-with-<c>Dispose</c> (XScopedGuard) form per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2 (the
/// <c>using</c>-blocks bullet) and Section 5.15 (IDisposable and using).
/// </summary>
/// <remarks>
/// <para>
/// <b>What it lowers.</b> Both <c>using</c> forms collapse to the same
/// scope-guard shape:
/// </para>
/// <list type="bullet">
///   <item><description>
///     The <em>statement</em> form
///     <c>using (var x = e) { body }</c> (or the resource-acquisition form
///     <c>using (e) { body }</c>) lowers to
///     <c>{ var x = e; try { body } finally { ((IDisposable)x).Dispose(); } }</c>.
///   </description></item>
///   <item><description>
///     The C# 8+ <em>declaration</em> form <c>using var x = e;</c> extends
///     through the end of the enclosing scope; the rest of the scope is the
///     try-body and the scope-exit runs <c>Dispose()</c>.
///   </description></item>
/// </list>
/// <para>
/// <b>What it records.</b> Pass 2 annotates; it does not rewrite. This
/// normalizer attaches one <see cref="UsingLoweringAnnotation"/> to each
/// <see cref="UsingStatementSyntax"/> and to each using-declaration
/// <see cref="LocalDeclarationStatementSyntax"/> (the
/// <c>UsingKeyword</c>-bearing local declaration), capturing the resource
/// declarations / expressions, whether it is the declaration form, and each
/// resource's resolved disposal-target type (including whether that type is
/// engine-<c>XObject</c>-derived, for which <c>Dispose()</c> maps to
/// <c>MarkForKill()</c> per Section 5.15).
/// </para>
/// <para>
/// <b>Determinism.</b> Files are visited in the Pass-1 parsed-file ordinal
/// order; nodes within a file in document order
/// (<see cref="SyntaxNode.DescendantNodesAndSelf(System.Func{SyntaxNode, bool}, bool)"/>);
/// resources within a declaration in source order. No ambient state.
/// </para>
/// </remarks>
public sealed class UsingBlockNormalizer : INormalizer
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

    /// <inheritdoc />
    public string Name => "UsingBlockNormalizer";

    /// <inheritdoc />
    public void Normalize(Pass1Result pass1, NormalizedUnitBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(pass1);
        ArgumentNullException.ThrowIfNull(builder);

        foreach (ModuleParser.ParsedFile parsed in pass1.ParsedFiles)
        {
            SemanticModel model = pass1.GetSemanticModel(parsed.Tree);
            SyntaxNode root = parsed.Tree.GetRoot();

            // Document order: parents before nested children. A nested using
            // inside another using's body is therefore annotated after its
            // enclosing using, deterministically.
            foreach (SyntaxNode node in root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case UsingStatementSyntax usingStatement:
                        AnnotateUsingStatement(usingStatement, model, builder);
                        break;

                    case LocalDeclarationStatementSyntax local when IsUsingDeclaration(local):
                        AnnotateUsingDeclaration(local, model, builder);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Annotate a <c>using (...) { body }</c> statement (declaration form
    /// <c>using (var x = e)</c> or resource-acquisition form
    /// <c>using (e)</c>).
    /// </summary>
    private void AnnotateUsingStatement(
        UsingStatementSyntax node,
        SemanticModel model,
        NormalizedUnitBuilder builder)
    {
        bool isDeclarationForm = node.Declaration is not null;
        IReadOnlyList<UsingResource> resources = node.Declaration is not null
            ? CollectDeclarationResources(node.Declaration, model)
            : CollectExpressionResource(node.Expression, model);

        builder.AnnotateNode(node, new UsingLoweringAnnotation(isDeclarationForm, resources));
    }

    /// <summary>
    /// Annotate a C# 8+ <c>using var x = e;</c> declaration statement (the
    /// <see cref="LocalDeclarationStatementSyntax"/> carrying the
    /// <c>using</c> keyword).
    /// </summary>
    private void AnnotateUsingDeclaration(
        LocalDeclarationStatementSyntax node,
        SemanticModel model,
        NormalizedUnitBuilder builder)
    {
        IReadOnlyList<UsingResource> resources = CollectDeclarationResources(node.Declaration, model);
        builder.AnnotateNode(node, new UsingLoweringAnnotation(isDeclarationForm: true, resources));
    }

    /// <summary>
    /// True iff <paramref name="local"/> is a C# 8+ <c>using</c> declaration
    /// (its <see cref="LocalDeclarationStatementSyntax.UsingKeyword"/> is
    /// present). A plain <c>var x = e;</c> local has a missing / none-kind
    /// using keyword.
    /// </summary>
    private static bool IsUsingDeclaration(LocalDeclarationStatementSyntax local)
        => !local.UsingKeyword.IsKind(SyntaxKind.None);

    /// <summary>
    /// Build the ordered resource list for a declaration form
    /// (<c>var x = e1, y = e2</c>). One <see cref="UsingResource"/> per
    /// declared variable, in source order; the disposal-target type is the
    /// declaration's type (or each initializer's resolved type when the
    /// declaration is <c>var</c>).
    /// </summary>
    private IReadOnlyList<UsingResource> CollectDeclarationResources(
        VariableDeclarationSyntax declaration,
        SemanticModel model)
    {
        // The declared type is shared across every variable in a
        // multi-declarator declaration (C# forbids `var a = e1, b = e2`
        // mixing -- `var` declarations carry a single declarator).
        ITypeSymbol? declaredType = model.GetTypeInfo(declaration.Type).Type;

        List<UsingResource> resources = new(declaration.Variables.Count);
        foreach (VariableDeclaratorSyntax declarator in declaration.Variables)
        {
            // Prefer the declared type; fall back to the initializer's type
            // when the declaration is `var` (the declared `var` resolves to
            // the initializer type anyway, but be defensive).
            ITypeSymbol? disposalType = declaredType;
            if (disposalType is null or IErrorTypeSymbol
                && declarator.Initializer is { Value: ExpressionSyntax init })
            {
                disposalType = model.GetTypeInfo(init).Type;
            }

            resources.Add(new UsingResource(
                VariableName: declarator.Identifier.ValueText,
                ResourceExpression: declarator.Initializer?.Value,
                DisposalTargetType: DisposalTypeName(disposalType),
                IsXObjectDerived: IsXObjectOrDerived(disposalType)));
        }
        return resources;
    }

    /// <summary>
    /// Build the single-element resource list for the resource-acquisition
    /// form <c>using (e) { body }</c>: there is no declared variable, only
    /// the disposable expression <paramref name="expression"/>.
    /// </summary>
    private IReadOnlyList<UsingResource> CollectExpressionResource(
        ExpressionSyntax? expression,
        SemanticModel model)
    {
        if (expression is null)
        {
            return Array.Empty<UsingResource>();
        }

        ITypeSymbol? disposalType = model.GetTypeInfo(expression).Type;
        return new[]
        {
            new UsingResource(
                VariableName: null,
                ResourceExpression: expression,
                DisposalTargetType: DisposalTypeName(disposalType),
                IsXObjectDerived: IsXObjectOrDerived(disposalType)),
        };
    }

    /// <summary>
    /// Render the disposal-target type's fully-qualified display name, or
    /// null when the type could not be resolved (an error type or a missing
    /// initializer). Used as a stable, deterministic identifier for the type
    /// whose <c>Dispose()</c> the lowering invokes.
    /// </summary>
    private static string? DisposalTypeName(ITypeSymbol? type)
    {
        if (type is null or IErrorTypeSymbol)
        {
            return null;
        }
        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    /// <summary>
    /// True iff <paramref name="type"/> IS or transitively DERIVES FROM the
    /// engine root reference type <c>XPact.CoreXObject.XObject</c>. For such
    /// targets <c>Dispose()</c> maps to <c>MarkForKill()</c> at emit per
    /// Section 5.15 (the destructor is not called; the GC reclaims the slot).
    /// </summary>
    /// <remarks>
    /// Matches on EITHER the fully-qualified display name OR the
    /// metadata-name + namespace pair, so the check still works when the
    /// curated XObject BCL ref is absent and the type is a locally-declared
    /// stand-in (the Phase 6.b tests bind against
    /// <c>Basic.Reference.Assemblies.Net80</c> plus a local
    /// <c>abstract class XObject</c>, not the real engine BCL).
    /// </remarks>
    private static bool IsXObjectOrDerived(ITypeSymbol? type)
    {
        for (INamedTypeSymbol? named = type as INamedTypeSymbol;
             named is not null;
             named = named.BaseType)
        {
            if (IsXObjectType(named))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True iff <paramref name="type"/> IS the engine root reference type
    /// <c>XPact.CoreXObject.XObject</c> itself.
    /// </summary>
    private static bool IsXObjectType(INamedTypeSymbol type)
    {
        if (type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == XObjectFullyQualifiedName)
        {
            return true;
        }

        return type.MetadataName == XObjectMetadataName
            && type.ContainingNamespace is { IsGlobalNamespace: false } ns
            && ns.ToDisplayString() == XObjectNamespace;
    }
}

/// <summary>
/// One resource a <c>using</c> construct acquires and disposes: either a
/// declared variable (<c>var x = e</c>) or a bare disposable expression
/// (<c>using (e)</c>). Records the data the try/finally-with-<c>Dispose</c>
/// lowering needs.
/// </summary>
/// <param name="VariableName">
/// The declared resource variable's identifier, or null for the
/// resource-acquisition form <c>using (e) { }</c> (no declared variable).
/// </param>
/// <param name="ResourceExpression">
/// The resource initializer / acquisition expression (the <c>e</c> in
/// <c>var x = e</c> or <c>using (e)</c>), or null when a declarator has no
/// initializer (malformed input -- recorded, not thrown on).
/// </param>
/// <param name="DisposalTargetType">
/// The fully-qualified name of the type whose <c>Dispose()</c> the lowering
/// invokes, or null when the type could not be resolved.
/// </param>
/// <param name="IsXObjectDerived">
/// True iff <see cref="DisposalTargetType"/> is or derives from the engine
/// <c>XObject</c>, for which <c>Dispose()</c> maps to <c>MarkForKill()</c>
/// per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.15.
/// </param>
public sealed record UsingResource(
    string? VariableName,
    ExpressionSyntax? ResourceExpression,
    string? DisposalTargetType,
    bool IsXObjectDerived);

/// <summary>
/// The Pass-2 lowering decision recorded on a <c>using</c> statement or a
/// <c>using</c> declaration by <see cref="UsingBlockNormalizer"/>: the
/// canonical try/finally-with-<c>Dispose</c> (XScopedGuard) form per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2 + 5.15.
/// </summary>
/// <remarks>
/// Pass 2 annotates; it does not rewrite. This annotation layers the
/// lowering decision onto the original Roslyn node so Pass 3 keeps the
/// authoritative Pass-1 binding info.
/// </remarks>
public sealed class UsingLoweringAnnotation : LoweredAnnotation
{
    /// <summary>
    /// Construct a using-lowering annotation.
    /// </summary>
    /// <param name="isDeclarationForm">
    /// True for the C# 8+ declaration form <c>using var x = e;</c> (the
    /// scope-extending form) AND for the declaration-bearing statement form
    /// <c>using (var x = e) { }</c>; false for the resource-acquisition
    /// statement form <c>using (e) { }</c>.
    /// </param>
    /// <param name="resources">
    /// The acquired resources, in source order. Must not be null.
    /// </param>
    /// <exception cref="ArgumentNullException">If <paramref name="resources"/> is null.</exception>
    public UsingLoweringAnnotation(bool isDeclarationForm, IReadOnlyList<UsingResource> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        IsDeclarationForm = isDeclarationForm;
        Resources = resources;
    }

    /// <inheritdoc />
    public override string Kind => "using-block";

    /// <summary>
    /// True iff the <c>using</c> declares its resource(s) (either the
    /// scope-extending <c>using var x = e;</c> declaration or the
    /// <c>using (var x = e) { }</c> statement). False for the
    /// resource-acquisition <c>using (e) { }</c> statement, which disposes a
    /// pre-existing expression without declaring a variable.
    /// </summary>
    public bool IsDeclarationForm { get; }

    /// <summary>
    /// The resources the construct acquires and disposes, in source order.
    /// A single <c>using</c> may acquire several (<c>using (var a = e1, b = e2)</c>).
    /// </summary>
    public IReadOnlyList<UsingResource> Resources { get; }
}
