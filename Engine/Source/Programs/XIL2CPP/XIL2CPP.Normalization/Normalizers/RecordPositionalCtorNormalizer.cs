// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Frontend;

namespace Simgenics.XPact.XIL2CPP.Normalization;

/// <summary>
/// Pass-2 normalizer for positional records and record-structs, per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2 (line 415: "Record
/// positional constructors") + Section 5.1 / 5.4 (the type-level + primary
/// constructor mappings). For each positional <c>record</c> /
/// <c>record struct</c> it makes the compiler-generated surface explicit so
/// later emit passes (Pass 6) treat every member uniformly: it records the
/// positional parameters, the synthesized init-only properties (one per
/// positional parameter, <c>param -&gt; this.Param = param</c>), the primary
/// constructor's parameter-to-property assignment list, and flags for the
/// Roslyn-generated <c>Equals</c> / <c>GetHashCode</c> / <c>Deconstruct</c> /
/// <c>ToString</c> members Pass 6 emits.
/// </summary>
/// <remarks>
/// <para>
/// <b>Annotate, do not rewrite.</b> Following the Pass-2 contract this
/// normalizer never mutates the Pass-1 trees. It attaches a
/// <see cref="SynthesizedRecordMembers"/> object to the record's
/// <see cref="INamedTypeSymbol"/> via
/// <see cref="NormalizedUnitBuilder.Attach{T}(ISymbol, T)"/> (one final value
/// per record symbol; per-symbol keying is the natural fit because the record
/// type is the thing whose synthesized members are being recorded) and it
/// annotates the originating <see cref="RecordDeclarationSyntax"/> with a
/// <see cref="RecordPositionalCtorAnnotation"/> so a node-keyed consumer
/// (Pass 6 emit walking the tree) can recover the same facts.
/// </para>
/// <para>
/// <b>Determinism.</b> Records are visited in source span order (the
/// <see cref="CSharpSyntaxWalker"/> visits in declaration order within each
/// tree, and the Pass-1 trees are themselves in canonical ordinal order).
/// Positional parameters keep their declared index; the synthesized property
/// list mirrors that order. No ambient state, <c>DateTime</c>, or <c>Random</c>.
/// </para>
/// <para>
/// <b>Scope.</b> Only <em>positional</em> records (those with a primary
/// constructor parameter list) are handled. Property-init records (no
/// parameter list) carry no compiler-synthesized positional surface and are
/// left untouched by this normalizer; an explicit member that happens to sit
/// alongside a positional parameter list (e.g. a computed property) is NOT a
/// synthesized member and is therefore excluded from the synthesized property
/// list.
/// </para>
/// </remarks>
public sealed class RecordPositionalCtorNormalizer : INormalizer
{
    /// <inheritdoc/>
    public string Name => "RecordPositionalCtorNormalizer";

    /// <inheritdoc/>
    public void Normalize(Pass1Result pass1, NormalizedUnitBuilder builder)
    {
        System.ArgumentNullException.ThrowIfNull(pass1);
        System.ArgumentNullException.ThrowIfNull(builder);

        foreach (ModuleParser.ParsedFile file in pass1.ParsedFiles)
        {
            SyntaxNode root = file.Tree.GetRoot();
            SemanticModel model = pass1.GetSemanticModel(file.Tree);

            // Walk the tree in source-declaration order. Nested records are
            // reached by the walker's recursive descent, so a record declared
            // inside another type is visited after its enclosing declaration
            // opens, preserving span order.
            RecordWalker walker = new(model, builder);
            walker.Visit(root);
        }
    }

    /// <summary>
    /// Source-order syntax walker that records the synthesized positional
    /// surface for each positional record / record-struct it encounters.
    /// </summary>
    private sealed class RecordWalker : CSharpSyntaxWalker
    {
        private readonly SemanticModel _model;
        private readonly NormalizedUnitBuilder _builder;

        public RecordWalker(SemanticModel model, NormalizedUnitBuilder builder)
        {
            _model = model;
            _builder = builder;
        }

