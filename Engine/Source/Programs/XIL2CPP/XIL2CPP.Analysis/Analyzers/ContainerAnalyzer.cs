// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using MsTypeKind = Microsoft.CodeAnalysis.TypeKind;
using XilSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Analysis;

/// <summary>
/// Pass-3 analyzer (WU-18) per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Sections 5.7 (container + tuple emit) and 6.2 (container roots:
/// XGCRootSpan emit) + Section 12. Enumerates every variable / field /
/// property / parameter declaration whose type is a supported BCL generic
/// container (<c>List&lt;T&gt;</c>, <c>Dictionary&lt;K,V&gt;</c>,
/// <c>HashSet&lt;T&gt;</c>), records a <see cref="ContainerSite"/> describing
/// the closed element / key / value types and the target
/// <c>TArray&lt;XPtr&lt;T'&gt;&gt;</c> / <c>TMap</c> / <c>TSet</c> emit
/// signature, and surfaces the GC-scaffolding diagnostics.
/// </summary>
/// <remarks>
/// <para>
/// <b>GC-scaffolding selection rules (Section 6.2 / 6.6).</b> For each
/// type-argument position that becomes a container slot (the element of
/// <c>List</c>/<c>HashSet</c>; the key + value of <c>Dictionary</c>):
/// <list type="bullet">
///   <item><description>Statically XObject-derived (e.g. <c>List&lt;XActor&gt;</c>) &#8594; <c>XGCRootKind::Strong</c>; the slot emits as <c>XPtr&lt;T'&gt;</c>. A <see cref="ContainerSite"/> is recorded.</description></item>
///   <item><description>Statically a value type (e.g. <c>List&lt;int&gt;</c>) &#8594; NO GC scaffolding (pure RAII); the slot emits as the closed value type. No <see cref="ContainerSite"/> is recorded.</description></item>
///   <item><description><c>object</c> or a non-XObject interface that could hold an XObject (e.g. <c>List&lt;object&gt;</c>, <c>List&lt;IDamageable&gt;</c>) &#8594; <c>XGCRootKind::Conservative</c>; the slot emits as <c>void*</c>. A conservative <see cref="ContainerSite"/> is recorded and <c>XIL2CPP070</c> is emitted.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Codes owned (Section 12, band 070-079).</b>
/// <list type="bullet">
///   <item><description><c>XIL2CPP070</c> &#8211; conservative XGCRootSpan; cross-arch determinism may be affected. Warning on non-sim-path; ELEVATED to error on sim-path (strict <c>SimPathConservativeRootsAllowed=false</c> default per Section 6.6).</description></item>
///   <item><description><c>XIL2CPP071</c> &#8211; container declared with copy semantics; XGC-aware containers are move-only (Section 6.2). Emitted for an XGC-aware (Strong / Conservative) container passed BY VALUE as a parameter, which would require a deleted copy-constructor in the C++ emit.</description></item>
///   <item><description><c>XIL2CPP073</c> &#8211; value type stored in an object-typed container requires boxing, which is not supported (Section 5.7). Emitted when an object-typed container declaration's initializer collection contains a statically value-typed element.</description></item>
/// </list>
/// <b>XIL2CPP072</b> (custom container implementation missing XGCRootSpan
/// registration in its constructor) is in this analyzer's owned band but has
/// no sound declaration-level signal within WU-18's scope (it concerns
/// author-written custom container <em>implementations</em>, not BCL
/// container <em>declarations</em>); it is therefore reserved and not emitted
/// here.
/// </para>
/// <para>
/// <b>Determinism.</b> Sites are visited in the deterministic member-then-
/// document order of <see cref="AnalyzerHelpers.EnumerateMemberDeclarations"/>
/// (extended with parameter + local declarations in document order), so the
/// recorded sites and diagnostics are identical across machines
/// (gate X-IL2CPP-CSPATH-DET).
/// </para>
/// </remarks>
public sealed class ContainerAnalyzer : ISemanticAnalyzer
{
    private const string ListDefinition = "System.Collections.Generic.List<T>";
    private const string DictionaryDefinition = "System.Collections.Generic.Dictionary<TKey, TValue>";
    private const string HashSetDefinition = "System.Collections.Generic.HashSet<T>";

    /// <inheritdoc/>
    public string Name => "ContainerAnalyzer";

