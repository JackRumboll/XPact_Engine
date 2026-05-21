// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Simgenics.XPact.XHT.Core;

namespace Simgenics.XPact.XHT.Tables;

/// <summary>
/// Default thread-safe <see cref="ISpecifierRegistry"/> implementation.
/// Backed by a <see cref="ConcurrentDictionary{TKey, TValue}"/> keyed by the
/// case-folded specifier name (<c>StringComparer.OrdinalIgnoreCase</c>) per
/// <c>/Documents/XHT.html</c> Rev 8 Section 7.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Conflict-detection rule (Section 18.1, footgun #7).</b> When a second
/// registration arrives for an already-registered specifier name, the
/// registry compares the two <see cref="SpecifierDefinition"/> records
/// structurally:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Structurally identical</b> -- benign duplicate. Logged via
///     <see cref="Logger.Warning(string, object?[])"/> so the operator
///     notices the redundant registration but the load continues. Useful
///     when two plugin assemblies legitimately ship the same specifier
///     declaration (e.g., a base plugin + a derived plugin both registering
///     <c>Category</c>).
///   </description></item>
///   <item><description>
///     <b>Structurally different</b> -- collision. Throws
///     <see cref="InvalidOperationException"/>. The thrown message includes
///     both definitions so the operator can identify which plugin
///     introduced the conflict. The resolver maps this throw onto
///     diagnostic <c>XHT140</c>.
///   </description></item>
/// </list>
/// <para>
/// Identical re-registration is detected via record-value equality on
/// <see cref="SpecifierDefinition"/>; the C# compiler generates this
/// automatically for record types.
/// </para>
/// <para>
/// <b>Thread safety.</b> The registry is safe for concurrent reads + writes.
/// <see cref="Register(SpecifierDefinition)"/> uses
/// <see cref="ConcurrentDictionary{TKey, TValue}.TryAdd"/> + a follow-up
/// equality check; in the rare case where two threads race to register the
/// same definition concurrently, the second arrival sees the first's entry
/// and falls into the identical-duplicate code path.
/// </para>
/// </remarks>
public sealed class SpecifierRegistry : ISpecifierRegistry
{
    private readonly ConcurrentDictionary<string, SpecifierDefinition> _byName
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Construct a registry, optionally pre-populated with the Phase-1
    /// locked specifier vocabulary from <see cref="BuiltInSpecifiers"/>.
    /// </summary>
    /// <param name="registerBuiltIns">
    /// When true (default) the built-in specifier set is registered
    /// immediately. Tests pass false when they want to exercise the
    /// registry surface in isolation without the locked vocabulary.
    /// </param>
    public SpecifierRegistry(bool registerBuiltIns = true)
    {
        if (registerBuiltIns)
        {
            BuiltInSpecifiers.RegisterAllInto(this);
        }
    }

    /// <inheritdoc />
    public void Register(SpecifierDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);

        // TryAdd succeeds on the common path (first registration).
        if (_byName.TryAdd(def.Name, def))
        {
            return;
        }

        // Already present. Compare structurally via record-value equality.
        SpecifierDefinition existing = _byName[def.Name];
        if (existing == def)
        {
            // Identical re-registration: benign. Surface a warning so the
            // operator notices and can clean up the duplicate plugin
            // declaration on their schedule.
            Logger.Warning(
                "Specifier '{0}' re-registered with structurally identical definition; benign duplicate.",
                def.Name);
            return;
        }

        // Per M5 audit (XHT.html Section 18.1): the AllowOverride flag
        // gates plugin-extensibility. When BOTH the existing entry AND
        // the incoming one set AllowOverride, the incoming wins. This
        // lets a derived plugin extend / replace a base plugin's
        // specifier without requiring the base to be unloaded.
        if (existing.AllowOverride && def.AllowOverride)
        {
            Logger.Warning(
                "Specifier '{0}' replaced via AllowOverride (both definitions opted in).",
                def.Name);
            _byName[def.Name] = def;
            return;
        }

        // Conflict: two definitions with the same name but different
        // shapes. This is XHT140 territory. Throw a descriptive exception;
        // the resolver / plugin-loader maps this onto the XHT140 diagnostic.
        throw new InvalidOperationException(
            $"Specifier conflict on name '{def.Name}': "
            + $"existing=(ApplicableTo={existing.ApplicableTo}, ValueKind={existing.ValueKind}, "
            + $"AllowMultiple={existing.AllowMultiple}, AllowOverride={existing.AllowOverride}), "
            + $"attempted=(ApplicableTo={def.ApplicableTo}, ValueKind={def.ValueKind}, "
            + $"AllowMultiple={def.AllowMultiple}, AllowOverride={def.AllowOverride}). "
            + "Two specifiers with the same name must be structurally identical "
            + "or both must set AllowOverride=true. See XHT140.");
    }

    /// <inheritdoc />
    public SpecifierDefinition? Resolve(string name, SpecifierContext context)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_byName.TryGetValue(name, out SpecifierDefinition? def))
        {
            return null;
        }

        return (def.ApplicableTo & context) != SpecifierContext.None ? def : null;
    }

    /// <inheritdoc />
    public bool TryResolve(string name, SpecifierContext context, [NotNullWhen(true)] out SpecifierDefinition? def)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_byName.TryGetValue(name, out SpecifierDefinition? candidate)
            && (candidate.ApplicableTo & context) != SpecifierContext.None)
        {
            def = candidate;
            return true;
        }

        def = null;
        return false;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<SpecifierDefinition> AllSpecifiers
        => (IReadOnlyCollection<SpecifierDefinition>)_byName.Values;
}
