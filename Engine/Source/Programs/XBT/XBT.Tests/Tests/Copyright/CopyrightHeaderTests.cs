// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Copyright;

/// <summary>
/// Plan-level copyright validation per <c>/Documents/XBT.html</c> Section 22.1
/// ("Copyright validation: a synthetic file missing the header -> expected
/// exit 40 with the file path in the diagnostic") and Toolchain Contract
/// Rev 13 Section 10.1 step 6 ("XBT runs ValidateCopyrightAction over every
/// authored source file in the build... every file must begin with
/// <c>// Copyright Simgenics. All Rights Reserved.</c>"). At this stage XBT
/// drives that validation as a build action; this test layer covers the
/// equivalent invariant for the XBT codebase itself (Subagent B's tests
/// catch missing headers in the bootstrap before any module action exists).
/// </summary>
public sealed class CopyrightHeaderTests
{
    /// <summary>The literal header text every file must start with (minus comment marker).</summary>
    private const string ExpectedText = "Copyright Simgenics. All Rights Reserved.";

    /// <summary>
    /// File extensions we validate. Mirrors XBT.html Section 22.1 + the file
    /// set Subagent A and B together author. We deliberately exclude
    /// generated outputs (FlatSharp-emitted code, .NET SDK obj/AssemblyInfo)
    /// because those carry the upstream tool's preamble and the policy
    /// only applies to authored files.
    /// </summary>
    private static readonly string[] ValidatedExtensions =
    {
        ".cs",      // C# source
        ".csproj",  // MSBuild C# project
        ".fbs",     // FlatBuffers schema
        ".props",   // MSBuild properties
        ".targets", // MSBuild targets
        ".sln",     // Visual Studio solution
        ".html",    // README + spec docs
    };

    /// <summary>
    /// Directory names whose entire subtree is excluded. <c>obj/</c> and
    /// <c>bin/</c> hold MSBuild and FlatSharp output. <c>node_modules/</c>
    /// is reserved for future JS tooling.
    /// </summary>
    private static readonly string[] ExcludedDirectoryNames =
    {
        "obj",
        "bin",
        "node_modules",
    };

    [Fact]
    public void EveryAuthoredFile_StartsWith_SimgenicsCopyrightHeader()
    {
        string xbtRoot = ResolveXbtRoot();
        List<string> failures = new();
        int filesChecked = 0;

        foreach (string file in EnumerateAuthoredFiles(xbtRoot))
        {
            filesChecked++;
            string ext = Path.GetExtension(file);

            // Visual Studio solution files MUST begin with the
            // "Microsoft Visual Studio Solution File, Format Version XX"
            // line per the VS file format spec; the copyright header
            // appears on line 2 as a "#" comment. Inspect the first few
            // lines instead of strictly the first non-empty one.
            int linesToScan = string.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase) ? 3 : 1;

            string? header = FindHeaderLine(file, linesToScan);
            if (header is null)
            {
                failures.Add($"{file}: file is empty or first {linesToScan} non-empty line(s) lack the Simgenics header.");
                continue;
            }
            if (!header.Contains(ExpectedText, StringComparison.Ordinal))
            {
                failures.Add($"{file}: first scanned line lacks header. Got: \"{Truncate(header, 120)}\".");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"Copyright header missing on {failures.Count}/{filesChecked} authored files:\n  - "
                + string.Join("\n  - ", failures));

        // Sanity bound: if we are accidentally filtering all files (e.g. by
        // pointing at the wrong root), the test would pass vacuously. Insist
        // we found at least the count of files Subagent A + B publish.
        Assert.True(
            filesChecked >= 8,
            $"Sanity floor: expected at least 8 authored XBT files; saw {filesChecked}. Check ResolveXbtRoot.");
    }

    /// <summary>
    /// Locate <c>/Engine/Source/Programs/XBT/</c> from the test assembly's
    /// location. The test executable runs out of
    /// <c>XBT.Tests/bin/&lt;Configuration&gt;/net8.0/</c>; walk up four levels.
    /// </summary>
    private static string ResolveXbtRoot()
    {
        string asmDir = Path.GetDirectoryName(typeof(CopyrightHeaderTests).Assembly.Location)!;
        // /Engine/Source/Programs/XBT/XBT.Tests/bin/<cfg>/net8.0/ -> up 4 -> XBT.Tests
        // We want one level above XBT.Tests, i.e. XBT/.
        DirectoryInfo? cursor = new(asmDir);
        for (int i = 0; i < 6 && cursor is not null; i++)
        {
            if (string.Equals(cursor.Name, "XBT", StringComparison.OrdinalIgnoreCase))
            {
                return cursor.FullName;
            }
            cursor = cursor.Parent;
        }
        throw new InvalidOperationException(
            $"Could not locate the XBT root above {asmDir}. CopyrightHeaderTests must run from inside the repo.");
    }

    private static IEnumerable<string> EnumerateAuthoredFiles(string root)
    {
        foreach (string path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            // Skip excluded directory subtrees.
            string relative = Path.GetRelativePath(root, path);
            if (HasExcludedSegment(relative))
            {
                continue;
            }

            string ext = Path.GetExtension(path);
            if (!ValidatedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            yield return path;
        }
    }

    private static bool HasExcludedSegment(string relative)
    {
        string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (string seg in segments)
        {
            foreach (string excluded in ExcludedDirectoryNames)
            {
                if (string.Equals(seg, excluded, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static string? GetFirstNonEmptyLine(string path)
    {
        foreach (string line in File.ReadLines(path))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the first non-empty line that contains
    /// <see cref="ExpectedText"/> within the first <paramref name="maxLines"/>
    /// non-empty lines, or that first line itself if none contain the header
    /// (so the caller can report what was actually there).
    /// </summary>
    private static string? FindHeaderLine(string path, int maxLines)
    {
        string? firstSeen = null;
        int seen = 0;
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            firstSeen ??= line;
            seen++;
            if (line.Contains(ExpectedText, StringComparison.Ordinal))
            {
                return line;
            }
            if (seen >= maxLines)
            {
                break;
            }
        }
        return firstSeen;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "...";
}
