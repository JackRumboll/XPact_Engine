// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;

namespace Simgenics.XPact.XBT.Core;

/// <summary>
/// Discovers and caches the XPact repository root by walking up from a
/// starting directory looking for a <c>.git</c> sibling. Used by
/// <see cref="Logger"/> to canonicalise diagnostic file paths to
/// repo-relative form so the streaming JSON channel records are stable
/// across developer machines and CI agents (per <c>/Documents/XBT.html</c>
/// Rev 4 Section 21.2; Toolchain Contract Rev 13 Section 2.1
/// reproducibility envelope).
/// </summary>
/// <remarks>
/// <para>
/// Discovery is best-effort. When XBT is run from outside any git
/// checkout (e.g. an installer-deployed binary running against a
/// non-repo source tree) the cache resolves to <c>null</c> and the
/// canonicaliser falls back to absolute paths -- <see cref="Logger"/>
/// emits a one-time warning so the operator knows the JSON channel
/// records are machine-local in that mode.
/// </para>
/// <para>
/// The cache is process-wide: the first <see cref="GetRepoRoot"/> call
/// pays the directory-walk cost; every subsequent call returns the
/// cached value. <see cref="ResetForTesting"/> clears the cache for
/// unit-test fixtures that need to exercise both the found and
/// not-found paths in the same process.
/// </para>
/// </remarks>
public static class RepoRoot
{
    private static readonly object s_gate = new();
    private static bool s_resolved;
    private static string? s_repoRoot;

    /// <summary>
    /// Return the absolute path to the discovered repo root (with no
    /// trailing separator), or <c>null</c> if no <c>.git</c> directory
    /// was found above <see cref="Environment.CurrentDirectory"/>.
    /// </summary>
    public static string? GetRepoRoot()
    {
        if (s_resolved)
        {
            return s_repoRoot;
        }
        lock (s_gate)
        {
            if (s_resolved)
            {
                return s_repoRoot;
            }
            s_repoRoot = DiscoverFrom(Environment.CurrentDirectory);
            s_resolved = true;
            return s_repoRoot;
        }
    }

    /// <summary>
    /// Test hook: clear the cache so the next <see cref="GetRepoRoot"/>
    /// call re-runs the walk. Not part of the public surface.
    /// </summary>
    internal static void ResetForTesting()
    {
        lock (s_gate)
        {
            s_resolved = false;
            s_repoRoot = null;
        }
    }

    /// <summary>
    /// Test hook: prime the cache with an explicit repo root value
    /// (or <c>null</c>) so tests can exercise both the found and
    /// not-found canonicalisation paths without manipulating the
    /// filesystem.
    /// </summary>
    internal static void SetForTesting(string? repoRoot)
    {
        lock (s_gate)
        {
            s_repoRoot = repoRoot;
            s_resolved = true;
        }
    }

    /// <summary>
    /// Walk up from <paramref name="startDirectory"/> until a
    /// <c>.git</c> child is found or the filesystem root is reached.
    /// Returns the path of the directory containing <c>.git</c>, or
    /// <c>null</c> if none was found.
    /// </summary>
    internal static string? DiscoverFrom(string startDirectory)
    {
        try
        {
            DirectoryInfo? dir = new(Path.GetFullPath(startDirectory));
            while (dir is not null)
            {
                string gitPath = Path.Combine(dir.FullName, ".git");
                // .git may be a directory (normal checkout) or a file (worktree
                // pointing back to the main .git). Either is a valid marker.
                if (Directory.Exists(gitPath) || File.Exists(gitPath))
                {
                    return dir.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                dir = dir.Parent;
            }
        }
        catch (Exception)
        {
            // Best-effort: any IO exception leaves us in the "no root" state.
            // We do NOT propagate -- diagnostic path canonicalisation must
            // never fail the diagnostic itself.
            return null;
        }
        return null;
    }

    /// <summary>
    /// Canonicalise a source-file path for diagnostic emission. Paths
    /// under the discovered repo root that fall inside <c>/Engine/</c>,
    /// <c>/Studio/</c>, or <c>/Projects/</c> are converted to
    /// forward-slash repo-relative form (e.g.
    /// <c>"Engine/Source/Runtime/XScoring/Private/XScoring.cpp"</c>);
    /// everything else is returned as an absolute path with forward
    /// slashes. Returns <paramref name="path"/> unchanged when null,
    /// empty, or unprocessable.
    /// </summary>
    /// <remarks>
    /// Path canonicalisation is best-effort -- any exception during
    /// the conversion falls back to the input absolute form rather than
    /// failing the diagnostic.
    /// </remarks>
    public static string? Canonicalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        try
        {
            // Idempotency: a forward-slash relative path that already
            // begins with a tier root is the canonical form. Return it
            // unchanged rather than re-resolving against the CWD.
            if (!Path.IsPathRooted(path)
                && !path.Contains('\\')
                && (path.StartsWith("Engine/",   StringComparison.Ordinal)
                ||  path.StartsWith("Studio/",   StringComparison.Ordinal)
                ||  path.StartsWith("Projects/", StringComparison.Ordinal)))
            {
                return path;
            }

            string absolute;
            try
            {
                absolute = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                // Path is not a real filesystem path (e.g. a logical
                // identifier). Return as-is.
                return path;
            }

            string? root = GetRepoRoot();
            string forwardAbsolute = absolute.Replace('\\', '/');

            if (root is null)
            {
                return forwardAbsolute;
            }

            string forwardRoot = root.Replace('\\', '/').TrimEnd('/');
            // Tolerate both case-insensitive (Windows / macOS-default) and
            // case-sensitive (Linux) filesystems; we record case as-is.
            StringComparison cmp = OperatingSystem.IsLinux()
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            string prefix = forwardRoot + "/";
            if (forwardAbsolute.StartsWith(prefix, cmp))
            {
                string rel = forwardAbsolute[prefix.Length..];
                // Only canonicalise the three first-class tier roots.
                // Other repo-internal paths (.git, .vs, build outputs)
                // round-trip as absolute to preserve diagnostic precision.
                if (rel.StartsWith("Engine/",  cmp)
                ||  rel.StartsWith("Studio/",  cmp)
                ||  rel.StartsWith("Projects/", cmp))
                {
                    return rel;
                }
            }

            return forwardAbsolute;
        }
        catch (Exception)
        {
            return path;
        }
    }
}
