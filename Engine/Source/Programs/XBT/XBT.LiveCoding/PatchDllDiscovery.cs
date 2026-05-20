// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.LiveCoding;

/// <summary>
/// Per <c>/Documents/XBT.html</c> Rev 4 Section 16.7: a patch DLL lives at
/// <c>/Intermediate/Build/&lt;Target&gt;/&lt;Configuration&gt;/LiveCoding/&lt;Module&gt;.patch.&lt;gen&gt;.dll</c>.
/// The <c>&lt;gen&gt;</c> counter is per-(Target, Module). Phase 1 implements
/// the directory discovery + scan-at-startup; Phase 2 XLiveCoding writes
/// the DLLs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Counter persistence model (Section 16.7).</b> The counter is
/// <em>not</em> kept in a separate persistence file. At XBT startup,
/// when XLiveCoding requests a patch-DLL write for
/// <c>(Target, Module)</c>, XBT scans the LiveCoding directory,
/// parses the integer suffix from every match, takes
/// <c>max(observed)</c>, and starts at <c>max + 1</c> -- or at <c>0</c>
/// if no prior patch-DLLs exist. Repeated XBT invocations in the same
/// physical directory monotonically advance the counter; a fresh CI
/// agent (with no prior <c>LiveCoding/</c> directory) starts at 0;
/// deleting old patch-DLLs by hand resets the high-water mark naturally.
/// </para>
/// <para>
/// <b>Scope.</b> The counter is per-(Target, Module) -- two concurrent
/// patches to <em>different</em> modules in the same target use
/// independent counters; two concurrent patches to the <em>same</em>
/// module in different targets likewise use independent counters; only
/// sequential patches to the same module in the same target share a
/// counter sequence.
/// </para>
/// </remarks>
public static class PatchDllDiscovery
{
    /// <summary>
    /// The fixed subdirectory holding patch DLLs for a target+config
    /// pair. The full path is
    /// <c>&lt;engineRoot&gt;/Intermediate/Build/&lt;Target&gt;/&lt;Configuration&gt;/LiveCoding/</c>.
    /// </summary>
    public const string LiveCodingDirectoryName = "LiveCoding";

    /// <summary>
    /// Get the absolute path to the per-(target, configuration) patch
    /// directory. Per Section 16.7. The directory is not created by
    /// Phase 1 XBT; Phase 2 XLiveCoding creates it on first patch write.
    /// </summary>
    /// <param name="engineRoot">Absolute path to the engine root.</param>
    /// <param name="targetName">Target name (matches the
    /// <c>.Target.toml</c> stem).</param>
    /// <param name="config">Active build configuration.</param>
    public static string GetPatchDirectory(string engineRoot, string targetName, BuildConfiguration config)
    {
        ArgumentException.ThrowIfNullOrEmpty(engineRoot);
        ArgumentException.ThrowIfNullOrEmpty(targetName);

        return Path.Combine(
            engineRoot,
            "Intermediate",
            "Build",
            targetName,
            config.ToString(),
            LiveCodingDirectoryName);
    }

    /// <summary>
    /// Scan <paramref name="patchDirectory"/> for
    /// <c>&lt;moduleName&gt;.patch.&lt;gen&gt;.dll</c> files and return
    /// the next free generation index per Section 16.7
    /// (<c>max(observed) + 1</c>, or 0 if none observed).
    /// </summary>
    /// <param name="patchDirectory">Absolute path to the LiveCoding
    /// directory for the current (Target, Configuration) pair. May be
    /// non-existent -- a missing directory yields 0, matching the
    /// "fresh CI agent" case.</param>
    /// <param name="moduleName">Module to scan for. Compared against
    /// the filename prefix using ordinal equality.</param>
    /// <returns>The next generation counter to use. 0 on an empty or
    /// missing directory; <c>max(observed) + 1</c> otherwise.</returns>
    public static int GetNextGeneration(string patchDirectory, string moduleName)
    {
        ArgumentException.ThrowIfNullOrEmpty(patchDirectory);
        ArgumentException.ThrowIfNullOrEmpty(moduleName);

        if (!Directory.Exists(patchDirectory))
        {
            return 0;
        }

        int max = -1;
        foreach (string fullPath in EnumeratePatchDlls(patchDirectory, moduleName))
        {
            int? gen = TryParseGeneration(fullPath, moduleName);
            if (gen is int g && g > max)
            {
                max = g;
            }
        }
        return max + 1;
    }

    /// <summary>
    /// Enumerate every patch-DLL filename in
    /// <paramref name="patchDirectory"/> matching the module's
    /// <c>&lt;moduleName&gt;.patch.&lt;gen&gt;.dll</c> shape. Non-matching
    /// files (other modules, other extensions, malformed counters) are
    /// silently ignored. Returns absolute paths sorted by ordinal
    /// string comparison so callers see a deterministic order.
    /// </summary>
    /// <param name="patchDirectory">Absolute path to the LiveCoding
    /// directory. A missing directory yields an empty sequence.</param>
    /// <param name="moduleName">Module to enumerate.</param>
    public static IEnumerable<string> EnumeratePatchDlls(string patchDirectory, string moduleName)
    {
        ArgumentException.ThrowIfNullOrEmpty(patchDirectory);
        ArgumentException.ThrowIfNullOrEmpty(moduleName);

        if (!Directory.Exists(patchDirectory))
        {
            return Array.Empty<string>();
        }

        // Enumerate files using the shape pattern then filter for
        // strict match. The shell wildcard pattern <module>.patch.*.dll
        // is a loose filter -- a malformed file like
        // <module>.patch.abc.dll matches the wildcard but is then
        // dropped by TryParseGeneration.
        string pattern = moduleName + ".patch.*.dll";
        string[] matches = Directory.GetFiles(patchDirectory, pattern, SearchOption.TopDirectoryOnly);

        // Stable ordinal sort so two enumerations of the same directory
        // produce identical output -- determinism per the spec.
        List<string> filtered = new(matches.Length);
        foreach (string m in matches)
        {
            if (TryParseGeneration(m, moduleName) is not null)
            {
                filtered.Add(m);
            }
        }
        filtered.Sort(StringComparer.Ordinal);
        return filtered;
    }

    /// <summary>
    /// Parse the <c>&lt;gen&gt;</c> integer from a filename of the
    /// shape <c>&lt;moduleName&gt;.patch.&lt;gen&gt;.dll</c>. Returns
    /// null when the filename does not match (wrong module, missing
    /// or non-integer counter, wrong extension).
    /// </summary>
    private static int? TryParseGeneration(string fullPath, string moduleName)
    {
        string fileName = Path.GetFileName(fullPath);
        string prefix = moduleName + ".patch.";
        const string suffix = ".dll";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }
        if (!fileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }
        string middle = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
        if (middle.Length == 0)
        {
            return null;
        }
        // The middle segment must be an unsigned decimal integer with
        // no leading sign, no embedded dots, and no other separators.
        // Use ordinal-strict int.TryParse to guarantee culture
        // independence (a Turkish locale must not change parse rules).
        if (!int.TryParse(middle, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
        {
            return null;
        }
        if (parsed < 0)
        {
            return null;
        }
        return parsed;
    }
}
