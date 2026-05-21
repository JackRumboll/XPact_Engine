// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Simgenics.XPact.XHT.Tables;

/// <summary>
/// Read / write surface of the specifier registry per
/// <c>/Documents/XHT.html</c> Rev 5 Section 7 + Section 18.1. The default
/// implementation is <see cref="SpecifierRegistry"/>; the interface exists so
/// the parser (Phase 1c.2) and the resolver / validator passes (Phase 1d)
/// can take a dependency on the registry abstraction rather than the
/// concrete class, allowing tests to substitute alternative implementations
/// and the plugin model to evolve without breaking call-sites.
/// </summary>
/// <remarks>
/// <para>
/// <b>Case sensitivity (Section 7.2).</b> Specifier name lookup is case-
/// insensitive, mirroring UHT's <c>UhtSpecifierTable</c> precedent and the
/// engine convention that <c>blueprintReadWrite</c>, <c>BlueprintReadWrite</c>,
/// and <c>BLUEPRINTREADWRITE</c> all bind to the same handler. Implementations
/// key by <c>name.ToLowerInvariant()</c>.
/// </para>
/// <para>
/// <b>Context intersection (Section 18.1).</b> <see cref="Resolve(string, SpecifierContext)"/>
/// returns the registered definition iff the caller-supplied <c>context</c>
/// intersects the definition's
/// <see cref="SpecifierDefinition.ApplicableTo"/> mask. Callers (the parser)
/// emit <c>XHT110</c> on miss and <c>XHT111</c> on a context-mismatch hit;
/// the registry surface itself does not emit diagnostics.
/// </para>
/// </remarks>
public interface ISpecifierRegistry
{
    /// <summary>
    /// Register <paramref name="def"/> into the registry. Phase-2 plugins
    /// invoke this at plugin-load time; <see cref="BuiltInSpecifiers"/>
    /// bulk-registers the Phase-1 locked vocabulary at process start.
    /// </summary>
    /// <param name="def">The specifier to register. Must not be null.</param>
    /// <exception cref="System.ArgumentNullException">If <paramref name="def"/> is null.</exception>
    /// <exception cref="System.InvalidOperationException">
    /// If a specifier with the same case-folded name is already registered
    /// and the new definition is structurally different (collision per
    /// <c>XHT140</c>). Identical re-registration is benign and is warned via
    /// <see cref="Simgenics.XPact.XHT.Core.Logger"/> rather than thrown.
    /// </exception>
    void Register(SpecifierDefinition def);

    /// <summary>
    /// Resolve a specifier by case-insensitive name within the supplied
    /// <paramref name="context"/>. Returns null on miss or context mismatch.
    /// </summary>
    /// <param name="name">
    /// Specifier name as authored. The lookup is case-insensitive per
    /// Section 7.2; <see langword="null"/> throws.
    /// </param>
    /// <param name="context">
    /// The currently-active syntactic context. Must be a single context or
    /// any subset; a definition matches when
    /// <c>(def.ApplicableTo &amp; context) != 0</c>.
    /// </param>
    /// <returns>
    /// The matching definition, or null when no specifier with this name is
    /// registered or when the registered specifier's
    /// <see cref="SpecifierDefinition.ApplicableTo"/> does not intersect
    /// <paramref name="context"/>.
    /// </returns>
    /// <exception cref="System.ArgumentNullException">If <paramref name="name"/> is null.</exception>
    SpecifierDefinition? Resolve(string name, SpecifierContext context);

    /// <summary>
    /// Try-pattern variant of <see cref="Resolve(string, SpecifierContext)"/>.
    /// </summary>
    /// <param name="name">Specifier name as authored. Must not be null.</param>
    /// <param name="context">The currently-active syntactic context.</param>
    /// <param name="def">The matched definition on success; null on miss.</param>
    /// <returns>True on hit; false on miss or context mismatch.</returns>
    /// <exception cref="System.ArgumentNullException">If <paramref name="name"/> is null.</exception>
    bool TryResolve(string name, SpecifierContext context, [NotNullWhen(true)] out SpecifierDefinition? def);

    /// <summary>
    /// Snapshot of every registered specifier definition. The order is
    /// unspecified; callers requiring deterministic ordering must sort the
    /// result themselves.
    /// </summary>
    IReadOnlyCollection<SpecifierDefinition> AllSpecifiers { get; }
}