    /// <inheritdoc/>
    public void Analyze(NormalizedUnit unit, Pass3ResultBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(builder);

        Pass1Result pass1 = unit.Pass1;
        bool isSimPath = pass1.IsSimPath;

        foreach (ModuleParser.ParsedFile parsed in pass1.ParsedFiles)
        {
            SemanticModel model = pass1.GetSemanticModel(parsed.Tree);
            SyntaxNode root = parsed.Tree.GetRoot();

            foreach (SyntaxNode node in root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case FieldDeclarationSyntax field:
                        foreach (VariableDeclaratorSyntax v in field.Declaration.Variables)
                        {
                            AnalyzeTyped(
                                unit, builder, model, field.Declaration.Type,
                                v.Initializer?.Value, ContainerStorage.Field, isSimPath);
                        }
                        break;

                    case PropertyDeclarationSyntax property:
                        AnalyzeTyped(
                            unit, builder, model, property.Type,
                            property.Initializer?.Value, ContainerStorage.Property, isSimPath);
                        break;

                    case LocalDeclarationStatementSyntax local:
                        foreach (VariableDeclaratorSyntax v in local.Declaration.Variables)
                        {
                            AnalyzeTyped(
                                unit, builder, model, local.Declaration.Type,
                                v.Initializer?.Value, ContainerStorage.Local, isSimPath);
                        }
                        break;

                    case ParameterSyntax parameter when parameter.Type is not null:
                        AnalyzeParameter(unit, builder, model, parameter, isSimPath);
                        break;
                }
            }
        }
    }

    private static void AnalyzeParameter(
        NormalizedUnit unit,
        Pass3ResultBuilder builder,
        SemanticModel model,
        ParameterSyntax parameter,
        bool isSimPath)
    {
        ContainerSite? site = AnalyzeTyped(
            unit, builder, model, parameter.Type!, initializer: null,
            ContainerStorage.Parameter, isSimPath);

        if (site is null)
        {
            return;
        }

        // XIL2CPP071: an XGC-aware (Strong / Conservative) container passed BY
        // VALUE requires copy-construction at the call boundary, but the
        // container's copy-ctor is `= delete` (move-only per Section 6.2). A
        // ref / in / out / params parameter does not copy, so it is exempt.
        if (site.GcKind == ContainerGcKind.None)
        {
            return;
        }

        bool byValue = true;
        foreach (SyntaxToken modifier in parameter.Modifiers)
        {
            if (modifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.RefKeyword)
                || modifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.OutKeyword)
                || modifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.InKeyword)
                || modifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ParamsKeyword))
            {
                byValue = false;
                break;
            }
        }

        if (!byValue)
        {
            return;
        }

        SourceSpan span = ToSpan(unit, parameter.Type!);
        builder.AddDiagnostic(span.ToDiagnostic(
            XilSeverity.Error,
            DiagnosticCodes.ContainerCopySemanticsForbidden,
            $"Container '{site.CSharpType}' is declared with copy semantics (by-value parameter '{parameter.Identifier.ValueText}'); "
            + "XGC-aware containers are move-only. Pass by 'ref'/'in'/'out' or refactor to avoid the copy.",
            unit.Pass1.ModuleName));
    }

    /// <summary>
    /// Analyze a single typed declaration site. Returns the recorded
    /// <see cref="ContainerSite"/> when the declared type is a supported,
    /// XGC-relevant container (Strong or Conservative), or null when the type
    /// is not a supported container or is a pure-value-type container (no
    /// site recorded for the latter, matching the &#8220;<c>List&lt;int&gt;</c> &#8594; no
    /// site&#8221; rule).
    /// </summary>
    private static ContainerSite? AnalyzeTyped(
        NormalizedUnit unit,
        Pass3ResultBuilder builder,
        SemanticModel model,
        TypeSyntax typeSyntax,
        ExpressionSyntax? initializer,
        ContainerStorage storage,
        bool isSimPath)
    {
        // `var`-typed declarations infer the type from the initializer; bind
        // the syntax to the authoritative model either way.
        if (model.GetTypeInfo(typeSyntax).Type is not INamedTypeSymbol declared
            || declared.TypeKind == MsTypeKind.Error)
        {
            return null;
        }

        if (!TryClassifyContainer(declared, out ContainerKind kind, out INamedTypeSymbol constructed))
        {
            return null;
        }

        SourceSpan span = ToSpan(unit, typeSyntax);

        // Classify each slot position.
        ITypeSymbol[] typeArgs = new ITypeSymbol[constructed.TypeArguments.Length];
        constructed.TypeArguments.CopyTo(typeArgs, 0);

        SlotClassification element;
        SlotClassification? key = null;
        SlotClassification? value = null;

        switch (kind)
        {
            case ContainerKind.List:
            case ContainerKind.HashSet:
                element = ClassifySlot(typeArgs[0]);
                break;
            case ContainerKind.Dictionary:
                key = ClassifySlot(typeArgs[0]);
                value = ClassifySlot(typeArgs[1]);
                // The container's "element" view is the value position for
                // List/Set semantics; for the record we keep key + value
                // separately, but use the value slot as the canonical element.
                element = value.Value;
                break;
            default:
                return null;
        }

        ContainerGcKind gcKind = CombineGcKind(kind, element, key, value);

        // Pure-value-type containers (e.g. List<int>, HashSet<int>,
        // Dictionary<int,int>) get no GC scaffolding and NO ContainerSite.
        if (gcKind == ContainerGcKind.None)
        {
            return null;
        }

        string emitSignature = BuildEmitSignature(kind, element, key, value);

        ContainerSite site = new(
            CSharpType: constructed.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Kind: kind,
            GcKind: gcKind,
            Storage: storage,
            ElementType: SlotDisplay(element),
            KeyType: key is { } k ? SlotDisplay(k) : null,
            ValueType: value is { } vv ? SlotDisplay(vv) : null,
            EmitSignature: emitSignature,
            File: span.File,
            Line: span.StartLine,
            Column: span.StartColumn);

        builder.Add(site);

        // XIL2CPP070: conservative container (object / non-XObject interface
        // slot). Warning on non-sim-path; error on sim-path (strict
        // SimPathConservativeRootsAllowed=false default, Section 6.6).
        if (gcKind == ContainerGcKind.Conservative)
        {
            XilSeverity severity = isSimPath ? XilSeverity.Error : XilSeverity.Warning;
            builder.AddDiagnostic(span.ToDiagnostic(
                severity,
                DiagnosticCodes.ConservativeXGCRootSpan,
                $"Container '{site.CSharpType}' emits a conservative XGCRootSpan; cross-arch determinism may be affected. "
                + "Consider replacing 'object' / non-XObject interface element with a typed XObject reference.",
                unit.Pass1.ModuleName));

            // XIL2CPP073: value-type entries in an object-typed container
            // require boxing (not supported). Detectable at the declaration
            // when the initializer is a collection / object-initializer whose
            // statically-typed elements are value types (Section 5.7).
            ReportObjectContainerValueTypeEntries(unit, builder, model, initializer, site);
        }

        return site;
    }

    /// <summary>
    /// Emit <c>XIL2CPP073</c> for each statically value-typed entry of an
    /// object-typed container's declaration initializer (a collection
    /// initializer / collection expression). A value-type entry would require
    /// implicit boxing into the <c>object</c> slot, which is banned in MVP.
    /// Reference-typed entries (XObject references, explicit boxing wrappers)
    /// are legal and not reported.
    /// </summary>
    private static void ReportObjectContainerValueTypeEntries(
        NormalizedUnit unit,
        Pass3ResultBuilder builder,
        SemanticModel model,
        ExpressionSyntax? initializer,
        ContainerSite site)
    {
        if (initializer is null)
        {
            return;
        }

        IEnumerable<ExpressionSyntax> entries = initializer switch
        {
            ObjectCreationExpressionSyntax oc when oc.Initializer is { } init => CollectionEntries(init),
            ImplicitObjectCreationExpressionSyntax ioc when ioc.Initializer is { } init => CollectionEntries(init),
            CollectionExpressionSyntax ce => CollectionExpressionEntries(ce),
            InitializerExpressionSyntax init => CollectionEntries(init),
            _ => Array.Empty<ExpressionSyntax>(),
        };

        foreach (ExpressionSyntax entry in entries)
        {
            if (model.GetTypeInfo(entry).Type is { } entryType
                && entryType.TypeKind != MsTypeKind.Error
                && entryType.IsValueType)
            {
                SourceSpan span = ToSpan(unit, entry);
                builder.AddDiagnostic(span.ToDiagnostic(
                    XilSeverity.Error,
                    DiagnosticCodes.ValueTypeInObjectContainerRequiresBoxing,
                    $"Value type '{entryType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' "
                    + $"stored in object-typed container '{site.CSharpType}' requires boxing, which is not supported. "
                    + "Use an XObject reference or an explicit [XValueClass] boxing wrapper.",
                    unit.Pass1.ModuleName));
            }
        }
    }

    private static IEnumerable<ExpressionSyntax> CollectionEntries(InitializerExpressionSyntax init)
    {
        // A collection initializer's expressions are the added elements; a
        // complex-element initializer ({ a, b }) inside a collection
        // initializer represents one multi-arg Add — we only inspect the
        // simple element form (the common List<object> { 1, 2 } shape).
        foreach (ExpressionSyntax e in init.Expressions)
        {
            if (e is not AssignmentExpressionSyntax)
            {
                yield return e;
            }
        }
    }

    private static IEnumerable<ExpressionSyntax> CollectionExpressionEntries(CollectionExpressionSyntax ce)
    {
        foreach (CollectionElementSyntax element in ce.Elements)
        {
            if (element is ExpressionElementSyntax ee)
            {
                yield return ee.Expression;
            }
        }
    }

    /// <summary>
    /// Classify <paramref name="declared"/> as a supported BCL generic
    /// container and yield the closed (constructed) symbol. Returns false for
    /// non-container types or unbound generics.
    /// </summary>
    private static bool TryClassifyContainer(
        INamedTypeSymbol declared,
        out ContainerKind kind,
        out INamedTypeSymbol constructed)
    {
        kind = default;
        constructed = declared;

        if (!declared.IsGenericType)
        {
            return false;
        }

        string definition = declared.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Strip the leading "global::" the fully-qualified format prepends.
        const string globalPrefix = "global::";
        if (definition.StartsWith(globalPrefix, StringComparison.Ordinal))
        {
            definition = definition.Substring(globalPrefix.Length);
        }

        switch (definition)
        {
            case ListDefinition when declared.TypeArguments.Length == 1:
                kind = ContainerKind.List;
                return true;
            case HashSetDefinition when declared.TypeArguments.Length == 1:
                kind = ContainerKind.HashSet;
                return true;
            case DictionaryDefinition when declared.TypeArguments.Length == 2:
                kind = ContainerKind.Dictionary;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Classify one container slot's static type into the GC-relevance bucket
    /// per the Section 6.2 selection rules.
    /// </summary>
    private static SlotClassification ClassifySlot(ITypeSymbol slotType)
    {
        if (slotType is INamedTypeSymbol named && AnalyzerHelpers.IsXObjectDerived(named))
        {
            return new SlotClassification(SlotKind.XObject, slotType);
        }

        // `object` itself, or any non-XObject interface, could hold an
        // XObject at runtime — conservative.
        if (slotType.SpecialType == SpecialType.System_Object
            || slotType.TypeKind == MsTypeKind.Interface)
        {
            return new SlotClassification(SlotKind.Conservative, slotType);
        }

        if (slotType.IsValueType)
        {
            return new SlotClassification(SlotKind.Value, slotType);
        }

        // A non-XObject reference class (e.g. a plain managed class) is not in
        // the XGC heap; treat it as a non-rooted value-ish slot (no GC
        // scaffolding). It is neither boxed nor XObject-rooted.
        return new SlotClassification(SlotKind.Value, slotType);
    }

    private static ContainerGcKind CombineGcKind(
        ContainerKind kind,
        SlotClassification element,
        SlotClassification? key,
        SlotClassification? value)
    {
        // Conservative dominates Strong only in the sense of warning; but the
        // container's overall kind is Conservative if ANY slot is
        // conservative, Strong if any slot is XObject (and none conservative),
        // else None.
        bool anyConservative;
        bool anyXObject;

        if (kind == ContainerKind.Dictionary)
        {
            SlotClassification k = key!.Value;
            SlotClassification v = value!.Value;
            anyConservative = k.Kind == SlotKind.Conservative || v.Kind == SlotKind.Conservative;
            anyXObject = k.Kind == SlotKind.XObject || v.Kind == SlotKind.XObject;
        }
        else
        {
            anyConservative = element.Kind == SlotKind.Conservative;
            anyXObject = element.Kind == SlotKind.XObject;
        }

        if (anyConservative)
        {
            return ContainerGcKind.Conservative;
        }
        if (anyXObject)
        {
            return ContainerGcKind.Strong;
        }
        return ContainerGcKind.None;
    }

    /// <summary>
    /// Build the target C++ emit signature string for the container per the
    /// Section 5.7 container-mapping table.
    /// </summary>
    private static string BuildEmitSignature(
        ContainerKind kind,
        SlotClassification element,
        SlotClassification? key,
        SlotClassification? value)
    {
        switch (kind)
        {
            case ContainerKind.List:
                return $"TArray<{SlotEmit(element)}>";
            case ContainerKind.HashSet:
                return $"TSet<{SlotEmit(element)}>";
            case ContainerKind.Dictionary:
                return $"TMap<{SlotEmit(key!.Value)}, {SlotEmit(value!.Value)}>";
            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// The C++ slot emit form per the Section 5.7 mapping table:
    /// XObject-derived &#8594; <c>XPtr&lt;T'&gt;</c>, conservative
    /// (<c>object</c> / interface) &#8594; <c>void*</c>, value type &#8594; the
    /// mapped value type (kept as the C# display name in this metadata-only
    /// pass; the emit pass maps int &#8594; int32_t etc.).
    /// </summary>
    private static string SlotEmit(SlotClassification slot) => slot.Kind switch
    {
        SlotKind.XObject => $"XPtr<{slot.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}>",
        SlotKind.Conservative => "void*",
        _ => slot.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
    };

    private static string SlotDisplay(SlotClassification slot)
        => slot.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    private static SourceSpan ToSpan(NormalizedUnit unit, SyntaxNode node)
    {
        FileLinePositionSpan flp = node.GetLocation().GetLineSpan();
        Microsoft.CodeAnalysis.Text.LinePosition start = flp.StartLinePosition;
        Microsoft.CodeAnalysis.Text.LinePosition end = flp.EndLinePosition;
        string path = string.IsNullOrEmpty(flp.Path) ? unit.Pass1.ModuleName : flp.Path;
        return new SourceSpan(
            path,
            start.Line + 1,
            start.Character + 1,
            end.Line + 1,
            end.Character + 1,
            validate: true);
    }

    private enum SlotKind
    {
        Value,
        XObject,
        Conservative,
    }

    private readonly record struct SlotClassification(SlotKind Kind, ITypeSymbol Type);
}

/// <summary>
/// The supported BCL generic container kinds WU-18 recognises.
/// </summary>
public enum ContainerKind
{
    /// <summary><c>System.Collections.Generic.List&lt;T&gt;</c> &#8594; <c>TArray</c>.</summary>
    List,

    /// <summary><c>System.Collections.Generic.Dictionary&lt;K,V&gt;</c> &#8594; <c>TMap</c>.</summary>
    Dictionary,

    /// <summary><c>System.Collections.Generic.HashSet&lt;T&gt;</c> &#8594; <c>TSet</c>.</summary>
    HashSet,
}

/// <summary>
/// The GC-scaffolding kind a container emits per <c>/Documents/XIL2CPP.html</c>
/// Rev 4 Section 6.2.
/// </summary>
public enum ContainerGcKind
{
    /// <summary>No GC scaffolding (pure RAII); every slot is a value type.</summary>
    None,

    /// <summary><c>XGCRootKind::Strong</c>; a slot is a statically XObject-derived reference.</summary>
    Strong,

    /// <summary><c>XGCRootKind::Conservative</c>; a slot is <c>object</c> / a non-XObject interface that could hold an XObject.</summary>
    Conservative,
}

/// <summary>
/// Where a container declaration is stored (the C++ parent-synthesis case per
/// Section 5.7's constructor-argument synthesis rule).
/// </summary>
public enum ContainerStorage
{
    /// <summary>Instance / static field declaration.</summary>
    Field,

    /// <summary>Property declaration.</summary>
    Property,

    /// <summary>Local variable declaration.</summary>
    Local,

    /// <summary>Method / constructor / indexer parameter declaration.</summary>
    Parameter,
}

/// <summary>
/// One recorded container declaration site (WU-18 emit metadata). Appended to
/// the Pass-3 result via <c>builder.Add&lt;ContainerSite&gt;</c>. Recorded only
/// for XGC-relevant containers (Strong or Conservative); pure-value-type
/// containers (e.g. <c>List&lt;int&gt;</c>) record no site.
/// </summary>
/// <param name="CSharpType">The closed C# container type (minimally-qualified, e.g. <c>List&lt;XActor&gt;</c>).</param>
/// <param name="Kind">The container kind (List / Dictionary / HashSet).</param>
/// <param name="GcKind">The GC-scaffolding kind (Strong / Conservative).</param>
/// <param name="Storage">Where the container is stored (field / property / local / parameter).</param>
/// <param name="ElementType">The closed element type (for Dictionary, the value type); the canonical slot type.</param>
/// <param name="KeyType">The closed key type for a Dictionary, else null.</param>
/// <param name="ValueType">The closed value type for a Dictionary, else null.</param>
/// <param name="EmitSignature">The target C++ emit signature (e.g. <c>TArray&lt;XPtr&lt;XActor&gt;&gt;</c>).</param>
/// <param name="File">Source file path of the declaration's type syntax.</param>
/// <param name="Line">1-based line of the declaration's type syntax.</param>
/// <param name="Column">1-based column of the declaration's type syntax.</param>
public sealed record ContainerSite(
    string CSharpType,
    ContainerKind Kind,
    ContainerGcKind GcKind,
    ContainerStorage Storage,
    string ElementType,
    string? KeyType,
    string? ValueType,
    string EmitSignature,
    string File,
    int Line,
    int Column);
