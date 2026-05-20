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
/// <c>Source/</c> tree for <c>*.Build.toml</c> and <c>*.Build.cs</c>
/// descriptors. Per <c>/Documents/XBT.html</c> Rev 4 Section 3.1 +
/// Section 3.6.
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
/// <b>Phase 1 Roslyn escape hatch (Section 3.6).</b> A module that
/// ships a <c>.Build.cs</c> opts into a per-module Roslyn fallback.
/// The enumerator routes <c>.Build.cs</c> files to
/// <see cref="BuildCsCompiler.Compile(string, TargetRules, string?)"/>
/// when the caller supplied a <see cref="TargetRules"/>. When no
/// target is supplied (a test-only convenience) the
/// <c>.Build.cs</c> still surfaces a
/// <see cref="IDiscoveryDiagnostics.ReportRoslynFallbackPending"/>
/// marker so callers can detect the case explicitly.
/// </para>
/// <para>
/// <b>Both descriptor kinds present.</b> Per Contract Section 9.6
/// "a module that legitimately needs full C# can ship a .Build.cs
/// alongside the TOML"; the <c>.Build.cs</c> takes precedence and the
/// TOML is informational. The enumerator emits a
/// <see cref="Logger.Info"/> diagnostic naming which descriptor was
/// used in that case.
/// </para>
/// </remarks>
public static class ModuleEnumerator
{
    /// <summary>
    /// Standard Build descriptor filename suffix.
    /// </summary>
    public const string BuildTomlSuffix = ".Build.toml";

    /// <summary>
    /// Standard Roslyn-fallback descriptor filename suffix.
    /// </summary>
    public const string BuildCsSuffix = ".Build.cs";

    /// <summary>
    /// Enumerate every module descriptor reachable from the supplied
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
    /// <param name="target">
    /// Optional active <see cref="TargetRules"/>. Required for the
    /// Phase 1 Roslyn escape hatch (<c>.Build.cs</c>) per Contract
    /// Section 9.6: the user-authored constructor receives the target
    /// and conditions on its fields (<c>target.Platform</c>,
    /// <c>target.FipsMode</c>, etc.). When null, <c>.Build.cs</c>
    /// files are skipped with the legacy
    /// <see cref="IDiscoveryDiagnostics.ReportRoslynFallbackPending"/>
    /// diagnostic so callers can opt into a TOML-only build.
    /// </param>
    /// <param name="buildCsCacheDirectory">
    /// Optional override of the compiled-DLL cache directory used by
    /// <see cref="BuildCsCompiler"/>. Tests use this for isolation;
    /// production code leaves it null so <see cref="BuildCsCompiler"/>
    /// derives the canonical
    /// <c>&lt;workspace&gt;/Intermediate/Build/XBT/BuildCsCache/</c>
    /// path.
    /// </param>
    public static ModuleCatalog Enumerate(
        IEnumerable<string> sourceRoots,
        IDiscoveryDiagnostics diagnostics,
        TargetRules? target = null,
        string? buildCsCacheDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(sourceRoots);
        ArgumentNullException.ThrowIfNull(diagnostics);

        ConcurrentBag<ModuleRecord> records = new();

        Parallel.ForEach(sourceRoots, root =>
        {
            EnumerateRoot(
                root,
                records,
                diagnostics,
                owningPluginName: null,
                target,
                buildCsCacheDirectory);
        });

        return new ModuleCatalog(records);
    }

    /// <summary>
    /// Enumerate modules under a single plugin's <c>Source/</c>
    /// directory, tagging each record with the owning plugin name.
    /// Same Phase 1 Roslyn escape-hatch semantics as
    /// <see cref="Enumerate(IEnumerable{string}, IDiscoveryDiagnostics, TargetRules?, string?)"/>.
    /// </summary>
    public static IReadOnlyList<ModuleRecord> EnumeratePlugin(
        string pluginSourceRoot,
        string owningPluginName,
        IDiscoveryDiagnostics diagnostics,
        TargetRules? target = null,
        string? buildCsCacheDirectory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginSourceRoot);
        ArgumentException.ThrowIfNullOrEmpty(owningPluginName);
        ArgumentNullException.ThrowIfNull(diagnostics);

