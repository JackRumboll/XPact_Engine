// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.Discovery;

/// <summary>
/// Walks every tier's <c>Source/</c> tree and every plugin's
/// <c>Source/</c> tree for <c>*.Build.toml</c> files. Per
/// <c>/Documents/XBT.html</c> Rev 4 Section 3.1.
/// </summary>
/// <remarks>
/// <para>
/// The walk roots:
/// </para>
/// <list type="bullet">
///   <item><c>/Engine/Source/</c></item>
///   <item><c>/Studio/Source/</c> (where applicable)</item>
///   <item><c>/Projects/&lt;P&gt;/Source/</c></item>
///   <item><c>/Engine/Plugins/&lt;Plugin&gt;/Source/</c></item>
///   <item><c>/Studio/Plugins/&lt;Plugin&gt;/Source/</c></item>
///   <item><c>/Projects/&lt;P&gt;/Plugins/&lt;Plugin&gt;/Source/</c></item>
/// </list>
/// <para>
/// The Phase 1 Roslyn escape hatch (<c>.Build.cs</c>) is not yet
/// implemented; this enumerator skips <c>*.Build.cs</c> files with a
/// stub diagnostic per <c>/Documents/XBT.html</c> Section 3.6.
/// </para>
/// </remarks>
public static class ModuleEnumerator
{
    /// <summary>
    /// Standard Build descriptor filename suffix.
    /// </summary>
    public const string BuildTomlSuffix = ".Build.toml";

    /// <summary>
    /// Enumerate every <c>.Build.toml</c> reachable from the supplied
    /// source roots and assemble a <see cref="ModuleCatalog"/>.
    /// </summary>
    /// <param name="sourceRoots">
    /// Absolute paths to enumerate. Typically: every tier's
    /// <c>Source/</c> directory plus each discovered plugin's
    /// <c>Source/</c> directory. The walk is parallel across roots.
    /// </param>
    /// <param name="diagnostics">
    /// Sink for parse-failure diagnostics. Failed descriptors are
    /// omitted from the catalog; the caller decides whether to fail
    /// the build.
    /// </param>
    public static ModuleCatalog Enumerate(
        IEnumerable<string> sourceRoots,
        IDiscoveryDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sourceRoots);
        ArgumentNullException.ThrowIfNull(diagnostics);

        ConcurrentBag<ModuleRecord> records = new();

        Parallel.ForEach(sourceRoots, root =>
        {
            EnumerateRoot(root, records, diagnostics, owningPluginName: null);
        });

        return new ModuleCatalog(records);
    }

    /// <summary>
    /// Enumerate modules under a single plugin's <c>Source/</c>
    /// directory, tagging each record with the owning plugin name.
    /// </summary>
    public static IReadOnlyList<ModuleRecord> EnumeratePlugin(
        string pluginSourceRoot,
        string owningPluginName,
        IDiscoveryDiagnostics diagnostics)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginSourceRoot);
        ArgumentException.ThrowIfNullOrEmpty(owningPluginName);
        ArgumentNullException.ThrowIfNull(diagnostics);

        ConcurrentBag<ModuleRecord> sink = new();
        EnumerateRoot(pluginSourceRoot, sink, diagnostics, owningPluginName);
        return sink.OrderBy(r => r.Rules.Name, StringComparer.Ordinal).ToList();
    }

    private static void EnumerateRoot(
        string root,
        ConcurrentBag<ModuleRecord> sink,
        IDiscoveryDiagnostics diagnostics,
        string? owningPluginName)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        IEnumerable<string> descriptors;
        try
        {
            descriptors = Directory.EnumerateFiles(
                root,
                "*" + BuildTomlSuffix,
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

        // Process .Build.cs files only to emit a "Roslyn fallback not
        // yet implemented" diagnostic and skip; Phase 1.2.1 lands the
        // implementation.
        try
        {
            foreach (string csPath in Directory.EnumerateFiles(
                root,
                "*.Build.cs",
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MatchType = MatchType.Simple,
                }))
            {
                // The XBT-own placeholder .Build.cs files (XBT.Core.Build.cs
                // etc.) live inside /Engine/Source/Programs/XBT/ and are
                // self-referential informational stubs guarded by
                // #if XBT_HAS_MODULERULES (never defined). They are not
                // descriptors XBT consumes; recognising the path keeps
                // discovery diagnostics quiet.
                if (csPath.Replace('\\', '/').Contains("/Programs/XBT/", StringComparison.Ordinal))
                {
                    continue;
                }
                diagnostics.ReportRoslynFallbackPending(csPath);
            }
        }
        catch
        {
            // Best-effort; never let a .Build.cs enumeration failure
            // hide the .Build.toml results.
        }

        foreach (string path in descriptors)
        {
            try
            {
                ModuleRules rules = BuildTomlParser.ParseFile(path);
                IoHash contentHash = FileItem.GetItemByPath(path).ContentHash;
                sink.Add(new ModuleRecord(
                    Rules: rules,
                    DescriptorPath: path,
                    ContentHash: contentHash,
                    OwningPluginName: owningPluginName));
            }
            catch (DescriptorParseException ex)
            {
                diagnostics.ReportModuleParseFailure(path, ex.Message);
            }
        }
    }
}
