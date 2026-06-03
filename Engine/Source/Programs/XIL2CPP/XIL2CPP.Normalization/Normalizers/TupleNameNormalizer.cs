// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Frontend;

namespace Simgenics.XPact.XIL2CPP.Normalization;

/// <summary>
/// Pass-2 normalizer that lowers C# tuple element names to their canonical
/// positional <c>Item-N</c> form per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 5.7 ("Tuple literals").
/// </summary>
/// <remarks>
/// <para>
/// Tuples emit as positional anonymous C++ structs whose members are
/// <c>Item1</c>, <c>Item2</c>, ... (the .NET IL convention). The C# field
/// names in a tuple declaration (e.g. <c>(int X, int Y)</c>) are name-aliases
/// only; the emitted C++ struct does NOT carry them, and two C# tuples of the
/// same shape with different field names map to the SAME C++ struct. This
/// normalizer therefore resolves every use-site that refers to a tuple element
/// by a friendly name (<c>t.X</c>) -- and every positional binding the C++
/// structured-binding emit must order (deconstruction targets, positional
/// <c>t.Item1</c> accesses) -- to the canonical positional index + <c>Item-N</c>
/// name, recording the decision as an additive
/// <see cref="TupleNameAnnotation"/> on the original Roslyn node. It does NOT
/// rewrite syntax; the Pass-1 binding info stays authoritative for Pass 3.
/// </para>
/// <para>
/// Three node shapes are annotated, in deterministic source order (parsed-file
/// order, then document order within each tree):
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Member-access element reads</b> -- a <see cref="MemberAccessExpressionSyntax"/>
/// (<c>t.X</c> or <c>t.Item1</c>) whose accessed member binds to a tuple
/// element field. The field's <see cref="IFieldSymbol.CorrespondingTupleField"/>
/// gives the canonical positional field (<c>Item1</c>); its position within the
/// containing tuple type's <see cref="INamedTypeSymbol.TupleElements"/> gives the
/// 1-based index.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Tuple-literal named elements</b> -- an <see cref="ArgumentSyntax"/> inside a
/// <see cref="TupleExpressionSyntax"/> that carries an explicit
/// <c>Name:</c> colon (<c>(X: 5, Y: "hi")</c>). Its ordinal position in the
/// literal is the canonical index.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Deconstruction targets</b> -- each variable designation inside a
/// <see cref="ParenthesizedVariableDesignationSyntax"/> (<c>var (a, b) = t</c>),
/// which the C++ structured binding must bind to <c>Item1</c>, <c>Item2</c>,
/// ... positionally.
/// </description>
/// </item>
/// </list>
/// </remarks>
public sealed class TupleNameNormalizer : INormalizer
{
    /// <inheritdoc />
    public string Name => "TupleNameNormalizer";

