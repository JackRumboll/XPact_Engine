// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;

namespace Simgenics.XPact.XIL2CPP.Normalization;

/// <summary>
/// The mutable accumulator the Pass-2 normalizers write into. One builder is
/// shared across every normalizer for a module; <see cref="Pass2Driver"/>
/// threads it through the discovered <see cref="INormalizer"/>s in
/// deterministic order, then seals it into an immutable
/// <see cref="NormalizedUnit"/> via <see cref="Build"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Parallel-safe extensibility (the whole point).</b> The per-symbol
/// <see cref="Attach{T}(ISymbol, T)"/> surface and the module-scoped
/// <see cref="AddSynthesized{T}(T)"/> surface are generic and keyed by the
/// value's CLR type, so a later agent can introduce a brand-new synthesized
/// data type (declared in that agent's own file) and store / retrieve it
/// WITHOUT editing this builder or <see cref="NormalizedUnit"/>. Likewise
/// <see cref="AnnotateNode(SyntaxNode, LoweredAnnotation)"/> accepts any
/// <see cref="LoweredAnnotation"/> subclass.
/// </para>
/// <para>
/// <b>Threading.</b> The driver runs normalizers sequentially on one
/// thread; the builder is not designed for concurrent writes. Determinism
/// comes from the driver's stable normalizer ordering plus each normalizer's
/// stable node-visit order.
/// </para>
/// </remarks>
public sealed class NormalizedUnitBuilder
{
    private readonly Pass1Result _pass1;
    private readonly Dictionary<SyntaxNode, List<LoweredAnnotation>> _nodeAnnotations = new();
    private readonly Dictionary<Type, Dictionary<ISymbol, object>> _attached = new();
    private readonly Dictionary<Type, List<object>> _synthesized = new();
    private readonly List<DiagnosticRecord> _diagnostics = new();
    private bool _sealed;

    /// <summary>
    /// Construct a builder over the supplied Pass-1 result. Called by
    /// <see cref="Pass2Driver"/>; tests may construct one directly to drive
    /// a single normalizer in isolation.
    /// </summary>
    /// <param name="pass1">The Pass-1 result the normalizers annotate. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="pass1"/> is null.</exception>
    public NormalizedUnitBuilder(Pass1Result pass1)
    {
        ArgumentNullException.ThrowIfNull(pass1);
        _pass1 = pass1;
    }

    /// <summary>
    /// The Pass-1 result the normalizers annotate. Exposed so a normalizer
    /// reads the compilation, semantic models, trees, module name, and
    /// <see cref="Pass1Result.IsSimPath"/> flag from the builder it was
    /// handed.
    /// </summary>
    public Pass1Result Pass1 => _pass1;

    /// <summary>
    /// Record a per-node lowering decision. A node may accumulate multiple
    /// annotations (of the same or different concrete types); they are kept
    /// in record order.
    /// </summary>
    /// <param name="node">The original Roslyn node the decision applies to. Must not be null.</param>
    /// <param name="annotation">The lowering decision. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="node"/> or <paramref name="annotation"/> is null.</exception>
    /// <exception cref="InvalidOperationException">If the builder has already been sealed.</exception>
    public void AnnotateNode(SyntaxNode node, LoweredAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(annotation);
        ThrowIfSealed();

        if (!_nodeAnnotations.TryGetValue(node, out List<LoweredAnnotation>? list))
        {
            list = new List<LoweredAnnotation>();
            _nodeAnnotations.Add(node, list);
        }
        list.Add(annotation);
    }

    /// <summary>
    /// Attach a per-symbol synthesized value of type <typeparamref name="T"/>
    /// to <paramref name="key"/>. The value is retrievable through
    /// <see cref="NormalizedUnit.GetAttached{T}(ISymbol)"/>. A second attach
    /// of the same type to the same symbol overwrites the first (last-write
    /// wins) -- a normalizer should compute one final value per symbol.
    /// </summary>
    /// <typeparam name="T">The synthesized value type (defined in the attaching normalizer's own file).</typeparam>
    /// <param name="key">The owning symbol. Must not be null.</param>
    /// <param name="value">The value to attach. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="key"/> or <paramref name="value"/> is null.</exception>
    /// <exception cref="InvalidOperationException">If the builder has already been sealed.</exception>
    public void Attach<T>(ISymbol key, T value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        ThrowIfSealed();

        if (!_attached.TryGetValue(typeof(T), out Dictionary<ISymbol, object>? bySymbol))
        {
            bySymbol = new Dictionary<ISymbol, object>(SymbolEqualityComparer.Default);
            _attached.Add(typeof(T), bySymbol);
        }
        bySymbol[key] = value;
    }

