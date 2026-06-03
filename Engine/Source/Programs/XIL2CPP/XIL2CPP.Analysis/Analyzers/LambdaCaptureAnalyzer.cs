// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using DiagnosticSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Analysis;

/// <summary>
/// Pass-3 semantic analyzer for C# lambda + anonymous-method closures per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.9 (lambda + closure emit).
/// For each anonymous function (a lambda
/// <see cref="ParenthesizedLambdaExpressionSyntax"/> /
/// <see cref="SimpleLambdaExpressionSyntax"/> or an anonymous method
/// <see cref="AnonymousMethodExpressionSyntax"/>) it queries the authoritative
/// semantic model's <see cref="DataFlowAnalysis.Captured"/> set and classifies
/// every captured symbol: which captures are XObject references (each becomes a
/// field registered in the closure's XGCRootSpan in Pass 6), which are plain
/// values, and whether the closure captures <c>this</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Recorded metadata.</b> One <see cref="ClassifiedCaptureSet"/> is
/// appended (via <see cref="Pass3ResultBuilder.Add{T}(T)"/>) per anonymous
/// function, keyed by the lambda's own <see cref="IMethodSymbol"/>. A list
/// (not a singleton) is used because a module contains many lambdas; the
/// per-lambda key lets Pass 6 look its closure metadata up by the lambda's
/// method symbol. Records are appended in deterministic source order so two
/// runs over identical input record an identical sequence.
/// </para>
/// <para>
/// <b>Local functions are NOT handled here.</b> A C# local function is a
/// <see cref="LocalFunctionStatementSyntax"/> (a statement), not an
/// <see cref="AnonymousFunctionExpressionSyntax"/>; its capture analysis is
/// already recorded by the Pass-2 <c>LocalFunctionNormalizer</c> annotation,
/// so this analyzer deliberately walks only anonymous functions to avoid
/// double-classifying the same construct (Section 5.9 / FIX-B-MINOR-06).
/// </para>
/// <para>
/// <b>Diagnostics owned (Section 12).</b>
/// <list type="bullet">
///   <item><description><b>XIL2CPP080</b> (Error, &amp;sect;5.13) -- a captured variable whose type is a
///   <c>Span&amp;lt;T&amp;gt;</c> / <c>ReadOnlySpan&amp;lt;T&amp;gt;</c> (or any other ref-struct) where T
///   is or contains an XObject reference. <c>Span</c> over XObject* is banned in MVP; capturing it
///   into a closure is the invalid shape this analyzer detects.</description></item>
///   <item><description><b>XIL2CPP096</b> (Error, &amp;sect;5.1) -- a captured variable whose type is a
///   <c>[XValueClass]</c>-tagged reference type. A C# closure captures locals by reference, so the
///   value-class would be observed with reference identity, which the attribute forbids.</description></item>
/// </list>
/// The general (non-capture) Span / value-class bands are owned by other
/// analyzers; this analyzer emits each code only at the lambda-capture site.
/// </para>
/// <para>
/// <b>Determinism.</b> Anonymous functions are visited in source order (parsed
/// files in canonical ordinal order, then document/span order within each
/// file). Each capture set is sorted by a stable key (the captured symbol's
/// display string ordinal, then <see cref="ISymbol.Kind"/>, then first
/// declaration span) so the recorded order is machine-independent (gate
/// X-IL2CPP-CSPATH-DET, Section 9.9). No ambient state / <c>DateTime</c> /
/// <c>Random</c>.
/// </para>
/// </remarks>
public sealed class LambdaCaptureAnalyzer : ISemanticAnalyzer
{
    /// <summary>The metadata name of the value-class opt-in attribute (with the Attribute suffix).</summary>
    private const string XValueClassAttributeMetadataName = "XValueClassAttribute";

    /// <summary>The short (source) name of the value-class opt-in attribute.</summary>
    private const string XValueClassAttributeShortName = "XValueClass";

    /// <inheritdoc />
    public string Name => "LambdaCaptureAnalyzer";

    /// <inheritdoc />
    public void Analyze(NormalizedUnit unit, Pass3ResultBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(builder);

        Pass1Result pass1 = unit.Pass1;

        // Visit files in canonical ordinal order, then anonymous functions in
        // document (span) order within each file. DescendantNodes yields nodes
        // in span order, so the visit order is deterministic.
        foreach (ModuleParser.ParsedFile parsed in pass1.ParsedFiles)
        {
            SemanticModel model = pass1.GetSemanticModel(parsed.Tree);
            SyntaxNode root = parsed.Tree.GetRoot();

            foreach (SyntaxNode node in root.DescendantNodes())
            {
                if (node is AnonymousFunctionExpressionSyntax anonymous)
                {
                    AnalyzeAnonymousFunction(model, anonymous, pass1.ModuleName, builder);
                }
            }
        }
    }

