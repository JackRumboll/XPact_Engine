// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using Simgenics.XPact.XHT.Manifest;

namespace Simgenics.XPact.XHT.Emitter;

/// <summary>
/// Helper for splitting a source-relative path into the logical-path
/// segments expected by the FileId scheme per Contract Section 1.4 +
/// XHT.html Section 10.1.
/// </summary>
/// <remarks>
/// <para>
/// <b>Logical path semantics.</b> Contract Section 1.4 makes the logical
/// path independent of the on-disk path: <c>"Moving the file on disk does
/// not break consumers as long as the logical path is preserved."</c> For
/// Phase 1 we derive the logical path from the manifest by stripping the
/// module's <see cref="XbtModule.BaseDirectory"/> prefix from the source-
/// relative path and using the remaining slash-separated segments. The
/// final segment is the file stem (without <c>.h</c> / <c>.hpp</c>
/// extension).
/// </para>
/// <para>
/// <b>Worked examples.</b>
/// </para>
/// <list type="bullet">
///   <item><description>
///     Source <c>"Engine/Source/Runtime/XGameFramework/Public/Valves/XValve.h"</c> +
///     module <c>BaseDirectory = "Engine/Source/Runtime/XGameFramework"</c>
///     -&gt; <c>["Public", "Valves", "XValve"]</c>.
///   </description></item>
///   <item><description>
///     Source <c>"Public/Foo.h"</c> + module <c>BaseDirectory = ""</c> (or
///     when the source is already module-relative) -&gt; <c>["Public", "Foo"]</c>.
///   </description></item>
///   <item><description>
///     Source <c>"XValve.h"</c> at module root (after strip) -&gt;
///     <c>["XValve"]</c>.
///   </description></item>
/// </list>
/// <para>
/// <b>Path separator normalisation.</b> Backslashes are normalised to
/// forward slashes before split per the engine-wide path-form convention
/// (XHT.html Section 14: "The PathSeparator for emitted include paths is
/// forward slash"). The result is independent of the host OS.
/// </para>
/// </remarks>
public static class LogicalPathSplitter
{
    /// <summary>
    /// Split <paramref name="sourceRelativePath"/> into logical-path
    /// segments relative to <paramref name="module"/>'s
    /// <see cref="XbtModule.BaseDirectory"/>.
    /// </summary>
    /// <param name="sourceRelativePath">
    /// Path of the source header, either module-relative (no leading
    /// <see cref="XbtModule.BaseDirectory"/>) or already-rooted at the
    /// repo root. Must not be null / empty / whitespace.
    /// </param>
    /// <param name="module">The owning module from the manifest. Must not be null.</param>
    /// <returns>The list of logical-path segments. Never empty (the final stem is always present).</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="module"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="sourceRelativePath"/> is null / empty / whitespace, or splits to zero segments.</exception>
    public static IReadOnlyList<string> Split(string sourceRelativePath, XbtModule module)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRelativePath);
        ArgumentNullException.ThrowIfNull(module);

        // Step 1. Normalise to forward slashes; XBT-side paths sometimes
        // arrive with backslashes on Windows, the emit side needs the
        // logical-path layer in canonical form.
        string normalized = sourceRelativePath.Replace('\\', '/');

        // Step 2. Strip the module's BaseDirectory prefix when present.
        // BaseDirectory itself may be empty (synthetic modules in tests)
        // or carry a trailing slash; normalise both edges.
        string baseDir = (module.BaseDirectory ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        if (baseDir.Length > 0)
        {
            string baseWithSlash = baseDir + "/";
            if (normalized.StartsWith(baseWithSlash, StringComparison.Ordinal))
            {
                normalized = normalized[baseWithSlash.Length..];
            }
            else if (normalized.Equals(baseDir, StringComparison.Ordinal))
            {
                // Source exactly equals BaseDirectory (unusual but possible
                // for a single-file module). Treat as empty after strip.
                normalized = string.Empty;
            }
        }

        // Defensive: also strip a leading slash if one survives.
        normalized = normalized.TrimStart('/');

        if (string.IsNullOrEmpty(normalized))
        {
            throw new ArgumentException(
                $"Source path '{sourceRelativePath}' is empty after stripping module BaseDirectory '{module.BaseDirectory}'.",
                nameof(sourceRelativePath));
        }

        // Step 3. Strip the .h / .hpp / .hh extension from the final
        // segment so the logical-path stem matches the file stem the
        // FileId scheme expects.
        string withoutExt = StripHeaderExtension(normalized);

        // Step 4. Split on '/'. Empty segments (e.g. from a double slash
        // in the input) are filtered to keep the result tight.
        List<string> segments = new();
        foreach (string seg in withoutExt.Split('/'))
        {
            if (!string.IsNullOrEmpty(seg))
            {
                segments.Add(seg);
            }
        }

        if (segments.Count == 0)
        {
            throw new ArgumentException(
                $"Source path '{sourceRelativePath}' split to zero segments after extension strip.",
                nameof(sourceRelativePath));
        }

        return segments;
    }

    private static string StripHeaderExtension(string path)
    {
        // Match against the well-known source-file extension set in a
        // single pass. Ordinal compare for OS-independence (the in-tree
        // manifest is canonical-cased). The body-macro FileId scheme is
        // C++ -only but .cs files also flow through the splitter when
        // emitter helpers compute a logical-path stem for diagnostic /
        // manifest entries; stripping the .cs extension keeps the
        // resulting stem aligned with the C# type name.
        string ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
        {
            return path;
        }

        bool isReflectionSource =
            ext.Equals(".h", StringComparison.Ordinal)
            || ext.Equals(".hpp", StringComparison.Ordinal)
            || ext.Equals(".hh", StringComparison.Ordinal)
            || ext.Equals(".inl", StringComparison.Ordinal)
            || ext.Equals(".cs", StringComparison.Ordinal);

        if (!isReflectionSource)
        {
            return path;
        }

        // Strip extension keeping the rest of the path intact.
        return path[..^ext.Length];
    }
}
