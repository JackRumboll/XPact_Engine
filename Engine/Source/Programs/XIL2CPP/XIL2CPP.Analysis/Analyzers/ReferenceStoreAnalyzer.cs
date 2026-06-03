// Copyright Simgenics. All Rights Reserved.

using System;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using DiagnosticSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Analysis;

/// <summary>
/// Pass-3 reference-store-site enumeration per <c>/Documents/XIL2CPP.html</c>
/// Rev 4 Sections 6.3 (write-barrier <c>XPACT_GC_STORE</c> emission) and 5.3
/// (multi-step reference write lowering). Walks every
/// <see cref="AssignmentExpressionSyntax"/> whose left-hand side resolves to a
/// reference-to-XObject field / property slot (the slot's <em>type</em> is
/// <see cref="AnalyzerHelpers.IsXObjectDerived(INamedTypeSymbol?)"/>) and
/// records a <see cref="ReferenceStoreSite"/> for the later Pass-6 barrier
/// emit. A chained write through an intermediate XObject reference link
/// (<c>a.b.c = x</c>, where the slot's receiver is itself reached through an
/// <c>XPtr&lt;T&gt;</c> intermediate) is rejected with
/// <see cref="DiagnosticCodes.ChainedWriteThroughXPtrUndefined"/>
/// (<c>XIL2CPP063</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is recorded.</b> Only direct <see cref="AssignmentExpressionSyntax"/>
/// stores into an XObject-reference field / property are this analyzer's
/// concern (Section 6.3 enumeration item 1). The lowered reference-store forms
/// (object-initializer setters, deconstruction, container Add/Set, etc.) are
/// out of scope here. Value-typed field stores and local-variable stores are
/// NOT reference stores and are not recorded.
/// </para>
/// <para>
/// <b>Chained-write detection.</b> Per Section 5.3, <c>outer.mid.inner.actor =
/// x</c> lowers to <c>Raw()</c>-promoted reads on each intermediate
/// <c>XPtr&lt;T&gt;</c> followed by the final barriered store; if an
/// intermediate XPtr is reached by-value rather than via a stable
/// <c>Raw()</c> promotion the chain is undefined. The analyzer flags any
/// reference store whose slot receiver is itself a member access resolving to
/// an XObject reference (a multi-link reference chain) with
/// <c>XIL2CPP063</c>. The site is still recorded (the emit metadata is needed
/// even for the diagnosed chain).
/// </para>
/// <para>
/// <b>Determinism.</b> Sites are visited in deterministic order: the Pass-1
/// canonical parsed-file order, then document order within each tree
/// (the Roslyn <c>DescendantNodesAndSelf</c> pre-order walk). No ambient
/// state, <c>DateTime</c>, or <c>Random</c>. Gate X-IL2CPP-CSPATH-DET.
/// </para>
/// </remarks>
public sealed class ReferenceStoreAnalyzer : ISemanticAnalyzer
{
    /// <summary>
    /// Display format for the slot / containing-method symbols: fully
    /// namespace-qualified containing types plus the member name, so the
    /// recorded display string is unambiguous across types that share a
    /// simple name (the emit metadata keys on it). Deterministic -- no
    /// culture or ambient state.
    /// </summary>
    private static readonly SymbolDisplayFormat s_memberFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <inheritdoc/>
    public string Name => "ReferenceStoreAnalyzer";

    /// <inheritdoc/>
    public void Analyze(NormalizedUnit unit, Pass3ResultBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(builder);

        Pass1Result pass1 = unit.Pass1;

        foreach (ModuleParser.ParsedFile parsed in pass1.ParsedFiles)
        {
            SemanticModel model = pass1.GetSemanticModel(parsed.Tree);
            SyntaxNode root = parsed.Tree.GetRoot();

            foreach (SyntaxNode node in root.DescendantNodesAndSelf())
            {
                if (node is AssignmentExpressionSyntax assignment)
                {
                    AnalyzeAssignment(unit, model, parsed, assignment, builder);
                }
            }
        }
    }

    /// <summary>
    /// Inspect one assignment: if the LHS resolves to a reference-to-XObject
    /// field / property slot, record a <see cref="ReferenceStoreSite"/> and,
    /// when the slot is reached through an intermediate XObject reference
    /// link, emit <c>XIL2CPP063</c>.
    /// </summary>
    private static void AnalyzeAssignment(
        NormalizedUnit unit,
        SemanticModel model,
        ModuleParser.ParsedFile parsed,
        AssignmentExpressionSyntax assignment,
        Pass3ResultBuilder builder)
    {
        ExpressionSyntax lhs = assignment.Left;

        // The slot symbol the store targets. Only a field or property can be a
        // reference-store slot; locals / parameters / discards never are.
        ISymbol? slot = model.GetSymbolInfo(lhs).Symbol;
        ITypeSymbol? slotType = slot switch
        {
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            _ => null,
        };

        // The stored value's slot type must itself be an XObject-derived
        // reference for this to be a reference store. A value-typed field
        // (int, a struct, etc.) is not a reference store.
        if (slotType is not INamedTypeSymbol namedSlotType
            || !AnalyzerHelpers.IsXObjectDerived(namedSlotType))
        {
            return;
        }

        bool isCompound = assignment.Kind() != SyntaxKind.SimpleAssignmentExpression;
        bool isChained = IsMultiLinkReferenceChain(model, lhs);

        FileLinePositionSpan lineSpan = lhs.GetLocation().GetLineSpan();
        SourceSpan span = SourceSpan.Point(
            parsed.AbsolutePath,
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1);

        IMethodSymbol? containingMethod = FindContainingMethod(model, assignment);

        builder.Add(new ReferenceStoreSite(
            SlotSymbolDisplay: slot!.ToDisplayString(s_memberFormat),
            SlotKind: slot is IFieldSymbol ? ReferenceSlotKind.Field : ReferenceSlotKind.Property,
            ContainingMethodDisplay: containingMethod?.ToDisplayString(s_memberFormat),
            Span: span,
            IsCompound: isCompound,
            IsChainedWrite: isChained));

        if (isChained)
        {
            string message = string.Format(
                CultureInfo.InvariantCulture,
                "chained write through XPtr<T> intermediate is undefined; assign '{0}' to a stable local first",
                ReceiverDisplay(lhs));

            builder.AddDiagnostic(span.ToDiagnostic(
                DiagnosticSeverity.Error,
                DiagnosticCodes.ChainedWriteThroughXPtrUndefined,
                message,
                unit.Pass1.ModuleName));
        }
    }

