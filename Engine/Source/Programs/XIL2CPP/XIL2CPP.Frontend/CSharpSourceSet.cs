// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using Simgenics.XPact.XIL2CPP.Manifest;

namespace Simgenics.XPact.XIL2CPP.Frontend;

/// <summary>
/// Determinism gate for C# source-file enumeration per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 9.9 (reproducibility) +
/// Section 3.2 (Pass 1). The C# source files a module compiles must be
/// enumerated in <see cref="StringComparer.Ordinal"/> order before parsing
/// so partial-class declarations merge in a canonical order regardless of
/// filesystem walk order. Mirrors the XHT parser's
/// <c>CSharpSourceEnumerator</c>
/// (<c>/Engine/Source/Programs/XHT/XHT.Parser/CSharp/CSharpSourceEnumerator.cs</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this matters.</b> Filesystem traversal order differs between
/// Linux (ext4 inode-order walk) and Windows (NTFS B-tree-order walk).
/// Without an explicit ordinal sort, two partial-class fragments declared
/// in different files would merge in different orders on different OSes,
/// breaking byte-identical output. The sort key is the absolute path
/// (case-sensitive, byte-by-byte).
/// </para>
/// <para>
/// <b>Manifest-relative resolution.</b> The XBT manifest records each C#
/// source as a path relative to the module's <c>BaseDirectory</c>, which is
/// itself relative to the manifest's <c>RootLocalPath</c>. This type
/// composes the absolute path
/// (<c>RootLocalPath / BaseDirectory / RelativePath</c>) so the parse and
/// the resulting diagnostics carry the on-disk location. The relative paths
/// are sorted before composition so the ordering is independent of the
/// (forward-vs-back-slash) root form.
/// </para>
/// </remarks>
public static class CSharpSourceSet
{
    /// <summary>
    /// One resolved C# source file: the absolute on-disk path plus the
    /// module-relative path the manifest recorded (retained for
    /// diagnostics that prefer the stable relative form).
    /// </summary>
    /// <param name="AbsolutePath">Absolute on-disk path (root + base + relative).</param>
    /// <param name="RelativePath">Module-relative path as recorded in the manifest.</param>
    public readonly record struct ResolvedSource(string AbsolutePath, string RelativePath);

    /// <summary>
    /// Resolve a module's C# sources to absolute paths, ordinal-sorted by
    /// the module-relative path so the order is canonical and stable across
    /// machines. The relative-path set comes from
    /// <see cref="Xil2CppManifestReader.GetCSharpSourceFiles(XbtModule)"/>
    /// (the union of <c>CSharpSources</c> and <c>IsCSharp</c>
    /// <c>SourceFiles</c>).
    /// </summary>
    /// <param name="manifest">The owning manifest (supplies RootLocalPath). Must not be null.</param>
    /// <param name="module">The module to enumerate. Must not be null.</param>
    /// <returns>The resolved, ordinal-sorted C# source list.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="manifest"/> or <paramref name="module"/> is null.</exception>
    public static IReadOnlyList<ResolvedSource> Resolve(XbtManifest manifest, XbtModule module)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(module);

        IReadOnlyList<string> relative = Xil2CppManifestReader.GetCSharpSourceFiles(module);

        // Ordinal-sort the relative paths first so the canonical order is
        // independent of the absolute-root spelling.
        List<string> sortedRelative = new(relative);
        sortedRelative.Sort(StringComparer.Ordinal);

        List<ResolvedSource> result = new(sortedRelative.Count);
        foreach (string rel in sortedRelative)
        {
            string absolute = ComposeAbsolutePath(manifest.RootLocalPath, module.BaseDirectory, rel);
            result.Add(new ResolvedSource(absolute, rel));
        }

        return result;
    }

    /// <summary>
    /// Compose the absolute on-disk path from the manifest root, the
    /// module base directory, and the source's module-relative path.
    /// </summary>
    /// <param name="rootLocalPath">The manifest's host-local repo root.</param>
    /// <param name="baseDirectory">The module's source root, relative to the root.</param>
    /// <param name="relativePath">The source's path, relative to the base directory.</param>
    /// <returns>The composed absolute path (the platform's directory separators).</returns>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    public static string ComposeAbsolutePath(string rootLocalPath, string baseDirectory, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(rootLocalPath);
        ArgumentNullException.ThrowIfNull(baseDirectory);
        ArgumentNullException.ThrowIfNull(relativePath);

        // Path.Combine handles the case where baseDirectory / relativePath
        // is already rooted (it discards the earlier segments), which is the
        // documented behaviour and matches how a hand-edited manifest with
        // an absolute source path would be honoured.
        string combined = Path.Combine(rootLocalPath, baseDirectory, relativePath);
        return Path.GetFullPath(combined);
    }
}
