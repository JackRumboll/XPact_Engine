// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;

namespace Simgenics.XPact.XIL2CPP.Normalization;

/// <summary>
/// The immutable product of XIL2CPP Pass 2 (AST normalization) for one
/// module per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2: the
/// authoritative Pass-1 result wrapped with the additive lowering metadata
/// the Pass-2 normalizers recorded (per-node
/// <see cref="LoweredAnnotation"/>s + a generic per-symbol synthesized-data
/// bag) and the Pass-2 diagnostics.
/// </summary>
/// <remarks>
/// <para>
/// <b>Annotation layer.</b> Pass 2 does not rewrite syntax trees; the
/// Pass-1 <see cref="Pass1Result.Compilation"/> and its semantic models
/// stay authoritative so Pass 3 can keep calling
/// <c>GetTypeInfo</c> / <c>GetSymbolInfo</c> / <c>AnalyzeDataFlow</c>. This
/// unit therefore exposes <see cref="Pass1"/> verbatim and layers the
/// lowering decisions on top, keyed on the original Roslyn
/// <see cref="SyntaxNode"/> identity.
/// </para>
/// <para>
/// <b>Parallel-safe extensibility.</b> A node may carry MULTIPLE
/// annotations of different concrete <see cref="LoweredAnnotation"/> types
/// (e.g. one normalizer's <c>using</c>-lowering and another's
/// capture-analysis on the same block). Retrieval is by concrete CLR type
/// (<see cref="GetAnnotation{T}(SyntaxNode)"/> /
/// <see cref="GetAnnotations{T}(SyntaxNode)"/>) and the per-symbol bag is
/// generic (<see cref="GetAttached{T}(ISymbol)"/> /
/// <see cref="GetSynthesized{T}"/>), so a later agent can store + retrieve a
/// brand-new annotation subclass or synthesized data type WITHOUT editing
/// this file or <see cref="NormalizedUnitBuilder"/>.
/// </para>
/// </remarks>
public sealed class NormalizedUnit
{
    private static readonly IReadOnlyList<LoweredAnnotation> s_emptyAnnotations =
        Array.Empty<LoweredAnnotation>();

    // Per-node lowering annotations. Keyed on SyntaxNode reference identity
    // (a tree node is a stable identity for the lifetime of the Pass-1
    // result this unit wraps). The list per node preserves insertion order
    // (normalizers run in deterministic Name order), so the recorded
    // annotation order is itself deterministic.
    private readonly IReadOnlyDictionary<SyntaxNode, IReadOnlyList<LoweredAnnotation>> _nodeAnnotations;

    // Per-symbol synthesized data, keyed first by the attaching value's CLR
    // type, then by the owning ISymbol. The two-level shape lets a later
    // agent attach a brand-new type without a shared schema.
    private readonly IReadOnlyDictionary<Type, IReadOnlyDictionary<ISymbol, object>> _attached;

    // Module-scoped synthesized data, keyed by the value's CLR type. Each
    // type maps to an append-only list (AddSynthesized) so a normalizer can
    // emit many records of its own type without colliding with other types.
    private readonly IReadOnlyDictionary<Type, IReadOnlyList<object>> _synthesized;

    /// <summary>
    /// Construct a normalized unit. Called only by
    /// <see cref="NormalizedUnitBuilder.Build"/>; the collections are
    /// already sealed (read-only snapshots) by the builder.
    /// </summary>
    internal NormalizedUnit(
        Pass1Result pass1,
        IReadOnlyDictionary<SyntaxNode, IReadOnlyList<LoweredAnnotation>> nodeAnnotations,
        IReadOnlyDictionary<Type, IReadOnlyDictionary<ISymbol, object>> attached,
        IReadOnlyDictionary<Type, IReadOnlyList<object>> synthesized,
        IReadOnlyList<DiagnosticRecord> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(pass1);
        ArgumentNullException.ThrowIfNull(nodeAnnotations);
        ArgumentNullException.ThrowIfNull(attached);
        ArgumentNullException.ThrowIfNull(synthesized);
        ArgumentNullException.ThrowIfNull(diagnostics);

        Pass1 = pass1;
        _nodeAnnotations = nodeAnnotations;
        _attached = attached;
        _synthesized = synthesized;
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// The authoritative Pass-1 result this unit annotates: the compilation,
    /// semantic models, parsed trees, the module name, and the
    /// <see cref="Pass1Result.IsSimPath"/> flag Pass 3 gates its sim-path
    /// analyzers on.
    /// </summary>
    public Pass1Result Pass1 { get; }

    /// <summary>
    /// The Pass-2 diagnostics the normalizers recorded, in collection order
    /// (normalizers run in deterministic <see cref="INormalizer.Name"/>
    /// order, and each appends in node-visit order).
    /// </summary>
    public IReadOnlyList<DiagnosticRecord> Diagnostics { get; }

    /// <summary>
    /// Get every annotation recorded on <paramref name="node"/>, of any
    /// concrete <see cref="LoweredAnnotation"/> type, in the order they were
    /// recorded. Returns an empty list when the node carries none.
    /// </summary>
    /// <param name="node">A Roslyn syntax node from the Pass-1 trees. Must not be null.</param>
    /// <returns>The annotations on the node (possibly empty, never null).</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="node"/> is null.</exception>
    public IReadOnlyList<LoweredAnnotation> GetAnnotations(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return _nodeAnnotations.TryGetValue(node, out IReadOnlyList<LoweredAnnotation>? list)
            ? list
            : s_emptyAnnotations;
    }

    /// <summary>
    /// Try to get every annotation recorded on <paramref name="node"/>.
    /// Returns false (and an empty list) when the node carries none.
    /// </summary>
    /// <param name="node">A Roslyn syntax node from the Pass-1 trees. Must not be null.</param>
    /// <param name="annotations">Receives the recorded annotations, or an empty list.</param>
    /// <returns>True iff at least one annotation is recorded on the node.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="node"/> is null.</exception>
    public bool TryGetAnnotations(SyntaxNode node, out IReadOnlyList<LoweredAnnotation> annotations)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (_nodeAnnotations.TryGetValue(node, out IReadOnlyList<LoweredAnnotation>? list))
        {
            annotations = list;
            return true;
        }
        annotations = s_emptyAnnotations;
        return false;
    }

