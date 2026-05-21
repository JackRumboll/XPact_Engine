// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Simgenics.XPact.XHT.Core;

namespace Simgenics.XPact.XHT.AST;

/// <summary>
/// Caseless lookup of reflected types by engine-name per
/// <c>/Documents/XHT.html</c> Rev 7 Section 3.3 + Section 5.4.
/// </summary>
/// <remarks>
/// <para>
/// <b>Caseless engine-name model (Round-2).</b> The engine-name is the
/// lowercase-invariant form of the source identifier. XPact's permanent
/// <c>X</c> prefix is preserved -- no UE-convention single-letter strip
/// is applied. (The legacy A / U / I / F prefix-strip rule was removed
/// per the 2026-05-21 user directive: XPact engine source uses only the
/// permanent <c>X</c> prefix, not the UE-convention single-letter
/// adornments.)
/// </para>
/// <para>
/// <b>Worked example.</b> A C++ class declared as
/// <c>class XValve : public XActor</c> and a C# class declared as
/// <c>[XClass] public partial class XValve : XActor</c> both register
/// under the engine name <c>"xvalve"</c>; cross-language pairing
/// requires the engine names to match exactly. A C# class declared as
/// <c>public partial class Valve</c> registers under <c>"valve"</c>
/// (different engine name; no implicit pairing).
/// </para>
/// <para>
/// <b>Thread safety.</b> The backing store is a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> so the parser's
/// per-header parallel pass (Section 11.1) and the resolver's per-header
/// parallel <c>StepPopulateTypeTable</c> phase can register types from
/// multiple worker threads concurrently. <see cref="Register"/> uses
/// <c>TryAdd</c> and throws <see cref="InvalidOperationException"/> on a
/// caseless collision so two distinct types cannot silently shadow each
/// other; the resolver maps the throw onto an <c>XHT040</c>-style
/// diagnostic in later phases.
/// </para>
/// </remarks>
public sealed class SymbolTable
{
    private readonly ConcurrentDictionary<string, XhtTypeBase> _byCaselessKey
        = new(StringComparer.Ordinal);

    /// <summary>
    /// Register <paramref name="type"/> under its
    /// <see cref="XhtTypeBase.CaselessKey"/>. Throws on caseless
    /// collision.
    /// </summary>
    /// <param name="type">The reflected type to register. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="type"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// If a type with the same caseless engine-name is already
    /// registered. The resolver translates this into an <c>XHT040</c>-
    /// style diagnostic naming both colliding source identifiers in
    /// Phase 1c+.
    /// </exception>
    public void Register(XhtTypeBase type)
    {
        ArgumentNullException.ThrowIfNull(type);

        string key = type.CaselessKey;
        if (!_byCaselessKey.TryAdd(key, type))
        {
            XhtTypeBase existing = _byCaselessKey[key];
            throw new InvalidOperationException(
                $"Caseless symbol-table collision on engine name '{key}': "
                + $"existing='{existing.Name}' (module '{existing.ModuleName}'), "
                + $"attempted='{type.Name}' (module '{type.ModuleName}').");
        }
    }

    /// <summary>
    /// Look up a reflected type by source identifier. The identifier is
    /// lowercased before the dictionary read; the permanent
    /// <c>X</c> prefix and any other characters are preserved (no
    /// UE-convention prefix-strip).
    /// </summary>
    /// <param name="identifier">
    /// Source identifier as authored. Lookup is case-insensitive
    /// (<c>"XValve"</c>, <c>"xvalve"</c>, and <c>"XVALVE"</c> all hit
    /// the same entry).
    /// </param>
    /// <returns>The registered type, or null when no match is found.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="identifier"/> is null.</exception>
    public XhtTypeBase? Lookup(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        string key = StringUtils.ToCaselessKey(identifier);
        return _byCaselessKey.TryGetValue(key, out XhtTypeBase? value) ? value : null;
    }

    /// <summary>
    /// Try-pattern variant of <see cref="Lookup"/> returning a boolean
    /// success flag and the resolved type via an out parameter.
    /// </summary>
    /// <param name="identifier">Source identifier as authored. Must not be null.</param>
    /// <param name="type">The registered type on success; null on miss.</param>
    /// <returns>True when the lookup hit; false when no match was found.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="identifier"/> is null.</exception>
    public bool TryLookup(string identifier, [NotNullWhen(true)] out XhtTypeBase? type)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        string key = StringUtils.ToCaselessKey(identifier);
        return _byCaselessKey.TryGetValue(key, out type);
    }

    /// <summary>
    /// Enumerate every registered type. Used by the resolver to walk the
    /// symbol set during the inheritance-graph build phases per
    /// Section 5.1.
    /// </summary>
    /// <remarks>
    /// The enumeration order is unspecified (it follows the underlying
    /// <see cref="ConcurrentDictionary{TKey, TValue}"/> snapshot). Callers
    /// requiring determinism must sort the result themselves.
    /// </remarks>
    public IEnumerable<XhtTypeBase> AllTypes => _byCaselessKey.Values;

    /// <summary>
    /// Replace an existing entry with a different
    /// <see cref="XhtTypeBase"/> sharing the same
    /// <see cref="XhtTypeBase.CaselessKey"/>. Used by the resolver's
    /// partial-class merge phase (C3 audit) to install the merged
    /// shape in place of the first-registered canonical. Throws if no
    /// existing entry is found OR if the replacement's caseless key
    /// differs from the original.
    /// </summary>
    /// <param name="original">The currently-registered entry. Must not be null.</param>
    /// <param name="replacement">The replacement. Must not be null; CaselessKey must match.</param>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    /// <exception cref="InvalidOperationException">If <paramref name="original"/> is not registered or <paramref name="replacement"/>'s caseless key differs.</exception>
    public void Replace(XhtTypeBase original, XhtTypeBase replacement)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(replacement);

        string key = original.CaselessKey;
        if (!string.Equals(key, replacement.CaselessKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Replace requires matching caseless keys: original='{key}', replacement='{replacement.CaselessKey}'.");
        }

        if (!_byCaselessKey.TryUpdate(key, replacement, original))
        {
            throw new InvalidOperationException(
                $"Replace target '{original.Name}' (key '{key}') is not the currently-registered entry.");
        }
    }

    /// <summary>The number of registered types.</summary>
    public int Count => _byCaselessKey.Count;
}