    /// <summary>
    /// Classify the captures of one anonymous function and append its
    /// <see cref="ClassifiedCaptureSet"/>, emitting XIL2CPP080 / XIL2CPP096 for
    /// invalid captured types.
    /// </summary>
    private static void AnalyzeAnonymousFunction(
        SemanticModel model,
        AnonymousFunctionExpressionSyntax anonymous,
        string moduleName,
        Pass3ResultBuilder builder)
    {
        // The lambda's own method symbol is the key Pass 6 will look up its
        // closure metadata by. If the model cannot bind the anonymous function
        // to a method symbol (only possible on malformed input), skip it: an
        // analyzer never throws on well-formed input and has nothing to record
        // for an unbound closure.
        if (model.GetSymbolInfo(anonymous).Symbol is not IMethodSymbol lambdaSymbol)
        {
            return;
        }

        // Analyze the lambda BODY (not the whole anonymous-function node): the
        // captures of THIS closure are the enclosing variables that flow across
        // the body boundary -- DataFlowsIn (read inside, defined outside) union
        // DataFlowsOut (written inside, observed outside) -- minus the lambda's
        // own parameters. (DataFlowAnalysis.Captured is unreliable on a body
        // region: it reports the captures of every anonymous function in the
        // surrounding analysis, leaking sibling lambdas' captures. The
        // flows-in/out delta is the closure's true capture set, including
        // write-only and transitively-nested captures.)
        DataFlowAnalysis? dataFlow = model.AnalyzeDataFlow(anonymous.Body);
        if (dataFlow is not { Succeeded: true })
        {
            // Dataflow can fail only on malformed input; record an empty,
            // non-capturing set so the lambda still has metadata.
            builder.Add(new ClassifiedCaptureSet(
                lambdaSymbol,
                Array.Empty<ClassifiedCapture>(),
                Array.Empty<ClassifiedCapture>(),
                CapturesThis: false,
                IsNonCapturing: true));
            return;
        }

        bool capturesThis = false;
        List<ClassifiedCapture> xobjectCaptures = new();
        List<ClassifiedCapture> valueCaptures = new();

        foreach (ISymbol captured in SortCaptures(CollectCaptures(dataFlow, lambdaSymbol)))
        {
            // Roslyn surfaces an implicit `this` capture (a reference to a
            // non-static instance member, `this`, or `base` of an enclosing
            // type) as the enclosing method's `this` parameter inside the same
            // captured set. Record it as the this-capture flag (Pass 6
            // registers `this` in the closure's XGCRootSpan when the enclosing
            // type is XObject-derived) rather than as a captured variable.
            if (captured is IParameterSymbol { IsThis: true })
            {
                capturesThis = true;
                continue;
            }

            ITypeSymbol? type = CapturedSymbolType(captured);
            if (type is null)
            {
                continue;
            }

            ClassifiedCapture capture = new(captured, captured.Name, type);

            // XIL2CPP080: a captured ref-struct (Span<T> / ReadOnlySpan<T> /
            // any ref-like type) whose element type is or contains an XObject
            // reference. Capturing such a value into a closure is banned in
            // MVP (Section 5.13). Emit and do not also bucket it as a value
            // capture.
            if (type is INamedTypeSymbol { IsRefLikeType: true } refStruct
                && RefStructCarriesXObject(refStruct))
            {
                builder.AddDiagnostic(MakeDiagnostic(
                    captured,
                    DiagnosticSeverity.Error,
                    DiagnosticCodes.SpanOverXObjectUnsupported,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "captured variable '{0}' of type '{1}' is a Span/ref-struct over an XObject reference, which is not supported in MVP",
                        captured.Name,
                        type.ToDisplayString()),
                    moduleName));
                continue;
            }

            // XIL2CPP096: a captured [XValueClass]-tagged reference type. A
            // closure captures locals by reference, so the value-class would
            // be observed with reference identity, which the attribute forbids
            // (Section 5.1). Emit but still classify it as a value capture so
            // Pass 6 has its metadata.
            if (type is INamedTypeSymbol named && HasXValueClassAttribute(named))
            {
                builder.AddDiagnostic(MakeDiagnostic(
                    captured,
                    DiagnosticSeverity.Error,
                    DiagnosticCodes.XValueClassObservedWithReferenceIdentity,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "[XValueClass]-tagged type '{0}' observed with reference identity at capture of '{1}'; remove [XValueClass] or change the call shape",
                        type.ToDisplayString(),
                        captured.Name),
                    moduleName));
            }

