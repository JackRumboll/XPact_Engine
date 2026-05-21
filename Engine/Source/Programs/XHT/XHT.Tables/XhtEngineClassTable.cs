// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Simgenics.XPact.XHT.Tables;

/// <summary>
/// Anchor-type table per <c>/Documents/XHT.html</c> Rev 7 Section 2 (the
/// XHT.Tables module description -- <c>XhtEngineClassTable</c>: "core
/// engine roles -- XObject, XClass, XStruct, XInterface -- mapped
/// to their reflected representations; Rev 2 addition mirroring UHT's
/// UhtEngineClassTable").
/// </summary>
/// <remarks>
/// <para>
/// <b>What this table is for.</b> The resolver (Phase 1d) consults this
/// table to detect whether a parsed identifier names a special engine type.
/// When the answer is yes, the resolver applies special handling: the
/// inheritance walk skips the anchor (it has no super), the descriptor
/// emission emits the intrinsic form, validators that depend on the
/// hierarchy root know where to stop. UHT does the same thing at
/// <c>UhtSession.cs</c> via <c>UhtEngineClassTable</c>.
/// </para>
/// <para>
/// <b>Case sensitivity.</b> Lookups are <em>exact</em> (case-sensitive)
/// because the engine-anchor names are reserved identifiers per the XPact
/// reflection-vocabulary commitment (Contract Section 1 -- the C++
/// naming row pins <c>XObject</c> / <c>XClass</c> etc. exactly).
/// <c>xobject</c> does NOT match <c>XObject</c>. The Rev 2 specifier-name
/// case-insensitivity (Section 7.2) applies only to specifier name lookup,
/// not to type-anchor lookup.
/// </para>
/// <para>
/// <b>Frozen backing store.</b> The table is built once at static init via
/// <see cref="FrozenDictionary"/> and never mutated; lookups are constant-
/// time and lock-free.
/// </para>
/// </remarks>
public static class XhtEngineClassTable
{
    private static readonly IReadOnlyList<(string Name, EngineClassRole Role)> s_all = BuildAll();

    private static readonly FrozenDictionary<string, EngineClassRole> s_byName
        = BuildFrozenIndex(s_all);

    /// <summary>
    /// Snapshot of every engine-anchor entry as a <c>(Name, Role)</c> pair
    /// in declaration order. Callers wanting deterministic iteration order
    /// rely on this.
    /// </summary>
    public static IReadOnlyList<(string Name, EngineClassRole Role)> All => s_all;

    /// <summary>
    /// Look up an engine-anchor role by exact name. Returns null when the
    /// name is not an anchor.
    /// </summary>
    /// <param name="typeName">
    /// Candidate type name (e.g. <c>"XObject"</c>). Lookup is exact (case-
    /// sensitive); <see langword="null"/> throws.
    /// </param>
    /// <returns>
    /// The matched <see cref="EngineClassRole"/>, or null when no anchor
    /// uses this exact name.
    /// </returns>
    /// <exception cref="ArgumentNullException">If <paramref name="typeName"/> is null.</exception>
    public static EngineClassRole? Lookup(string typeName)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        return s_byName.TryGetValue(typeName, out EngineClassRole role) ? role : null;
    }

    /// <summary>
    /// Returns true iff <paramref name="typeName"/> is one of the engine
    /// anchor names. Convenience over a non-null check on
    /// <see cref="Lookup(string)"/>.
    /// </summary>
    /// <param name="typeName">Candidate type name. Must not be null.</param>
    /// <returns>True when the name matches an anchor exactly.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="typeName"/> is null.</exception>
    public static bool IsEngineClass(string typeName)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        return s_byName.ContainsKey(typeName);
    }

    private static IReadOnlyList<(string Name, EngineClassRole Role)> BuildAll()
    {
        return new List<(string, EngineClassRole)>
        {
            ("XObject", EngineClassRole.XObject),
            ("XClass", EngineClassRole.XClass),
            ("XStruct", EngineClassRole.XStruct),
            ("XInterface", EngineClassRole.XInterface),
            ("XEnum", EngineClassRole.XEnum),
            ("XFunction", EngineClassRole.XFunction),
            ("XProperty", EngineClassRole.XProperty),
        };
    }

    private static FrozenDictionary<string, EngineClassRole> BuildFrozenIndex(
        IReadOnlyList<(string Name, EngineClassRole Role)> all)
    {
        Dictionary<string, EngineClassRole> index = new(all.Count, StringComparer.Ordinal);
        foreach ((string name, EngineClassRole role) in all)
        {
            index.Add(name, role);
        }
        return index.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
