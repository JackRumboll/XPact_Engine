// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Frontend;

namespace Simgenics.XPact.XIL2CPP.Normalization;

/// <summary>
/// Pass-2 normalizer for C# local functions
/// (<see cref="LocalFunctionStatementSyntax"/>) per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.9 (lambda + closure
/// lowering) and the Section 3.2 / line 418 lowering rule: captured-variable
/// analysis lowers a local function to a generated display class with the
/// captures as fields (the same lowering as a capturing lambda), while a
/// <em>non-capturing</em> local function emits as a same-file C++ function in
/// an anonymous namespace.
/// </summary>
/// <remarks>
/// <para>
/// <b>Annotate, do not rewrite.</b> Following the Pass-2 contract, this
/// normalizer records its lowering decision as an additive
/// <see cref="LocalFunctionAnnotation"/> on each
/// <see cref="LocalFunctionStatementSyntax"/>; it never mutates the Pass-1
/// compilation or trees. The capture set is computed from the authoritative
/// semantic model's <c>AnalyzeDataFlow</c>
/// (<see cref="DataFlowAnalysis.Captured"/>), the same dataflow Roslyn uses
/// to decide hoisting, so the recorded decision matches the C# semantics
/// Pass 6 must emit.
/// </para>
/// <para>
/// <b>Classification.</b> A local function is <em>non-capturing</em> iff it
/// captures no enclosing local / parameter AND does not capture
/// <c>this</c>. Roslyn surfaces an implicit <c>this</c> capture (a reference
/// to an instance member, <c>this</c>, or <c>base</c> of an enclosing type)
/// as the enclosing method's <c>this</c> parameter inside the same
/// <see cref="DataFlowAnalysis.Captured"/> set, so the captured-this flag and
/// the captured-variable set both fall out of one authoritative query. A
/// <c>static</c> local function can never capture (the C# compiler forbids
/// it), so it is always non-capturing. A capturing local function carries the
/// same display-class lowering as a capturing lambda (Section 5.9,
/// FIX-B-MINOR-06).
/// </para>
/// <para>
/// <b>Determinism.</b> Local functions are visited in source-declaration
/// (span) order across the parsed files in their canonical ordinal order; the
/// captured-symbol set is sorted by a stable key
/// (<c>ToDisplayString</c> ordinal, then
/// <see cref="ISymbol.Kind"/>, then declaration span) so two runs over
/// identical input record an identical set (gates X-IL2CPP-MANGLE-DET /
/// X-IL2CPP-CSPATH-DET, Section 9.9).
/// </para>
/// </remarks>
public sealed class LocalFunctionNormalizer : INormalizer
{
    /// <inheritdoc />
    public string Name => "LocalFunctionNormalizer";