    /// <inheritdoc />
    public void Normalize(Pass1Result pass1, NormalizedUnitBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(pass1);
        ArgumentNullException.ThrowIfNull(builder);

        // Deterministic traversal: parsed-file order (the Pass-1 canonical
        // ordinal order), then document order within each tree. No ambient
        // state, no hash ordering.
        foreach (ModuleParser.ParsedFile parsed in pass1.ParsedFiles)
        {
            SemanticModel model = pass1.GetSemanticModel(parsed.Tree);
            SyntaxNode root = parsed.Tree.GetRoot();

            foreach (SyntaxNode node in root.DescendantNodes())
            {
                switch (node)
                {
                    case MemberAccessExpressionSyntax memberAccess:
                        TryAnnotateMemberAccess(memberAccess, model, builder);
                        break;

                    case TupleExpressionSyntax tupleLiteral:
                        AnnotateTupleLiteralElements(tupleLiteral, model, builder);
                        break;

                    case ParenthesizedVariableDesignationSyntax deconstruction:
                        AnnotateDeconstructionTargets(deconstruction, builder);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Annotate a <c>t.X</c> / <c>t.Item1</c> element read with its canonical
    /// positional index + <c>Item-N</c> name when the accessed member binds to
    /// a tuple element field. Non-tuple member accesses are left untouched.
    /// </summary>
    private static void TryAnnotateMemberAccess(
        MemberAccessExpressionSyntax memberAccess,
        SemanticModel model,
        NormalizedUnitBuilder builder)
    {
        // The accessed member must bind to a field that is a tuple element.
        if (model.GetSymbolInfo(memberAccess).Symbol is not IFieldSymbol field)
        {
            return;
        }

        // CorrespondingTupleField is the canonical (positional) default field
        // for any tuple-element field: for a friendly-named element (X) it is
        // the matching default field (Item1); for a default field (Item1) it
        // is the field itself. It is null for fields that are not tuple
        // elements at all, which filters out ordinary struct/class member
        // accesses.
        IFieldSymbol? canonical = field.CorrespondingTupleField;
        if (canonical is null)
        {
            return;
        }

        if (!TryResolvePosition(field.ContainingType, canonical, out int index))
        {
            return;
        }

        builder.AnnotateNode(
            memberAccess,
            new TupleNameAnnotation(index, canonical.Name));
    }

    /// <summary>
    /// Annotate each explicitly-named element of a tuple literal
    /// (<c>(X: 5, Y: "hi")</c>) with its canonical positional index +
    /// <c>Item-N</c> name. Unnamed positional elements are already canonical
    /// and carry no friendly name to lower, so they are skipped.
    /// </summary>
    private static void AnnotateTupleLiteralElements(
        TupleExpressionSyntax tupleLiteral,
        SemanticModel model,
        NormalizedUnitBuilder builder)
    {
        SeparatedSyntaxList<ArgumentSyntax> arguments = tupleLiteral.Arguments;

        // The tuple type gives the authoritative TupleElements ordering; fall
        // back to positional argument index when the type cannot be resolved
        // (well-formed input still gets a deterministic annotation).
        INamedTypeSymbol? tupleType =
            model.GetTypeInfo(tupleLiteral).ConvertedType as INamedTypeSymbol;
        if (tupleType is not null && !tupleType.IsTupleType)
        {
            tupleType = null;
        }

        for (int i = 0; i < arguments.Count; i++)
        {
            ArgumentSyntax argument = arguments[i];
            if (argument.NameColon is null)
            {
                // Positional element: no friendly name to canonicalize.
                continue;
            }

            // Position is the element's ordinal in the literal (1-based). The
            // TupleElements list, when available, agrees by construction; the
            // ordinal is itself authoritative for a literal.
            int index = i + 1;
            string canonicalName = CanonicalElementName(tupleType, i);

            builder.AnnotateNode(
                argument,
                new TupleNameAnnotation(index, canonicalName));
        }
    }

    /// <summary>
    /// Annotate every target of a parenthesized deconstruction
    /// (<c>var (a, b) = t</c>) with the positional index + <c>Item-N</c> name
    /// the C++ structured binding must bind it to. Nested designations are
    /// handled by the outer traversal visiting them as their own
    /// <see cref="ParenthesizedVariableDesignationSyntax"/>.
    /// </summary>
    private static void AnnotateDeconstructionTargets(
        ParenthesizedVariableDesignationSyntax deconstruction,
        NormalizedUnitBuilder builder)
    {
        SeparatedSyntaxList<VariableDesignationSyntax> variables = deconstruction.Variables;
        for (int i = 0; i < variables.Count; i++)
        {
            VariableDesignationSyntax variable = variables[i];
            int index = i + 1;

            builder.AnnotateNode(
                variable,
                new TupleNameAnnotation(index, ItemName(index)));
        }
    }

    /// <summary>
    /// Resolve the 1-based position of <paramref name="canonical"/> within the
    /// containing tuple type's <see cref="INamedTypeSymbol.TupleElements"/>.
    /// Falls back to parsing the canonical <c>Item-N</c> name when the type's
    /// element list cannot be consulted.
    /// </summary>
    private static bool TryResolvePosition(
        INamedTypeSymbol? containingType,
        IFieldSymbol canonical,
        out int index)
    {
        if (containingType is { IsTupleType: true })
        {
            System.Collections.Immutable.ImmutableArray<IFieldSymbol> elements =
                containingType.TupleElements;
            for (int i = 0; i < elements.Length; i++)
            {
                if (SymbolEqualityComparer.Default.Equals(
                        elements[i].CorrespondingTupleField ?? elements[i],
                        canonical))
                {
                    index = i + 1;
                    return true;
                }
            }
        }

        // Fallback: derive the index from the canonical "ItemN" name.
        return TryParseItemIndex(canonical.Name, out index);
    }

    /// <summary>
    /// The canonical <c>Item-N</c> member name for the element at the given
    /// 0-based ordinal, preferring the resolved tuple type's element name and
    /// falling back to the synthesized <c>Item(ordinal+1)</c>.
    /// </summary>
    private static string CanonicalElementName(INamedTypeSymbol? tupleType, int ordinal)
    {
        if (tupleType is { IsTupleType: true })
        {
            System.Collections.Immutable.ImmutableArray<IFieldSymbol> elements =
                tupleType.TupleElements;
            if (ordinal >= 0 && ordinal < elements.Length)
            {
                IFieldSymbol element = elements[ordinal];
                return (element.CorrespondingTupleField ?? element).Name;
            }
        }
        return ItemName(ordinal + 1);
    }

    /// <summary>The canonical positional member name for a 1-based index.</summary>
    private static string ItemName(int oneBasedIndex) =>
        "Item" + oneBasedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Parse the trailing integer of a canonical <c>Item-N</c> name into its
    /// 1-based index. Returns false for any non-conforming name.
    /// </summary>
    private static bool TryParseItemIndex(string name, out int index)
    {
        index = 0;
        const string prefix = "Item";
        if (name.Length <= prefix.Length || !name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return int.TryParse(
            name.AsSpan(prefix.Length),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out index)
            && index >= 1;
    }
}

/// <summary>
/// The Pass-2 lowering decision for a single tuple element reference: the
/// canonical positional index (1-based) + the canonical <c>Item-N</c> member
/// name the C++ emit uses, per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.7.
/// </summary>
/// <remarks>
/// Both the integer index and the <c>Item-N</c> string are recorded (per the
/// orchestrator decision) so Pass 3 can pick whichever it needs without
/// re-deriving one from the other: the index drives positional C++
/// structured-binding ordering, and the name drives member-access emit
/// (<c>tuple.X</c> to <c>tuple.Item1</c>).
/// </remarks>
public sealed class TupleNameAnnotation : LoweredAnnotation
{
    /// <summary>
    /// Construct a tuple-name lowering annotation.
    /// </summary>
    /// <param name="positionalIndex">The canonical 1-based positional index of the element. Must be &gt;= 1.</param>
    /// <param name="canonicalMemberName">The canonical <c>Item-N</c> member name (e.g. <c>"Item1"</c>). Must not be null / empty / whitespace.</param>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="positionalIndex"/> is less than 1.</exception>
    /// <exception cref="ArgumentException">If <paramref name="canonicalMemberName"/> is null / empty / whitespace.</exception>
    public TupleNameAnnotation(int positionalIndex, string canonicalMemberName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(positionalIndex, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalMemberName);

        PositionalIndex = positionalIndex;
        CanonicalMemberName = canonicalMemberName;
    }

    /// <inheritdoc />
    public override string Kind => "tuple-name";

    /// <summary>
    /// The canonical 1-based positional index of the tuple element (element 1
    /// maps to <c>Item1</c>). Drives positional C++ structured-binding order.
    /// </summary>
    public int PositionalIndex { get; }

    /// <summary>
    /// The canonical <c>Item-N</c> C++ member name for the element (e.g.
    /// <c>"Item1"</c>). Drives member-access emit (<c>tuple.X</c> to
    /// <c>tuple.Item1</c>).
    /// </summary>
    public string CanonicalMemberName { get; }
}