    /// <summary>
    /// Get the first annotation of concrete type <typeparamref name="T"/>
    /// recorded on <paramref name="node"/>, or null when none is present.
    /// The typical case (one annotation of a given kind per node).
    /// </summary>
    /// <typeparam name="T">The concrete <see cref="LoweredAnnotation"/> subclass to retrieve.</typeparam>
    /// <param name="node">A Roslyn syntax node from the Pass-1 trees. Must not be null.</param>
    /// <returns>The first matching annotation, or null.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="node"/> is null.</exception>
    public T? GetAnnotation<T>(SyntaxNode node) where T : LoweredAnnotation
    {
        ArgumentNullException.ThrowIfNull(node);
        if (_nodeAnnotations.TryGetValue(node, out IReadOnlyList<LoweredAnnotation>? list))
        {
            foreach (LoweredAnnotation a in list)
            {
                if (a is T typed)
                {
                    return typed;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Get every annotation of concrete type <typeparamref name="T"/>
    /// recorded on <paramref name="node"/>, in recorded order. Returns an
    /// empty list when none match.
    /// </summary>
    /// <typeparam name="T">The concrete <see cref="LoweredAnnotation"/> subclass to retrieve.</typeparam>
    /// <param name="node">A Roslyn syntax node from the Pass-1 trees. Must not be null.</param>
    /// <returns>The matching annotations (possibly empty, never null).</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="node"/> is null.</exception>
    public IReadOnlyList<T> GetAnnotations<T>(SyntaxNode node) where T : LoweredAnnotation
    {
        ArgumentNullException.ThrowIfNull(node);
        List<T>? matches = null;
        if (_nodeAnnotations.TryGetValue(node, out IReadOnlyList<LoweredAnnotation>? list))
        {
            foreach (LoweredAnnotation a in list)
            {
                if (a is T typed)
                {
                    (matches ??= new List<T>()).Add(typed);
                }
            }
        }
        return (IReadOnlyList<T>?)matches ?? Array.Empty<T>();
    }

    /// <summary>
    /// Get the per-symbol synthesized value of type <typeparamref name="T"/>
    /// a normalizer attached to <paramref name="key"/> via
    /// <see cref="NormalizedUnitBuilder.Attach{T}(ISymbol, T)"/>, or the
    /// default of <typeparamref name="T"/> when none was attached.
    /// </summary>
    /// <typeparam name="T">The synthesized value type (defined in the attaching normalizer's own file).</typeparam>
    /// <param name="key">The owning symbol the value was attached to. Must not be null.</param>
    /// <returns>The attached value, or <c>default</c>.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="key"/> is null.</exception>
    public T? GetAttached<T>(ISymbol key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_attached.TryGetValue(typeof(T), out IReadOnlyDictionary<ISymbol, object>? bySymbol)
            && bySymbol.TryGetValue(key, out object? value))
        {
            return (T)value;
        }
        return default;
    }

    /// <summary>
    /// Try to get the per-symbol synthesized value of type
    /// <typeparamref name="T"/> attached to <paramref name="key"/>.
    /// </summary>
    /// <typeparam name="T">The synthesized value type.</typeparam>
    /// <param name="key">The owning symbol the value was attached to. Must not be null.</param>
    /// <param name="value">Receives the attached value when present.</param>
    /// <returns>True iff a value of type <typeparamref name="T"/> is attached to the symbol.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="key"/> is null.</exception>
    public bool TryGetAttached<T>(ISymbol key, out T value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_attached.TryGetValue(typeof(T), out IReadOnlyDictionary<ISymbol, object>? bySymbol)
            && bySymbol.TryGetValue(key, out object? stored))
        {
            value = (T)stored;
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>
    /// Get every module-scoped synthesized record of type
    /// <typeparamref name="T"/> a normalizer appended via
    /// <see cref="NormalizedUnitBuilder.AddSynthesized{T}(T)"/>, in append
    /// order. Returns an empty list when none were added.
    /// </summary>
    /// <typeparam name="T">The synthesized record type (defined in the appending normalizer's own file).</typeparam>
    /// <returns>The appended records (possibly empty, never null).</returns>
    public IReadOnlyList<T> GetSynthesized<T>()
    {
        if (_synthesized.TryGetValue(typeof(T), out IReadOnlyList<object>? list))
        {
            // The builder stored a List<object>; project to T. The cast is
            // total because AddSynthesized only ever appends T into the
            // typeof(T) slot.
            List<T> typed = new(list.Count);
            foreach (object o in list)
            {
                typed.Add((T)o);
            }
            return typed;
        }
        return Array.Empty<T>();
    }
}
