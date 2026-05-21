// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Simgenics.XPact.XHT.Core;

namespace Simgenics.XPact.XHT.AST;

/// <summary>
/// Caseless lookup of reflected types by engine-name per
/// <c>/Documents/XHT.html</c> Rev 5 Section 3.3 + Section 5.4.
/// </summary>
/// <remarks>
/// <para>
/// <b>Caseless engine-name model.</b> Identifiers are normalised through
/// two passes before they key the table:
/// </para>
/// <list type="number">
///   <item><description>
///     <see cref="StringUtils.StripCppPrefix(string)"/> removes a single
///     leading UE-convention letter (<c>A</c> / <c>U</c> / <c>I</c> /
///     <c>F</c>) when followed by another uppercase letter. The XPact
///     <c>X</c> prefix is preserved (it is the permanent project prefix,
///     not a UE convention prefix per Section 3.3).
///   </description></item>
///   <item><description>
///     <see cref="StringUtils.ToCaselessKey(string)"/> lowercases the
///     result via <c>ToLowerInvariant</c>.
///   </description></item>
/// </list>
/// <para>
/// <b>Worked example.</b> A C++ class declared as
/// <c>class AXValve : public AXActor</c> registers under the key
/// <c>"xvalve"</c> (strip leading <c>A</c>, lowercase). A matching C#
/// class declared as <c>[XClass] public partial class Valve : Actor</c>
/// registers under <c>"valve"</c> (no UE prefix to strip, lowercase) --
/// note these are <em>different</em> keys; cross-language pairing per
/// Section 3.3 requires the engine names to match exactly. The
/// recommended C++ form (<c>class XValve : public XActor</c>) and the
/// C# form (<c>partial class Valve</c>) both fold to <c>"valve"</c> and
/// therefore pair through this table.
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
    /// stripped of its UE-convention prefix and lowercased before the
    /// dictionary read.
    /// </summary>
    /// <param name="identifier">
    /// Source identifier as authored. Both C++ forms (<c>"AXValve"</c>)
    /// and C# forms (<c>"Valve"</c>) work because the caseless-key
    /// normalisation applies to both.
    /// </param>
    /// <returns>The registered type, or null when no match is found.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="identifier"/> is null.</exception>
    public XhtTypeBase? Lookup(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        string key = StringUtils.ToCaselessKey(StringUtils.StripCppPrefix(identifier));
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

        string key = StringUtils.ToCaselessKey(StringUtils.StripCppPrefix(identifier));
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

    /// <summary>The number of registered types.</summary>
    public int Count => _byCaselessKey.Count;
}
