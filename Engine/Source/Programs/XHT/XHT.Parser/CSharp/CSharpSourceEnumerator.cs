// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XHT.Manifest;

namespace Simgenics.XPact.XHT.Parser.CSharp;

/// <summary>
/// Determinism gate for C# source-file enumeration per
/// <c>/Documents/XHT.html</c> Rev 7 Section 11.4 (byte-identical-output
/// under parallelism, "Roslyn determinism -- partial class file
/// ordering"). C# source files within a module must be enumerated in
/// <see cref="StringComparer.Ordinal"/> order before invoking the Roslyn
/// walker so partial-class declarations are merged in a canonical order
/// regardless of filesystem walk order.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this matters.</b> Filesystem traversal order can differ between
/// Linux (ext4 inode-order walk) and Windows (NTFS B-tree-order walk).
/// Without an explicit ordinal sort, two partial-class fragments declared
/// in different files would merge in different orders on different OSes,
/// breaking byte-identical-output (Section 14).
/// </para>
/// <para>
/// <b>Sort key.</b> The whole path is compared with
/// <see cref="StringComparer.Ordinal"/> (case-sensitive, byte-order-by-byte-
/// order). Two files that differ only in case rank distinctly; the
/// underlying filesystem may or may not preserve the case but the
/// manifest must record the canonical form per XBT's discipline
/// (XBT.html Addendum Section 2.3).
/// </para>
/// <para>
/// <b>Stable across calls.</b> The sort produces an identical sequence
/// every call given an identical input set; no hash-randomised or
/// time-derived state participates.
/// </para>
/// </remarks>
public static class CSharpSourceEnumerator
{
    /// <summary>
    /// Return the module's C# source files (paths from
    /// <see cref="XbtSourceFile.RelativePath"/> filtered to entries with
    /// <see cref="XbtSourceFile.IsCSharp"/> = true) in
    /// <see cref="StringComparer.Ordinal"/> order. The returned list is
    /// what <see cref="CSharpMarkerWalker"/> should be invoked over in
    /// sequence per Section 11.4's partial-class ordering rule.
    /// </summary>
    /// <param name="module">The XBT-manifest module entry. Must not be null.</param>
    /// <returns>The ordinal-sorted list of module-relative C# paths.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="module"/> is null.</exception>
    public static IReadOnlyList<string> EnumerateOrdered(XbtModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        List<string> paths = new();
        foreach (XbtSourceFile sf in module.SourceFiles)
        {
            if (sf.IsCSharp)
            {
                paths.Add(sf.RelativePath);
            }
        }
        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    /// <summary>
    /// Return <paramref name="sourcePaths"/> sorted by
    /// <see cref="StringComparer.Ordinal"/>. Useful for tests and for
    /// non-XBT-manifest callers (e.g. ad-hoc CLI invocation supplying a
    /// raw path list).
    /// </summary>
    /// <param name="sourcePaths">The paths to sort. Must not be null; entries must not be null.</param>
    /// <returns>The ordinal-sorted path list.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="sourcePaths"/> is null.</exception>
    public static IReadOnlyList<string> EnumerateOrdered(IEnumerable<string> sourcePaths)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);

        List<string> paths = new();
        foreach (string p in sourcePaths)
        {
            ArgumentNullException.ThrowIfNull(p);
            paths.Add(p);
        }
        paths.Sort(StringComparer.Ordinal);
        return paths;
    }
}
