// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.Discovery;

/// <summary>
/// Walks the three plugin roots (<c>/Engine/Plugins/</c>,
/// <c>/Studio/Plugins/</c>, <c>/Projects/&lt;P&gt;/Plugins/</c>)
/// recursively for <c>*.xplugin</c> files. Per
/// <c>/Documents/XBT.html</c> Rev 4 Section 17.1.
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency.</b> Each root walks in parallel per XBT.html
/// Section 17.1. Per-root results aggregate into a
/// <see cref="ConcurrentBag{T}"/> and are collated alphabetically
/// before the catalog is built. The catalog's own ordering is
/// stable (alphabetical by name) so two enumerations produce
/// byte-identical catalogs.
/// </para>
/// <para>
/// <b>Failure isolation.</b> A descriptor that fails to parse does
/// not abort the entire walk: the failure is reported through
/// <see cref="DiscoveryDiagnostics"/> and the offending descriptor is
/// omitted. Callers receive the catalog of the descriptors that
/// parsed successfully; the catching site decides whether to proceed
/// (typical for tooling that just lists what is on disk) or to fail
/// the build (the normal compile/link path).
/// </para>
/// </remarks>
public static class PluginEnumerator
{
    /// <summary>
    /// Standard plugin filename suffix.
    /// </summary>
    public const string PluginExtension = ".xplugin";

    /// <summary>
    /// Enumerate every <c>.xplugin</c> reachable from the three plugin
    /// roots and assemble a <see cref="PluginCatalog"/>.
    /// </summary>
    /// <param name="engineRoot">
    /// Absolute path to <c>/Engine/</c>. May be null to skip the
    /// Engine root entirely (tests).
    /// </param>
    /// <param name="studioRoot">
    /// Absolute path to <c>/Studio/</c>. May be null to skip.
    /// </param>
    /// <param name="projectRoots">
    /// Absolute paths to project directories (each containing a
    /// <c>Plugins/</c> subtree). May be empty or null to skip.
    /// </param>
    /// <param name="diagnostics">
    /// Sink for parse-failure / EngineVersion-validation diagnostics.
    /// Pass <see cref="DiscoveryDiagnostics.Default"/> in production
    /// to route through XBT's <c>Logger</c>; pass a stub in tests.
    /// </param>
    /// <returns>
    /// A <see cref="PluginCatalog"/> with shadow precedence resolved.
    /// </returns>
    public static PluginCatalog Enumerate(
        string? engineRoot,
        string? studioRoot,
        IEnumerable<string>? projectRoots,
        IDiscoveryDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        ConcurrentBag<PluginRecord> records = new();

        List<(string Root, ModuleTier Tier)> tasks = new();
        if (engineRoot is not null)
        {
            tasks.Add((Path.Combine(engineRoot, "Plugins"), ModuleTier.Engine));
        }
        if (studioRoot is not null)
        {
            tasks.Add((Path.Combine(studioRoot, "Plugins"), ModuleTier.Studio));
        }
        if (projectRoots is not null)
        {
            foreach (string projRoot in projectRoots)
            {
                tasks.Add((Path.Combine(projRoot, "Plugins"), ModuleTier.Project));
            }
        }

        // Parallel walk per XBT.html Section 17.1.
        Parallel.ForEach(tasks, task =>
        {
            EnumerateRoot(task.Root, task.Tier, records, diagnostics);
        });

        return new PluginCatalog(records);
    }

    private static void EnumerateRoot(
        string root,
        ModuleTier tier,
        ConcurrentBag<PluginRecord> sink,
        IDiscoveryDiagnostics diagnostics)
    {
        if (!Directory.Exists(root))
        {
            // Tier may legitimately have no Plugins directory (an
            // empty repo or a project without plugins). Silent skip.
            return;
        }

        // Each plugin descriptor lives at /Plugins/<Name>/<Name>.xplugin
        // by convention; we enumerate the second-level subtree.
        IEnumerable<string> descriptors;
        try
        {
            descriptors = Directory.EnumerateFiles(
                root,
                "*" + PluginExtension,
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MatchType = MatchType.Simple,
                });
        }
        catch (UnauthorizedAccessException ex)
        {
            diagnostics.ReportDiscoveryFailure(root, ex.Message);
            return;
        }
        catch (IOException ex)
        {
            diagnostics.ReportDiscoveryFailure(root, ex.Message);
            return;
        }

        foreach (string path in descriptors)
        {
            try
            {
                PluginDescriptor descriptor = PluginDescriptorParser.ParseFile(path);
                IoHash contentHash = FileItem.GetItemByPath(path).ContentHash;
                sink.Add(new PluginRecord(
                    Descriptor: descriptor,
                    Tier: tier,
                    DescriptorPath: path,
                    ContentHash: contentHash));
            }
            catch (DescriptorParseException ex)
            {
                diagnostics.ReportPluginParseFailure(path, ex.Message);
            }
        }
    }
}