    /// <summary>
    /// Append a module-scoped synthesized record of type
    /// <typeparamref name="T"/>. Records of the same type accumulate in
    /// append order, retrievable through
    /// <see cref="NormalizedUnit.GetSynthesized{T}"/>.
    /// </summary>
    /// <typeparam name="T">The synthesized record type (defined in the appending normalizer's own file).</typeparam>
    /// <param name="value">The record to append. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="value"/> is null.</exception>
    /// <exception cref="InvalidOperationException">If the builder has already been sealed.</exception>
    public void AddSynthesized<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ThrowIfSealed();

        if (!_synthesized.TryGetValue(typeof(T), out List<object>? list))
        {
            list = new List<object>();
            _synthesized.Add(typeof(T), list);
        }
        list.Add(value);
    }

    /// <summary>
    /// Record a Pass-2 diagnostic. Diagnostics accumulate in record order.
    /// </summary>
    /// <param name="diagnostic">The diagnostic to record. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="diagnostic"/> is null.</exception>
    /// <exception cref="InvalidOperationException">If the builder has already been sealed.</exception>
    public void AddDiagnostic(DiagnosticRecord diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        ThrowIfSealed();
        _diagnostics.Add(diagnostic);
    }

    /// <summary>
    /// Seal the builder and produce the immutable <see cref="NormalizedUnit"/>.
    /// After this call the builder rejects further writes. Called by
    /// <see cref="Pass2Driver"/> once every normalizer has run.
    /// </summary>
    /// <param name="pass1">
    /// The Pass-1 result to attach to the unit. Must reference-equal the
    /// result the builder was constructed over (a guard against threading a
    /// mismatched result through the seal).
    /// </param>
    /// <returns>The sealed, immutable normalized unit.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="pass1"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="pass1"/> is not the result the builder was constructed over.</exception>
    /// <exception cref="InvalidOperationException">If the builder has already been sealed.</exception>
    internal NormalizedUnit Build(Pass1Result pass1)
    {
        ArgumentNullException.ThrowIfNull(pass1);
        if (!ReferenceEquals(pass1, _pass1))
        {
            throw new ArgumentException(
                "Build must be called with the same Pass1Result the builder was constructed over.",
                nameof(pass1));
        }
        ThrowIfSealed();
        _sealed = true;

        // Snapshot the per-node annotations into read-only lists.
        Dictionary<SyntaxNode, IReadOnlyList<LoweredAnnotation>> nodeAnnotations =
            new(_nodeAnnotations.Count);
        foreach (KeyValuePair<SyntaxNode, List<LoweredAnnotation>> kv in _nodeAnnotations)
        {
            nodeAnnotations.Add(kv.Key, kv.Value.AsReadOnly());
        }

        // Snapshot the per-symbol attached bag into read-only inner maps.
        Dictionary<Type, IReadOnlyDictionary<ISymbol, object>> attached = new(_attached.Count);
        foreach (KeyValuePair<Type, Dictionary<ISymbol, object>> kv in _attached)
        {
            attached.Add(kv.Key, kv.Value);
        }

        // Snapshot the module-scoped synthesized lists into read-only lists.
        Dictionary<Type, IReadOnlyList<object>> synthesized = new(_synthesized.Count);
        foreach (KeyValuePair<Type, List<object>> kv in _synthesized)
        {
            synthesized.Add(kv.Key, kv.Value.AsReadOnly());
        }

        return new NormalizedUnit(
            _pass1,
            nodeAnnotations,
            attached,
            synthesized,
            _diagnostics.AsReadOnly());
    }

    private void ThrowIfSealed()
    {
        if (_sealed)
        {
            throw new InvalidOperationException(
                "The NormalizedUnitBuilder has been sealed; no further writes are permitted.");
        }
    }
}