    /// <summary>
    /// Return true iff <paramref name="lhs"/> is a member access whose
    /// receiver is itself a member access that resolves to an XObject-derived
    /// reference -- i.e. the final slot is reached through an intermediate
    /// <c>XPtr&lt;T&gt;</c> link (a multi-link reference chain
    /// <c>a.b.c = x</c>). A single-link write (<c>a.c = x</c> where <c>a</c>
    /// is a local, parameter, or <c>this</c>) is NOT chained.
    /// </summary>
    private static bool IsMultiLinkReferenceChain(SemanticModel model, ExpressionSyntax lhs)
    {
        if (lhs is not MemberAccessExpressionSyntax memberAccess)
        {
            return false;
        }

        // The receiver of the final slot. For a multi-link reference chain it
        // must itself be a member access that reads an intermediate XObject
        // reference (the XPtr the doc's Section 5.3 example promotes via
        // Raw()). A plain identifier / `this` receiver is a single link.
        if (memberAccess.Expression is not MemberAccessExpressionSyntax intermediate)
        {
            return false;
        }

        ITypeSymbol? intermediateType = model.GetTypeInfo(intermediate).Type;
        return intermediateType is INamedTypeSymbol namedIntermediate
            && AnalyzerHelpers.IsXObjectDerived(namedIntermediate);
    }

    /// <summary>
    /// Render the receiver expression of a member-access LHS for the
    /// diagnostic message, or the whole LHS when it is not a member access.
    /// </summary>
    private static string ReceiverDisplay(ExpressionSyntax lhs)
        => lhs is MemberAccessExpressionSyntax memberAccess
            ? memberAccess.Expression.ToString()
            : lhs.ToString();

    /// <summary>
    /// Resolve the method symbol that lexically contains
    /// <paramref name="node"/> from the authoritative semantic model, or null
    /// when the assignment is not inside a method body (e.g. a field
    /// initializer). Walks up to the nearest enclosing member declaration.
    /// </summary>
    private static IMethodSymbol? FindContainingMethod(SemanticModel model, SyntaxNode node)
    {
        for (SyntaxNode? current = node; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case MethodDeclarationSyntax method:
                    return model.GetDeclaredSymbol(method);
                case ConstructorDeclarationSyntax ctor:
                    return model.GetDeclaredSymbol(ctor);
                case AccessorDeclarationSyntax accessor:
                    return model.GetDeclaredSymbol(accessor) as IMethodSymbol;
                case LocalFunctionStatementSyntax local:
                    return model.GetDeclaredSymbol(local) as IMethodSymbol;
                case OperatorDeclarationSyntax op:
                    return model.GetDeclaredSymbol(op);
                case ConversionOperatorDeclarationSyntax conv:
                    return model.GetDeclaredSymbol(conv);
                case DestructorDeclarationSyntax dtor:
                    return model.GetDeclaredSymbol(dtor);
            }
        }
        return null;
    }
}

/// <summary>
/// The kind of slot a <see cref="ReferenceStoreSite"/> targets: an instance /
/// static field, or a property (whose setter / backing field carries the
/// write barrier).
/// </summary>
public enum ReferenceSlotKind
{
    /// <summary>A field slot (<c>obj.Field = x</c>).</summary>
    Field,

    /// <summary>A property slot (<c>obj.Prop = x</c>).</summary>
    Property,
}

/// <summary>
/// One recorded reference-store site per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 6.3: an <see cref="AssignmentExpressionSyntax"/> whose LHS resolves
/// to a reference-to-XObject field / property slot. Pass 6 emits an
/// <c>XPACT_GC_STORE(parent_obj, &amp;slot, new_value)</c> write barrier at each
/// site recorded here.
/// </summary>
/// <param name="SlotSymbolDisplay">
/// The fully-qualified display string of the field / property slot the store
/// targets (the directly-enclosing object's reference slot).
/// </param>
/// <param name="SlotKind">Whether the slot is a field or a property.</param>
/// <param name="ContainingMethodDisplay">
/// The fully-qualified display string of the method / accessor / constructor
/// that lexically contains the store, or null when the store is outside any
/// method body (e.g. a field initializer).
/// </param>
/// <param name="Span">The 1-based source span of the assignment's left-hand side.</param>
/// <param name="IsCompound">
/// True for a compound assignment (<c>+=</c>, <c>??=</c>, etc.) whose final
/// reassignment to the slot is the barriered store; false for a simple
/// <c>=</c> store.
/// </param>
/// <param name="IsChainedWrite">
/// True iff the slot is reached through an intermediate XObject reference link
/// (<c>a.b.c = x</c>); such a site also carries an <c>XIL2CPP063</c>
/// diagnostic.
/// </param>
public sealed record ReferenceStoreSite(
    string SlotSymbolDisplay,
    ReferenceSlotKind SlotKind,
    string? ContainingMethodDisplay,
    SourceSpan Span,
    bool IsCompound,
    bool IsChainedWrite);
