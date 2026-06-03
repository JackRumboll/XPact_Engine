// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XIL2CPP.Core;

namespace Simgenics.XPact.XIL2CPP.Analysis;

/// <summary>
/// The immutable product of XIL2CPP Pass 3 (semantic analysis + validation)
/// for one module per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2: the
/// gathered emit metadata (each analyzer's own result type, retrieved
/// generically) plus the Pass-3 diagnostics.
/// </summary>
/// <remarks>
/// <para>
/// <b>Parallel-safe result bag.</b> Each analyzer appends its OWN result
/// type (declared in the analyzer's own file) via the builder; results are
/// retrieved here by CLR type through <see cref="GetAll{T}"/> (append-list
/// slot) and <see cref="GetSingleton{T}"/> (single-value slot), so a later
/// agent can store + retrieve a brand-new result type WITHOUT editing this
/// file or <see cref="Pass3ResultBuilder"/>.
/// </para>
/// </remarks>
public sealed class Pass3Result
{
    // Per-type append lists (Add<T>). Keyed by the result value's CLR type.
    private readonly IReadOnlyDictionary<Type, IReadOnlyList<object>> _items;

    // Per-type single-value slots (SetSingleton<T>). Keyed by the value's
    // CLR type.
    private readonly IReadOnlyDictionary<Type, object> _singletons;

    /// <summary>
    /// Construct a Pass-3 result. Called only by
    /// <see cref="Pass3ResultBuilder.Build"/>; the collections are already
    /// sealed (read-only snapshots) by the builder.
    /// </summary>
    internal Pass3Result(
        IReadOnlyList<DiagnosticRecord> diagnostics,
        IReadOnlyDictionary<Type, IReadOnlyList<object>> items,
        IReadOnlyDictionary<Type, object> singletons)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(singletons);

        Diagnostics = diagnostics;
        _items = items;
        _singletons = singletons;

        bool hasErrors = false;
        foreach (DiagnosticRecord d in diagnostics)
        {
            if (d.Severity == DiagnosticSeverity.Error)
            {
                hasErrors = true;
                break;
            }
        }
        HasErrors = hasErrors;
    }

    /// <summary>
    /// The Pass-3 diagnostics the analyzers recorded, in collection order
    /// (analyzers run in deterministic <see cref="ISemanticAnalyzer.Name"/>
    /// order, and each appends in node-visit order).
    /// </summary>
    public IReadOnlyList<DiagnosticRecord> Diagnostics { get; }

    /// <summary>
    /// True iff any diagnostic in <see cref="Diagnostics"/> is
    /// error-severity. The CLI mode gates its exit code on this.
    /// </summary>
    public bool HasErrors { get; }

    /// <summary>
    /// Get every result of type <typeparamref name="T"/> an analyzer
    /// appended via <see cref="Pass3ResultBuilder.Add{T}(T)"/>, in append
    /// order. Returns an empty list when none were added.
    /// </summary>
    /// <typeparam name="T">The result type (defined in the appending analyzer's own file).</typeparam>
    /// <returns>The appended results (possibly empty, never null).</returns>
    public IReadOnlyList<T> GetAll<T>()
    {
        if (_items.TryGetValue(typeof(T), out IReadOnlyList<object>? list))
        {
            List<T> typed = new(list.Count);
            foreach (object o in list)
            {
                typed.Add((T)o);
            }
            return typed;
        }
        return Array.Empty<T>();
    }

    /// <summary>
    /// Get the single-value result of type <typeparamref name="T"/> an
    /// analyzer set via <see cref="Pass3ResultBuilder.SetSingleton{T}(T)"/>,
    /// or the default of <typeparamref name="T"/> when none was set.
    /// </summary>
    /// <typeparam name="T">The singleton result type.</typeparam>
    /// <returns>The singleton value, or <c>default</c>.</returns>
    public T? GetSingleton<T>()
    {
        if (_singletons.TryGetValue(typeof(T), out object? value))
        {
            return (T)value;
        }
        return default;
    }

    /// <summary>
    /// Try to get the single-value result of type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The singleton result type.</typeparam>
    /// <param name="value">Receives the singleton value when present.</param>
    /// <returns>True iff a singleton of type <typeparamref name="T"/> was set.</returns>
    public bool TryGetSingleton<T>(out T value)
    {
        if (_singletons.TryGetValue(typeof(T), out object? stored))
        {
            value = (T)stored;
            return true;
        }
        value = default!;
        return false;
    }
}