        ConcurrentBag<ModuleRecord> sink = new();
        EnumerateRoot(
            pluginSourceRoot,
            sink,
            diagnostics,
            owningPluginName,
            target,
            buildCsCacheDirectory);
        return sink.OrderBy(r => r.Rules.Name, StringComparer.Ordinal).ToList();
    }

    private static void EnumerateRoot(
        string root,
        ConcurrentBag<ModuleRecord> sink,
        IDiscoveryDiagnostics diagnostics,
        string? owningPluginName,
        TargetRules? target,
        string? buildCsCacheDirectory)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        // Enumerate both descriptor kinds. We group by directory so the
        // .Build.cs precedence rule (Contract Section 9.6) can be
        // applied per-module.
        string[] tomlPaths;
        string[] csPaths;
        try
        {
            tomlPaths = Directory.GetFiles(
                root,
                "*" + BuildTomlSuffix,
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MatchType = MatchType.Simple,
                });
            csPaths = Directory.GetFiles(
                root,
                "*" + BuildCsSuffix,
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

        // Filter out XBT-own placeholder .Build.cs files. These live
        // inside /Engine/Source/Programs/XBT/ and are self-referential
        // informational stubs guarded by #if XBT_HAS_MODULERULES (never
        // defined). They are not descriptors XBT consumes.
        List<string> filteredCs = new(csPaths.Length);
        foreach (string p in csPaths)
        {
            if (p.Replace('\\', '/').Contains("/Programs/XBT/", StringComparison.Ordinal))
            {
                continue;
            }
            filteredCs.Add(p);
        }

        // Group TOMLs and CSs by their containing directory so we can
        // apply the precedence rule per module.
        Dictionary<string, string> tomlByDir = new(StringComparer.OrdinalIgnoreCase);
        foreach (string t in tomlPaths)
        {
            string dir = Path.GetDirectoryName(t) ?? string.Empty;
            tomlByDir[dir] = t;
        }
        Dictionary<string, string> csByDir = new(StringComparer.OrdinalIgnoreCase);
        foreach (string c in filteredCs)
        {
            string dir = Path.GetDirectoryName(c) ?? string.Empty;
            csByDir[dir] = c;
        }

        HashSet<string> processedDirs = new(StringComparer.OrdinalIgnoreCase);

        // Pass 1: every directory with a .Build.cs. If a TOML also
        // lives in the same directory, the .Build.cs wins per Contract
        // Section 9.6 and the TOML is logged-and-ignored.
        foreach ((string dir, string csPath) in csByDir)
        {
            processedDirs.Add(dir);

            if (target is null)
            {
                // Legacy no-target path: emit the existing
                // Roslyn-fallback-pending diagnostic, leaving the
                // module out of the catalog. Callers that want the
                // .Build.cs compiled supply a target.
                diagnostics.ReportRoslynFallbackPending(csPath);
                continue;
            }

            if (tomlByDir.TryGetValue(dir, out string? siblingToml))
            {
                Logger.Info(
                    $".Build.cs takes precedence over sibling .Build.toml in {dir}: " +
                    $"compiling {Path.GetFileName(csPath)}; ignoring " +
                    $"{Path.GetFileName(siblingToml)} per Contract Rev 13 Section 9.6.",
                    new DiagnosticContext
                    {
                        Action = "discover",
                        File = csPath,
                    });
            }

            try
            {
                ModuleRules rules = BuildCsCompiler.Compile(csPath, target, buildCsCacheDirectory);
                IoHash contentHash = FileItem.GetItemByPath(csPath).ContentHash;
                sink.Add(new ModuleRecord(
                    Rules: rules,
                    DescriptorPath: csPath,
                    ContentHash: contentHash,
                    OwningPluginName: owningPluginName));
            }
            catch (DescriptorParseException ex)
            {
                diagnostics.ReportModuleParseFailure(csPath, ex.Message);
            }
        }

        // Pass 2: every TOML in a directory NOT already covered by a
        // .Build.cs. (The TOML-only modules; this is the canonical
        // path for the vast majority of modules.)
        foreach ((string dir, string tomlPath) in tomlByDir)
        {
            if (processedDirs.Contains(dir))
            {
                continue;
            }
            try
            {
                ModuleRules rules = BuildTomlParser.ParseFile(tomlPath, target);
                IoHash contentHash = FileItem.GetItemByPath(tomlPath).ContentHash;
                sink.Add(new ModuleRecord(
                    Rules: rules,
                    DescriptorPath: tomlPath,
                    ContentHash: contentHash,
                    OwningPluginName: owningPluginName));
            }
            catch (DescriptorParseException ex)
            {
                diagnostics.ReportModuleParseFailure(tomlPath, ex.Message);
            }
        }
    }
}