            if (IsXObjectReference(type))
            {
                xobjectCaptures.Add(capture);
            }
            else
            {
                valueCaptures.Add(capture);
            }
        }

        bool isNonCapturing = !capturesThis
            && xobjectCaptures.Count == 0
            && valueCaptures.Count == 0;

        builder.Add(new ClassifiedCaptureSet(
            lambdaSymbol,
            xobjectCaptures,
            valueCaptures,
            capturesThis,
            isNonCapturing));
    }

    /// <summary>
    /// Collect the closure's captured symbols from a body-region dataflow
    /// result: the union of <see cref="DataFlowAnalysis.DataFlowsIn"/> and
    /// <see cref="DataFlowAnalysis.DataFlowsOut"/> (the enclosing locals /
    /// parameters / <c>this</c> that cross the body boundary, covering
    /// read-only, write-only, and transitively-nested captures) minus the
    /// lambda's own parameters (which appear in <c>DataFlowsIn</c> at the body
    /// boundary but are not captures). Locals declared inside the lambda never
    /// cross the boundary, so they are excluded automatically.
    /// </summary>
    private static IEnumerable<ISymbol> CollectCaptures(
        DataFlowAnalysis dataFlow,
        IMethodSymbol lambdaSymbol)
    {
        HashSet<ISymbol> ownParameters = new(SymbolEqualityComparer.Default);
        foreach (IParameterSymbol parameter in lambdaSymbol.Parameters)
        {
            ownParameters.Add(parameter);
        }

        HashSet<ISymbol> captures = new(SymbolEqualityComparer.Default);
        foreach (ISymbol symbol in dataFlow.DataFlowsIn)
        {
            if (!ownParameters.Contains(symbol))
            {
                captures.Add(symbol);
            }
        }
        foreach (ISymbol symbol in dataFlow.DataFlowsOut)
        {
            if (!ownParameters.Contains(symbol))
            {
                captures.Add(symbol);
            }
        }
        return captures;
    }

    /// <summary>
    /// The declared type of a captured local or parameter, or null for a
    /// captured symbol that has no value type (e.g. a captured range variable
    /// the model cannot type).
    /// </summary>
    private static ITypeSymbol? CapturedSymbolType(ISymbol symbol) => symbol switch
    {
        ILocalSymbol local => local.Type,
        IParameterSymbol parameter => parameter.Type,
        IRangeVariableSymbol => null,
        _ => null,
    };

    /// <summary>
    /// Return true iff <paramref name="type"/> is an XObject reference: it IS
    /// the engine root <c>XObject</c> or transitively derives from it.
    /// </summary>
    private static bool IsXObjectReference(ITypeSymbol type)
        => type is INamedTypeSymbol named
           && (AnalyzerHelpers.IsXObjectType(named) || AnalyzerHelpers.IsXObjectDerived(named));

    /// <summary>
    /// Return true iff <paramref name="refStruct"/> (a ref-like type such as
    /// <c>Span&lt;T&gt;</c> / <c>ReadOnlySpan&lt;T&gt;</c>) is or contains an
    /// XObject reference: any of its type arguments (recursively, for nested
    /// ref-structs / spans) is an XObject reference, or the ref-struct itself
    /// is one of its type arguments' element types.
    /// </summary>
    private static bool RefStructCarriesXObject(INamedTypeSymbol refStruct)
    {
        foreach (ITypeSymbol arg in refStruct.TypeArguments)
        {
            if (TypeCarriesXObject(arg))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Return true iff <paramref name="type"/> is an XObject reference, an
    /// array of XObject references, or a generic type one of whose type
    /// arguments carries an XObject reference.
    /// </summary>
    private static bool TypeCarriesXObject(ITypeSymbol type)
    {
        if (IsXObjectReference(type))
        {
            return true;
        }

        if (type is IArrayTypeSymbol array)
        {
            return TypeCarriesXObject(array.ElementType);
        }

        if (type is INamedTypeSymbol named)
        {
            foreach (ITypeSymbol arg in named.TypeArguments)
            {
                if (TypeCarriesXObject(arg))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Return true iff <paramref name="type"/> carries the value-class opt-in
    /// attribute <c>[XValueClass]</c> (matched by the attribute class's
    /// metadata name, with or without the <c>Attribute</c> suffix).
    /// </summary>
    private static bool HasXValueClassAttribute(INamedTypeSymbol type)
    {
        foreach (AttributeData attribute in type.GetAttributes())
        {
            INamedTypeSymbol? attributeClass = attribute.AttributeClass;
            if (attributeClass is null)
            {
                continue;
            }
            string name = attributeClass.Name;
            if (name == XValueClassAttributeMetadataName || name == XValueClassAttributeShortName)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Build a file-anchored diagnostic at the captured symbol's first source
    /// location (1-based), or a file-less diagnostic when the symbol has none.
    /// </summary>
    private static DiagnosticRecord MakeDiagnostic(
        ISymbol capturedSymbol,
        DiagnosticSeverity severity,
        string code,
        string message,
        string moduleName)
    {
        if (!capturedSymbol.Locations.IsDefaultOrEmpty)
        {
            Location loc = capturedSymbol.Locations[0];
            if (loc.IsInSource)
            {
                FileLinePositionSpan lineSpan = loc.GetLineSpan();
                if (!string.IsNullOrEmpty(lineSpan.Path))
                {
                    // Roslyn reports 0-based line/character; SourceSpan is 1-based.
                    int line = lineSpan.StartLinePosition.Line + 1;
                    int column = lineSpan.StartLinePosition.Character + 1;
                    SourceSpan span = SourceSpan.Point(lineSpan.Path, line, column);
                    return span.ToDiagnostic(severity, code, message, moduleName);
                }
            }
        }

        return new DiagnosticRecord(severity, code, message, Module: moduleName);
    }

    /// <summary>
    /// Sort the captured-symbol set into a stable, deterministic order
    /// (display string ordinal, then <see cref="ISymbol.Kind"/>, then first
    /// declaration span) matching the Pass-2 LocalFunctionNormalizer's key so
    /// the two passes agree on capture ordering.
    /// </summary>
    private static IReadOnlyList<ISymbol> SortCaptures(IEnumerable<ISymbol> captured)
    {
        return captured
            .OrderBy(s => s.ToDisplayString(), StringComparer.Ordinal)
            .ThenBy(s => (int)s.Kind)
            .ThenBy(s => DeclarationSpanKey(s), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// A stable string tiebreaker derived from a symbol's first declaring
    /// location (file path + character span), or its metadata name when it has
    /// no source location.
    /// </summary>
    private static string DeclarationSpanKey(ISymbol symbol)
    {
        if (!symbol.Locations.IsDefaultOrEmpty)
        {
            Location loc = symbol.Locations[0];
            if (loc.IsInSource && loc.SourceTree is { } tree)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"{tree.FilePath}:{loc.SourceSpan.Start}:{loc.SourceSpan.Length}");
            }
        }
        return symbol.MetadataName;
    }
}

/// <summary>
/// One classified captured variable from a lambda / anonymous-method closure,
/// recorded by <see cref="LambdaCaptureAnalyzer"/> per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.9: the captured symbol, its
/// source name, and its declared type. An XObject capture becomes a field
/// registered in the closure's XGCRootSpan in Pass 6; a value capture becomes
/// a by-value display-class field.
/// </summary>
/// <param name="Symbol">The captured local / parameter symbol from the authoritative semantic model.</param>
/// <param name="Name">The captured symbol's source name (the display-class field name in Pass 6).</param>
/// <param name="Type">The captured symbol's declared type.</param>
public sealed record ClassifiedCapture(ISymbol Symbol, string Name, ITypeSymbol Type);

/// <summary>
/// The classified capture set of one lambda / anonymous-method closure,
/// recorded by <see cref="LambdaCaptureAnalyzer"/> per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.9. Keyed by the lambda's own
/// method symbol so Pass 6 can look up the closure's XGCRootSpan layout by the
/// lambda it is emitting.
/// </summary>
/// <param name="LambdaSymbol">The lambda's / anonymous method's own <see cref="IMethodSymbol"/> (the lookup key).</param>
/// <param name="XObjectCaptures">
/// The captured variables whose type is or derives from the engine
/// <c>XObject</c>, in deterministic order. Each becomes a field registered in
/// the closure's XGCRootSpan (Strong) in Pass 6.
/// </param>
/// <param name="ValueCaptures">
/// The captured variables that are plain values (not XObject references), in
/// deterministic order. Each becomes a by-value field on the display class.
/// </param>
/// <param name="CapturesThis">
/// True iff the closure captures <c>this</c> (it references <c>this</c>,
/// <c>base</c>, or a non-static instance member of an enclosing type). When
/// true and the enclosing type is XObject-derived, Pass 6 registers the
/// captured <c>this</c> in the closure's XGCRootSpan.
/// </param>
/// <param name="IsNonCapturing">
/// True iff the closure captures nothing (no variables and not <c>this</c>)
/// and therefore emits as a static C++ function pointer rather than a display
/// class (Section 5.9).
/// </param>
public sealed record ClassifiedCaptureSet(
    IMethodSymbol LambdaSymbol,
    IReadOnlyList<ClassifiedCapture> XObjectCaptures,
    IReadOnlyList<ClassifiedCapture> ValueCaptures,
    bool CapturesThis,
    bool IsNonCapturing);