    /// <inheritdoc />
    public void Normalize(Pass1Result pass1, NormalizedUnitBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(pass1);
        ArgumentNullException.ThrowIfNull(builder);

        // Visit files in their canonical ordinal order, then local functions
        // in document (span) order within each file. DescendantNodes yields
        // nodes in span order, so the visit order is deterministic.
        foreach (ModuleParser.ParsedFile parsed in pass1.ParsedFiles)
        {
            SemanticModel model = pass1.GetSemanticModel(parsed.Tree);
            SyntaxNode root = parsed.Tree.GetRoot();

            foreach (LocalFunctionStatementSyntax local in
                     root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
            {
                LocalFunctionAnnotation annotation = Analyze(model, local);
                builder.AnnotateNode(local, annotation);
            }
        }
    }

    /// <summary>
    /// Compute the lowering decision for a single local function from the
    /// authoritative semantic model.
    /// </summary>
    private static LocalFunctionAnnotation Analyze(
        SemanticModel model,
        LocalFunctionStatementSyntax local)
    {
        bool isStatic = local.Modifiers.Any(SyntaxKind.StaticKeyword);

        // Captured variables + this. A static local function cannot capture,
        // so skip the dataflow query (and Roslyn would report the empty set
        // anyway). For an instance local function, AnalyzeDataFlow over the
        // whole statement reports the variables it reads/writes from any
        // enclosing scope via DataFlowAnalysis.Captured. Roslyn surfaces an
        // implicit this capture as the enclosing method's `this` parameter
        // (an IParameterSymbol with IsThis == true) inside that same set, so
        // the captured-this flag and the captured-variable set both fall out
        // of one authoritative query.
        bool capturesThis = false;
        IReadOnlyList<ISymbol> captured = Array.Empty<ISymbol>();
        if (!isStatic)
        {
            DataFlowAnalysis? dataFlow = model.AnalyzeDataFlow(local);
            if (dataFlow is { Succeeded: true })
            {
                List<ISymbol> variables = new();
                foreach (ISymbol symbol in dataFlow.Captured)
                {
                    if (symbol is IParameterSymbol { IsThis: true })
                    {
                        capturesThis = true;
                    }
                    else
                    {
                        variables.Add(symbol);
                    }
                }
                captured = SortCaptures(variables);
            }
        }

        bool isNonCapturing = captured.Count == 0 && !capturesThis;

        return new LocalFunctionAnnotation(
            captured,
            capturesThis,
            isStatic,
            isNonCapturing);
    }

    /// <summary>
    /// Sort the captured-variable set into a stable, deterministic order.
    /// </summary>
    /// <remarks>
    /// Roslyn returns <see cref="DataFlowAnalysis.Captured"/> in an
    /// implementation-defined order. We sort by the symbol's display string
    /// (ordinal), then its <see cref="ISymbol.Kind"/>, then its first
    /// declaration span (file path, then start) so identically-named symbols
    /// in different scopes order stably across runs.
    /// </remarks>
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
    /// location (file path + character span), or the symbol's metadata name
    /// when it has no source location.
    /// </summary>
    private static string DeclarationSpanKey(ISymbol symbol)
    {
        if (!symbol.Locations.IsDefaultOrEmpty)
        {
            Location loc = symbol.Locations[0];
            if (loc.IsInSource && loc.SourceTree is { } tree)
            {
                return $"{tree.FilePath}:{loc.SourceSpan.Start}:{loc.SourceSpan.Length}";
            }
        }
        return symbol.MetadataName;
    }
}

/// <summary>
/// The Pass-2 lowering decision recorded on each
/// <see cref="LocalFunctionStatementSyntax"/> by
/// <see cref="LocalFunctionNormalizer"/>, per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.9: the captured-variable
/// symbol set, whether the local function captures <c>this</c>, whether it is
/// declared <c>static</c>, and the non-capturing flag that selects the
/// anonymous-namespace free-function emit vs the display-class emit.
/// </summary>
public sealed class LocalFunctionAnnotation : LoweredAnnotation
{
    /// <summary>
    /// Construct a local-function lowering annotation.
    /// </summary>
    /// <param name="capturedVariables">
    /// The captured enclosing locals / parameters, in a stable deterministic
    /// order. Empty for a non-capturing or static local function. Must not be
    /// null.
    /// </param>
    /// <param name="capturesThis">True iff the body captures <c>this</c> (an instance-member / this / base reference).</param>
    /// <param name="isStatic">True iff the local function is declared <c>static</c>.</param>
    /// <param name="isNonCapturing">
    /// True iff the local function captures nothing (no variables and not
    /// <c>this</c>) and therefore emits as an anonymous-namespace free
    /// function rather than a display class.
    /// </param>
    /// <exception cref="ArgumentNullException">If <paramref name="capturedVariables"/> is null.</exception>
    public LocalFunctionAnnotation(
        IReadOnlyList<ISymbol> capturedVariables,
        bool capturesThis,
        bool isStatic,
        bool isNonCapturing)
    {
        ArgumentNullException.ThrowIfNull(capturedVariables);
        CapturedVariables = capturedVariables;
        CapturesThis = capturesThis;
        IsStatic = isStatic;
        IsNonCapturing = isNonCapturing;
    }

    /// <inheritdoc />
    public override string Kind => "local-function";

    /// <summary>
    /// The captured enclosing locals / parameters this local function reads
    /// or writes, in a stable deterministic order (sorted by display string,
    /// then kind, then declaration span). Each becomes a field on the
    /// generated display class when <see cref="IsNonCapturing"/> is false.
    /// Empty for a non-capturing or <c>static</c> local function.
    /// </summary>
    public IReadOnlyList<ISymbol> CapturedVariables { get; }

    /// <summary>
    /// True iff the local-function body captures <c>this</c> (it references
    /// <c>this</c>, <c>base</c>, or a non-static instance member of an
    /// enclosing type). When true, the display class carries the captured
    /// <c>this</c> as a field (registered in the XGCRootSpan per Section 5.9).
    /// Always false for a <c>static</c> local function.
    /// </summary>
    public bool CapturesThis { get; }

    /// <summary>
    /// True iff the local function is declared <c>static</c>. A static local
    /// function can never capture, so it is always non-capturing.
    /// </summary>
    public bool IsStatic { get; }

    /// <summary>
    /// True iff the local function captures nothing (no enclosing locals /
    /// parameters and not <c>this</c>). A non-capturing local function emits
    /// as a same-file C++ function in an anonymous namespace (Section 3.2
    /// line 418); a capturing one emits as a display class with the captures
    /// as fields (the same lowering as a capturing lambda, Section 5.9 /
    /// FIX-B-MINOR-06).
    /// </summary>
    public bool IsNonCapturing { get; }
}
