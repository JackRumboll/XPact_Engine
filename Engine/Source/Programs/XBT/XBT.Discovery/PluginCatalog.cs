// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.Discovery;

/// <summary>
/// A single discovered <c>.xplugin</c> file with its filesystem
/// metadata. Multiple instances of the same plugin name can coexist in
/// a <see cref="PluginCatalog"/>; the catalog resolves shadow
/// precedence and records all observed records so cache invalidation
/// behaves correctly.
/// </summary>
/// <param name="Descriptor">The parsed plugin descriptor.</param>
/// <param name="Tier">
/// The tier the descriptor was discovered under, derived from its
/// filesystem path: Engine plugins under <c>/Engine/Plugins/</c>,
/// Studio plugins under <c>/Studio/Plugins/</c>, Project plugins
/// under <c>/Projects/&lt;P&gt;/Plugins/</c>.
/// </param>
/// <param name="DescriptorPath">
/// Absolute path of the <c>.xplugin</c> file on disk. Used by the
/// cache-key construction in the makefile (XBT.html Section 15.1) so
/// editing a non-shadowed descriptor still invalidates the cache.
/// </param>
/// <param name="ContentHash">
/// BLAKE3 content hash of the descriptor file. Used as the cache-key
/// fragment per <c>/Documents/XBT.html</c> Section 17.2.
/// </param>
public sealed record PluginRecord(
    PluginDescriptor Descriptor,
    ModuleTier Tier,
    string DescriptorPath,
    IoHash ContentHash);

/// <summary>
/// One plugin's deduplicated record set with shadow precedence
/// resolved. Per <c>/Documents/XBT.html</c> Rev 4 Section 17.2 + Rev 2
/// audit finding #8: <strong>records ALL observed plugin descriptors
/// of each name, tier-tagged, with the winner explicitly noted</strong>
/// so a hot-rebuild does not spuriously rebuild when a non-shadowed
/// plugin elsewhere is touched.
/// </summary>
/// <param name="Name">Plugin name. Same across <see cref="Resolved"/> and <see cref="Shadowed"/>.</param>
/// <param name="Resolved">
/// The winning descriptor (highest tier). Per the precedence rule:
/// Project &gt; Studio &gt; Engine.
/// </param>
/// <param name="Shadowed">
/// Every other observed descriptor of this name, ordered with the
/// closest-to-Project tier first. Includes content hashes for cache-key
/// stability.
/// </param>
public sealed record PluginEntry(
    string Name,
    PluginRecord Resolved,
    IReadOnlyList<PluginRecord> Shadowed);

/// <summary>
/// Deduplicated plugin set after walking the three plugin roots. Built
/// by <see cref="PluginEnumerator"/>.
/// </summary>
/// <remarks>
/// <para>
/// The catalog is <strong>order-stable</strong> per
/// <c>/Documents/XBT.html</c> Section 22.7: two enumerations of the
/// same repo produce byte-identical catalogs, even across machines.
/// Plugin entries are sorted alphabetically by name; the shadow list
/// within each entry is sorted by tier (Project, then Studio, then
/// Engine) so the winning resolution is always at the head.
/// </para>
/// <para>
/// <b>Why record all observed records?</b> The Rev 1 design recorded
/// only the winning descriptor in the makefile cache key. The Rev 2
/// audit identified this as a cache-correctness bug: a developer
/// editing the Studio-tier <c>HMIPanels.xplugin</c> would not
/// invalidate the makefile because the Project-tier shadow was the
/// only descriptor in the cache key. Recording all observed records
/// fixes this.
/// </para>
/// </remarks>
public sealed class PluginCatalog
{
    private readonly Dictionary<string, PluginEntry> _entries;

    /// <summary>
    /// All plugin entries, ordered alphabetically by name for
    /// determinism.
    /// </summary>
    public IReadOnlyList<PluginEntry> Entries { get; }

    /// <summary>
    /// Try to fetch the entry for a given plugin name. Returns false
    /// when the plugin is not in the catalog (e.g. it was disabled via
    /// the project's <c>.xproject</c> before discovery, or was never
    /// present).
    /// </summary>
    public bool TryGet(string name, out PluginEntry entry)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_entries.TryGetValue(name, out PluginEntry? hit))
        {
            entry = hit;
            return true;
        }
        entry = null!;
        return false;
    }

    /// <summary>
    /// Build a catalog from the raw discovered records.
    /// </summary>
    /// <param name="records">
    /// Every observed <see cref="PluginRecord"/>. The catalog groups
    /// them by name, applies shadow precedence, and stores the result.
    /// </param>
    public PluginCatalog(IEnumerable<PluginRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        Dictionary<string, List<PluginRecord>> grouped = new(StringComparer.Ordinal);
        foreach (PluginRecord rec in records)
        {
            if (!grouped.TryGetValue(rec.Descriptor.Name, out List<PluginRecord>? list))
            {
                list = new List<PluginRecord>();
                grouped[rec.Descriptor.Name] = list;
            }
            list.Add(rec);
        }

        List<PluginEntry> entries = new(grouped.Count);
        foreach (string name in grouped.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            List<PluginRecord> list = grouped[name];

            // Shadow precedence: highest tier wins. ModuleTier ordinals
            // ascend from Engine=0 -> Studio=1 -> Project=2; the
            // resolved record is the one with the maximum ordinal.
            // Within the same tier, the descriptor with the
            // alphabetically-first path wins (a deterministic tiebreak
            // for hypothetical duplicate plugins, which is unusual but
            // not impossible at large repo scale).
            List<PluginRecord> sorted = list
                .OrderByDescending(r => (int)r.Tier)
                .ThenBy(r => r.DescriptorPath, StringComparer.Ordinal)
                .ToList();

            PluginRecord resolved = sorted[0];
            List<PluginRecord> shadowed = sorted.Count > 1
                ? sorted.GetRange(1, sorted.Count - 1)
                : new List<PluginRecord>();

            entries.Add(new PluginEntry(
                Name: name,
                Resolved: resolved,
                Shadowed: shadowed));
        }

        Entries = entries;
        _entries = entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// Number of distinct plugin names in the catalog.
    /// </summary>
    public int Count => Entries.Count;
}
