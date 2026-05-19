// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.Discovery;

/// <summary>
/// One discovered <c>.Build.toml</c> with the parsed
/// <see cref="ModuleRules"/> and the filesystem metadata.
/// </summary>
/// <param name="Rules">The parsed module descriptor.</param>
/// <param name="DescriptorPath">
/// Absolute path of the <c>.Build.toml</c> file on disk.
/// </param>
/// <param name="ContentHash">
/// BLAKE3 content hash of the descriptor file. Per
/// <c>/Documents/XBT.html</c> Section 15.1 the hash participates in
/// the TargetMakefile cache key.
/// </param>
/// <param name="OwningPluginName">
/// If the module was discovered under a plugin's <c>Source/</c>
/// directory, the plugin name. Null for tier-root modules under
/// <c>/Engine/Source/</c>, <c>/Studio/Source/</c>, or
/// <c>/Projects/&lt;P&gt;/Source/</c>.
/// </param>
public sealed record ModuleRecord(
    ModuleRules Rules,
    string DescriptorPath,
    IoHash ContentHash,
    string? OwningPluginName);

/// <summary>
/// Deduplicated module set after walking every tier's <c>Source/</c>
/// tree and every plugin's <c>Source/</c> tree for
/// <c>*.Build.toml</c> files. Built by
/// <see cref="ModuleEnumerator"/>.
/// </summary>
/// <remarks>
/// <para>
/// The catalog rejects duplicate module names with a
/// <see cref="DescriptorParseException"/> (exit code 30,
/// <c>RulesCompileFailed</c>) at construction time. Unlike plugins,
/// module names must be globally unique -- shadow precedence does
/// <em>not</em> apply at the module level. A duplicate name across
/// tiers is a hoisting mistake the developer must resolve.
/// </para>
/// <para>
/// The <see cref="Modules"/> list is stable-sorted alphabetically by
/// <see cref="ModuleRules.Name"/> so two enumerations of the same
/// repo produce byte-identical catalogs.
/// </para>
/// </remarks>
public sealed class ModuleCatalog
{
    private readonly Dictionary<string, ModuleRecord> _byName;

    /// <summary>
    /// All discovered modules, sorted alphabetically by name for
    /// determinism.
    /// </summary>
    public IReadOnlyList<ModuleRecord> Modules { get; }

    /// <summary>
    /// Look up a module by its declared name. Throws
    /// <see cref="KeyNotFoundException"/> if the name is not in the
    /// catalog.
    /// </summary>
    public ModuleRecord this[string name] => _byName[name];

    /// <summary>
    /// Try to fetch the module record for a given name. Returns false
    /// when the name is unknown (e.g. a dependency in
    /// <see cref="ModuleRules.PublicDependencyModuleNames"/> that
    /// references a module not present in this build).
    /// </summary>
    public bool TryGet(string name, out ModuleRecord record)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_byName.TryGetValue(name, out ModuleRecord? hit))
        {
            record = hit;
            return true;
        }
        record = null!;
        return false;
    }

    /// <summary>
    /// Build a catalog from the raw discovered records. Throws
    /// <see cref="DescriptorParseException"/> if two records share a
    /// module name.
    /// </summary>
    public ModuleCatalog(IEnumerable<ModuleRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        List<ModuleRecord> sorted = records
            .OrderBy(r => r.Rules.Name, StringComparer.Ordinal)
            .ThenBy(r => r.DescriptorPath, StringComparer.Ordinal)
            .ToList();

        _byName = new Dictionary<string, ModuleRecord>(StringComparer.Ordinal);

        foreach (ModuleRecord rec in sorted)
        {
            if (_byName.TryGetValue(rec.Rules.Name, out ModuleRecord? existing))
            {
                throw new DescriptorParseException(
                    $"Duplicate module name '{rec.Rules.Name}': declared at " +
                    $"{existing.DescriptorPath} (tier {existing.Rules.Tier}) and " +
                    $"{rec.DescriptorPath} (tier {rec.Rules.Tier}). Module names must be globally unique.",
                    filePath: rec.DescriptorPath);
            }
            _byName[rec.Rules.Name] = rec;
        }

        Modules = sorted;
    }

    /// <summary>
    /// Number of distinct modules in the catalog.
    /// </summary>
    public int Count => Modules.Count;

    /// <summary>
    /// Convenience resolver compatible with
    /// <see cref="TierValidator.ValidateModuleDeps"/>: maps a module
    /// name to its declared <see cref="ModuleTier"/>, or null when the
    /// name is unknown.
    /// </summary>
    public ModuleTier? ResolveTier(string moduleName)
    {
        return _byName.TryGetValue(moduleName, out ModuleRecord? r)
            ? r.Rules.Tier
            : null;
    }
}
