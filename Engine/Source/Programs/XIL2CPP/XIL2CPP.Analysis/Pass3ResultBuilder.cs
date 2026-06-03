// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Normalization;

namespace Simgenics.XPact.XIL2CPP.Analysis;

/// <summary>
/// The mutable accumulator the Pass-3 analyzers write into. One builder is
/// shared across every analyzer for a module; <see cref="Pass3Driver"/>
/// threads it through the discovered <see cref="ISemanticAnalyzer"/>s in
/// deterministic order, then seals it into an immutable
/// <see cref="Pass3Result"/> via <see cref="Build"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Parallel-safe extensibility (the whole point).</b> The append surface
/// <see cref="Add{T}(T)"/> and the single-value surface
/// <see cref="SetSingleton{T}(T)"/> are generic and keyed by the value's
/// CLR type, so a later agent can introduce a brand-new result type
/// (declared in that agent's own file) and store / retrieve it WITHOUT
/// editing this builder or <see cref="Pass3Result"/>.
/// </para>
/// <para>
/// <b>Threading.</b> The driver runs analyzers sequentially on one thread;
/// the builder is not designed for concurrent writes. Determinism comes
/// from the driver's stable analyzer ordering plus each analyzer's stable
/// visit order.
/// </para>
/// </remarks>
public sealed class Pass3ResultBuilder
{
    private readonly NormalizedUnit _unit;
    private readonly List<DiagnosticRecord> _diagnostics = new();
    private readonly Dictionary<Type, List<object>> _items = new();
    private readonly Dictionary<Type, object> _singletons = new();
    private bool _sealed;

    /// <summary>
    /// Construct a builder over the supplied normalized unit. Called by
    /// <see cref="Pass3Driver"/>; tests may construct one directly to drive
    /// a single analyzer in isolation.
    /// </summary>
    /// <param name="unit">The normalized unit the analyzers consume. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="unit"/> is null.</exception>
    public Pass3ResultBuilder(NormalizedUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        _unit = unit;
    }

    /// <summary>
    /// The normalized unit the analyzers consume. Exposed so an analyzer
    /// reads the Pass-2 annotations + the authoritative Pass-1 binding info
    /// from the builder it was handed.
    /// </summary>
    public NormalizedUnit Unit => _unit;

    /// <summary>
    /// Record a Pass-3 diagnostic. Diagnostics accumulate in record order.
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
    /// Append a result record of type <typeparamref name="T"/>. Records of
    /// the same type accumulate in append order, retrievable through
    /// <see cref="Pass3Result.GetAll{T}"/>.
    /// </summary>
    /// <typeparam name="T">The result type (defined in the appending analyzer's own file).</typeparam>
    /// <param name="item">The result to append. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="item"/> is null.</exception>
    /// <exception cref="InvalidOperationException">If the builder has already been sealed.</exception>
    public void Add<T>(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ThrowIfSealed();

        if (!_items.TryGetValue(typeof(T), out List<object>? list))
        {
            list = new List<object>();
            _items.Add(typeof(T), list);
        }
        list.Add(item);
    }

    /// <summary>
    /// Set the single-value result slot for type <typeparamref name="T"/>.
    /// A second set of the same type overwrites the first (last-write wins)
    /// -- an analyzer that owns a singleton should set it once.
    /// </summary>
    /// <typeparam name="T">The singleton result type (defined in the setting analyzer's own file).</typeparam>
    /// <param name="value">The value to set. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="value"/> is null.</exception>
    /// <exception cref="InvalidOperationException">If the builder has already been sealed.</exception>
    public void SetSingleton<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ThrowIfSealed();
        _singletons[typeof(T)] = value;
    }

    /// <summary>
    /// Seal the builder and produce the immutable <see cref="Pass3Result"/>.
    /// After this call the builder rejects further writes. Called by
    /// <see cref="Pass3Driver"/> once every analyzer has run.
    /// </summary>
    /// <param name="unit">
    /// The normalized unit to attach to the result. Must reference-equal the
    /// unit the builder was constructed over (a guard against threading a
    /// mismatched unit through the seal).
    /// </param>
    /// <returns>The sealed, immutable Pass-3 result.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="unit"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="unit"/> is not the unit the builder was constructed over.</exception>
    /// <exception cref="InvalidOperationException">If the builder has already been sealed.</exception>
    internal Pass3Result Build(NormalizedUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!ReferenceEquals(unit, _unit))
        {
            throw new ArgumentException(
                "Build must be called with the same NormalizedUnit the builder was constructed over.",
                nameof(unit));
        }
        ThrowIfSealed();
        _sealed = true;

        // Snapshot the per-type append lists into read-only lists.
        Dictionary<Type, IReadOnlyList<object>> items = new(_items.Count);
        foreach (KeyValuePair<Type, List<object>> kv in _items)
        {
            items.Add(kv.Key, kv.Value.AsReadOnly());
        }

        // The singletons dictionary is already a flat type->object map;
        // snapshot it so post-seal builder state cannot leak into the result.
        Dictionary<Type, object> singletons = new(_singletons);

        return new Pass3Result(
            _diagnostics.AsReadOnly(),
            items,
            singletons);
    }

    private void ThrowIfSealed()
    {
        if (_sealed)
        {
            throw new InvalidOperationException(
                "The Pass3ResultBuilder has been sealed; no further writes are permitted.");
        }
    }
}
