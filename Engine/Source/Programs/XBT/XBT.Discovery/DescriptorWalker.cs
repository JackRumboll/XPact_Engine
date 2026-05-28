// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;

namespace Simgenics.XPact.XBT.Discovery;

/// <summary>
/// Shared descriptor-tree walker for <see cref="ModuleEnumerator"/> and
/// <see cref="PluginEnumerator"/>. Prunes well-known non-source subtrees
/// (build outputs, IDE state, VCS internals) at the directory level so
/// the OS walk never descends into them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why directory-level pruning, not post-filtering.</b> A naive
/// implementation that calls <see cref="Directory.GetFiles(string, string, EnumerationOptions)"/>
/// and discards matches by path substring still pays the OS-walk cost of
/// every excluded subtree. For .NET projects under <c>/Engine/Source/Programs/</c>
/// that cost is unbounded -- a single <c>bin/Debug/net8.0/</c> directory
/// after NuGet restore can contain thousands of dependency DLLs across
/// hundreds of nested package directories. Pruning at
/// <see cref="FileSystemEnumerable{TResult}.ShouldRecursePredicate"/>
/// makes the directory tree the natural unit of exclusion: the predicate
/// is consulted once per directory entry, and a <c>false</c> return
/// causes the OS to skip the subtree entirely.
/// </para>
/// <para>
/// <b>Why these names.</b> Each excluded name corresponds to a directory
/// that, by construction, never contains canonical engine descriptors
/// that XBT consumes:
/// </para>
/// <list type="bullet">
///   <item>
///     <c>bin/</c>, <c>obj/</c> -- MSBuild output. XBT.Tests's csproj
///     copies its <c>Fixtures/</c> tree into <c>bin/&lt;cfg&gt;/&lt;tfm&gt;/Fixtures/</c>;
///     a scan rooted at <c>/Engine/Source/</c> that descended into the
///     test project's <c>bin/</c> would surface every fixture descriptor
///     a second time and the catalog would reject the duplicate.
///   </item>
///   <item>
///     <c>.idea/</c>, <c>.vs/</c>, <c>.vscode/</c> -- IDE workspace
///     state. JetBrains Rider, Visual Studio, and VS Code respectively;
///     never contain XBT descriptors but may contain autosave / scratch
///     copies that would mislead a recursive scan.
///   </item>
///   <item>
///     <c>.git/</c> -- Git's internal object store. Contains no XBT
///     descriptors and can be very large; walking it is pure waste.
///   </item>
/// </list>
/// <para>
/// Match is case-insensitive. On Windows the filesystem case-folds
/// before the predicate ever runs, so case sensitivity is observationally
/// equivalent to case-insensitivity. On Linux/macOS a directory named
/// <c>Bin/</c> in upper-case is extraordinarily unlikely to be a real
/// source-tree directory; treating it the same as <c>bin/</c> errs on
/// the side of safety and matches developer expectation.
/// </para>
/// <para>
/// <b>Note.</b> XBT-engine build intermediates land under
/// <c>/Engine/Intermediate/</c> / <c>/Engine/Binaries/</c>, which are
/// siblings of <c>/Engine/Source/</c> and so are never visited by a
/// scan rooted at <c>Source/</c>. They do not need to be on this list.
/// </para>
/// </remarks>
internal static class DescriptorWalker
{
    /// <summary>
    /// Directory names whose subtrees are pruned during enumeration.
    /// Compared with <see cref="StringComparison.OrdinalIgnoreCase"/>.
    /// </summary>
    private static readonly string[] s_ExcludedDirectoryNames =
    {
        "bin",
        "obj",
        ".idea",
        ".vs",
        ".vscode",
        ".git",
    };

    /// <summary>
    /// Enumerate every file under <paramref name="root"/> whose name
    /// ends with <paramref name="filenameSuffix"/>, recursing into
    /// subdirectories EXCEPT those whose name appears in
    /// <see cref="s_ExcludedDirectoryNames"/>.
    /// </summary>
    /// <param name="root">
    /// Absolute directory path to walk. The caller is responsible for
    /// verifying the path exists before calling; this method returns an
    /// empty sequence (without throwing) when the root does not exist.
    /// </param>
    /// <param name="filenameSuffix">
    /// Full filename suffix to match, e.g. <c>".Build.toml"</c> or
    /// <c>".xplugin"</c>. Matched ordinally (case-sensitive) against
    /// <see cref="FileSystemEntry.FileName"/>.
    /// </param>
    /// <returns>
    /// A materialized array of absolute paths. The walk is materialized
    /// so caller-side <c>try</c>/<c>catch</c> blocks observe
    /// <see cref="UnauthorizedAccessException"/> /
    /// <see cref="IOException"/> at a single call site, matching the
    /// pre-existing reporting contract on
    /// <see cref="IDiscoveryDiagnostics.ReportDiscoveryFailure"/>.
    /// </returns>
    public static string[] EnumerateFiles(string root, string filenameSuffix)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(filenameSuffix);

        if (!Directory.Exists(root))
        {
            return Array.Empty<string>();
        }

        FileSystemEnumerable<string> enumerable = new(
            root,
            static (ref FileSystemEntry entry) => entry.ToFullPath(),
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchType = MatchType.Simple,
            })
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                !entry.IsDirectory
                && entry.FileName.EndsWith(filenameSuffix, StringComparison.Ordinal),
            ShouldRecursePredicate = static (ref FileSystemEntry entry) =>
                !IsExcludedDirectory(entry.FileName),
        };

        List<string> results = new();
        foreach (string path in enumerable)
        {
            results.Add(path);
        }
        return results.ToArray();
    }

    private static bool IsExcludedDirectory(ReadOnlySpan<char> dirName)
    {
        foreach (string excluded in s_ExcludedDirectoryNames)
        {
            if (dirName.Equals(excluded.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