        public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
        {
            // Only positional records have a primary-constructor parameter
            // list. A property-init record (no parameter list) carries no
            // synthesized positional surface; leave it untouched.
            ParameterListSyntax? parameterList = node.ParameterList;
            if (parameterList is not null)
            {
                Record(node, parameterList);
            }

            // Descend so nested positional records (declared inside this
            // record's body) are visited in span order too.
            base.VisitRecordDeclaration(node);
        }

        private void Record(RecordDeclarationSyntax node, ParameterListSyntax parameterList)
        {
            if (_model.GetDeclaredSymbol(node) is not INamedTypeSymbol recordSymbol)
            {
                // Malformed / unresolvable record: Pass 2 never throws on
                // well-formed input, and an unbound declaration is a Pass-1
                // diagnostic concern, so simply skip it here.
                return;
            }

            List<RecordPositionalParameter> parameters = new(parameterList.Parameters.Count);
            List<SynthesizedInitProperty> initProperties = new(parameterList.Parameters.Count);
            List<PrimaryCtorAssignment> ctorAssignments = new(parameterList.Parameters.Count);

            int index = 0;
            foreach (ParameterSyntax parameterSyntax in parameterList.Parameters)
            {
                string parameterName = parameterSyntax.Identifier.ValueText;

                // The parameter's type. Prefer the bound parameter symbol's
                // type display; fall back to the syntactic type text when the
                // parameter does not bind (it never throws either way).
                string typeDisplay = ResolveParameterTypeDisplay(parameterSyntax);

                parameters.Add(new RecordPositionalParameter(parameterName, typeDisplay, index));

                // Each positional parameter lowers to a public init-only
                // auto-property of the same name + type (Section 3.2 line 415,
                // Section 5.4 line 1553: "parameters become public init-only
                // FProperty entries").
                initProperties.Add(new SynthesizedInitProperty(parameterName, typeDisplay));

                // The primary constructor assigns each parameter to its
                // property: `this.Name = name;` (the param + property share a
                // name in C# records, so the assignment is by-name).
                ctorAssignments.Add(new PrimaryCtorAssignment(
                    PropertyName: parameterName,
                    ParameterName: parameterName));

                index++;
            }

            // Probe the bound record symbol for the Roslyn-synthesized
            // value-semantics members Pass 6 will emit. These are always
            // generated for a record with a primary constructor, but probing
            // the symbol keeps the flags honest if the language model changes.
            bool hasEquals = HasMember(recordSymbol, "Equals");
            bool hasGetHashCode = HasMember(recordSymbol, "GetHashCode");
            bool hasDeconstruct = parameters.Count > 0 && HasMember(recordSymbol, "Deconstruct");
            bool hasToString = HasMember(recordSymbol, "ToString");

            bool isRecordStruct = node.IsKind(SyntaxKind.RecordStructDeclaration);

            SynthesizedRecordMembers synthesized = new(
                RecordName: recordSymbol.Name,
                IsRecordStruct: isRecordStruct,
                Parameters: parameters,
                InitProperties: initProperties,
                PrimaryCtorAssignments: ctorAssignments,
                GeneratesEquals: hasEquals,
                GeneratesGetHashCode: hasGetHashCode,
                GeneratesDeconstruct: hasDeconstruct,
                GeneratesToString: hasToString);

            // Per-symbol attach (the record type owns its synthesized
            // surface) + a node annotation so a tree-walking consumer recovers
            // the same facts from the originating declaration.
            _builder.Attach(recordSymbol, synthesized);
            _builder.AnnotateNode(node, new RecordPositionalCtorAnnotation(synthesized));
        }

        private string ResolveParameterTypeDisplay(ParameterSyntax parameterSyntax)
        {
            if (parameterSyntax.Type is TypeSyntax typeSyntax)
            {
                ITypeSymbol? typeSymbol = _model.GetTypeInfo(typeSyntax).Type;
                if (typeSymbol is not null && typeSymbol.TypeKind != TypeKind.Error)
                {
                    return typeSymbol.ToDisplayString();
                }

                // Fall back to the source text of the type when binding does
                // not yield a concrete type (never throws).
                return typeSyntax.ToString();
            }

            return string.Empty;
        }

        private static bool HasMember(INamedTypeSymbol type, string name)
        {
            foreach (ISymbol member in type.GetMembers(name))
            {
                // A member with the name exists (auto-generated or explicit);
                // that is sufficient for the "Pass 6 will emit this" flag.
                _ = member;
                return true;
            }
            return false;
        }
    }
}

/// <summary>
/// One positional parameter of a record's primary constructor: its declared
/// name, its type's display string, and its zero-based declaration index.
/// </summary>
/// <param name="Name">The parameter name (also the synthesized property name).</param>
/// <param name="TypeDisplay">The parameter type's fully-qualified display string (or the source type text when unbound).</param>
/// <param name="Index">The zero-based position in the primary-constructor parameter list.</param>
public sealed record RecordPositionalParameter(string Name, string TypeDisplay, int Index);

/// <summary>
/// One init-only auto-property synthesized from a positional parameter
/// (Section 3.2 line 415 / Section 5.4 line 1553: positional parameters become
/// public init-only properties).
/// </summary>
/// <param name="Name">The property name (matches the originating parameter).</param>
/// <param name="TypeDisplay">The property type's display string.</param>
public sealed record SynthesizedInitProperty(string Name, string TypeDisplay);

/// <summary>
/// One primary-constructor body assignment: <c>this.PropertyName =
/// ParameterName;</c> (in C# records the parameter and property share a name).
/// </summary>
/// <param name="PropertyName">The init-only property assigned to.</param>
/// <param name="ParameterName">The primary-constructor parameter read from.</param>
public sealed record PrimaryCtorAssignment(string PropertyName, string ParameterName);

/// <summary>
/// The explicit synthesized member surface of one positional record /
/// record-struct, recorded by <see cref="RecordPositionalCtorNormalizer"/> so
/// Pass 6 emits every member uniformly (Section 3.2 line 415).
/// </summary>
/// <param name="RecordName">The record's simple type name.</param>
/// <param name="IsRecordStruct">True for <c>record struct</c>; false for <c>record class</c>.</param>
/// <param name="Parameters">The positional parameters, in declaration order.</param>
/// <param name="InitProperties">The synthesized init-only properties, one per positional parameter, in order.</param>
/// <param name="PrimaryCtorAssignments">The primary-constructor parameter-to-property assignments, in order.</param>
/// <param name="GeneratesEquals">True iff the record carries a compiler-generated (or explicit) <c>Equals</c>.</param>
/// <param name="GeneratesGetHashCode">True iff the record carries a compiler-generated (or explicit) <c>GetHashCode</c>.</param>
/// <param name="GeneratesDeconstruct">True iff the record carries a compiler-generated (or explicit) <c>Deconstruct</c> (positional records with at least one parameter).</param>
/// <param name="GeneratesToString">True iff the record carries a compiler-generated (or explicit) <c>ToString</c>.</param>
public sealed record SynthesizedRecordMembers(
    string RecordName,
    bool IsRecordStruct,
    IReadOnlyList<RecordPositionalParameter> Parameters,
    IReadOnlyList<SynthesizedInitProperty> InitProperties,
    IReadOnlyList<PrimaryCtorAssignment> PrimaryCtorAssignments,
    bool GeneratesEquals,
    bool GeneratesGetHashCode,
    bool GeneratesDeconstruct,
    bool GeneratesToString);

/// <summary>
/// Per-node annotation recorded on each positional
/// <see cref="RecordDeclarationSyntax"/>, carrying the same
/// <see cref="SynthesizedRecordMembers"/> attached to the record symbol so a
/// tree-walking Pass-6 consumer can recover the synthesized surface from the
/// declaration node.
/// </summary>
public sealed class RecordPositionalCtorAnnotation : LoweredAnnotation
{
    /// <summary>
    /// Construct the annotation over the record's synthesized member surface.
    /// </summary>
    /// <param name="members">The synthesized member surface. Must not be null.</param>
    /// <exception cref="System.ArgumentNullException">If <paramref name="members"/> is null.</exception>
    public RecordPositionalCtorAnnotation(SynthesizedRecordMembers members)
    {
        System.ArgumentNullException.ThrowIfNull(members);
        Members = members;
    }

    /// <summary>The synthesized member surface of the annotated record.</summary>
    public SynthesizedRecordMembers Members { get; }

    /// <inheritdoc/>
    public override string Kind => "record-positional-ctor";
}
