// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests;

/// <summary>
/// XBT-side mirror of <c>XHT.Tests.RevisionCiteTests</c>. Lock-in test for
/// the repo-wide convention that CURRENT-cite references to
/// <c>/Documents/XHT.html</c> in XBT-side source files point at the latest
/// revision. Round 11 R10-1 introduced this guard after a Round-10
/// convergence audit found 6 stale "Rev 5" cites in XBT-side files that
/// XHT.Tests's XHT-only scope bypassed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why mirror (not extend XHT.Tests).</b> Each test assembly owns its
/// own source-tree scan and scopes it to its own tier; XBT-test asserts
/// XBT-source cite discipline, XHT-test asserts XHT-source cite
/// discipline. No cross-assembly dependency and no XHT.Tests reaching
/// into XBT source. The cost is a one-line constant
/// <see cref="CurrentXhtRevision"/> duplicated across both files; the
/// test failures (when one is bumped without the other) make the
/// requirement self-documenting.
/// </para>
/// <para>
/// <b>KEEP IN SYNC WITH</b>
/// <c>Engine/Source/Programs/XHT/XHT.Tests/Tests/RevisionCiteTests.cs</c>
/// <c>CurrentXhtRevision</c>. Both constants must be hand-bumped in
/// lockstep with the XHT.html revision header.
/// </para>
/// <para>
/// <b>Scope.</b> The scan walks every <c>.cs</c> / <c>.csproj</c> /
/// <c>.props</c> / <c>.targets</c> file under
/// <c>Engine/Source/Programs/XBT/</c> (excluding <c>obj/</c> / <c>bin/</c>)
/// and asserts that no file contains a <em>current-cite</em>
/// reference to an older revision of XHT.html.
/// </para>
/// <para>
/// <b>What counts as a current-cite.</b> A reference of the form
/// <c>/Documents/XHT.html Rev N</c> or <c>XHT.html</c>&lt;newline&gt;<c>/// Rev N</c>
/// is treated as a current-cite. Historical narrative (e.g., "Rev 5
/// placed RecursiveStructCheck...", "Rev 6 reordered the phase") does
/// NOT match these patterns and is preserved as design-history record.
/// </para>
/// <para>
/// <b>How to bump.</b> When XHT.html bumps to a new revision, update
/// <see cref="CurrentXhtRevision"/> below AND the matching constant in
/// <c>XHT.Tests/Tests/RevisionCiteTests.cs</c>. Either test will then
/// fail for every file in its tier that still cites the prior revision;
/// the combined failure list IS the cross-tier sweep checklist for the
/// round.
/// </para>
/// </remarks>
public class XhtRevisionCiteTests
{
    /// <summary>
    /// The currently-published XHT.html revision. Hand-bump in lockstep
    /// with the XHT.html doc header on every revision.
    /// <para>
    /// <b>KEEP IN SYNC WITH</b>
    /// <c>XHT.Tests/Tests/RevisionCiteTests.cs CurrentXhtRevision</c>.
    /// </para>
    /// </summary>
    private const int CurrentXhtRevision = 8;

    [Fact]
    public void XBT_Source_Tree_Contains_No_StaleXHTHtml_CurrentCites()
    {
        string xbtRoot = FindXbtSourceRoot();
        Assert.True(
            Directory.Exists(xbtRoot),
            $"Could not locate the XBT source tree root (tried walking up from {AppContext.BaseDirectory}). "
            + "XhtRevisionCiteTests must run from a build output beneath the repo root.");

        // Match the inline form "XHT.html ... Rev N" where the prose
        // between XHT.html and the Rev token is short and does NOT
        // contain another doc reference (Contract, Addendum, XBT.html,
        // etc.). A literal "Contract" or "Addendum" word in the span
        // means the "Rev N" refers to that other doc, not XHT.html.
        // Also match the line-wrapped form: "XHT.html\n/// Rev N" with
        // possibly trailing "</c>" before the newline.
        // Identical regexes to XHT.Tests/RevisionCiteTests.cs so the
        // two scans use the exact same cite-detection rules.
        Regex inlineCite = new(
            @"XHT\.html(?:[^\n.](?!Contract|Addendum|XBT\.html|Rev 13)){0,40}?Rev\s+(\d+)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        Regex wrappedCite = new(
            @"XHT\.html(?:</c>)?\s*\r?\n[\s/]+Rev\s+(\d+)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        List<string> violations = new();
        foreach (string path in EnumerateSourceFiles(xbtRoot))
        {
            // Skip this test file itself; it documents the convention by
            // defining CurrentXhtRevision and would otherwise self-match
            // on the comment text above.
            string relName = Path.GetFileName(path);
            if (relName.Equals("XhtRevisionCiteTests.cs", StringComparison.Ordinal))
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch
            {
                continue; // unreadable; skip
            }

            CollectMismatches(path, text, inlineCite, violations);
            CollectMismatches(path, text, wrappedCite, violations);
        }

        Assert.True(
            violations.Count == 0,
            $"Found {violations.Count} stale XHT.html current-cite reference(s) "
            + $"in XBT source that should be at Rev {CurrentXhtRevision}:\n"
            + string.Join("\n", violations));
    }

    private static void CollectMismatches(string path, string text, Regex re, List<string> violations)
    {
        foreach (Match m in re.Matches(text))
        {
            if (!int.TryParse(m.Groups[1].Value, out int n))
            {
                continue;
            }
            if (n == CurrentXhtRevision)
            {
                continue;
            }
            // Locate the line number for a useful diagnostic.
            int lineNum = 1 + CountChar(text, '\n', m.Index);
            violations.Add($"  {path}:{lineNum} -- cites Rev {n} (current is Rev {CurrentXhtRevision})");
        }
    }

    private static int CountChar(string s, char c, int upToExclusive)
    {
        int count = 0;
        for (int i = 0; i < upToExclusive && i < s.Length; i++)
        {
            if (s[i] == c)
            {
                count++;
            }
        }
        return count;
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        foreach (string ext in new[] { "*.cs", "*.csproj", "*.props", "*.targets" })
        {
            foreach (string path in Directory.EnumerateFiles(root, ext, SearchOption.AllDirectories))
            {
                if (PathHasSegment(path, "obj") || PathHasSegment(path, "bin"))
                {
                    continue;
                }
                yield return path;
            }
        }
    }

    private static bool PathHasSegment(string path, string segment)
    {
        string norm = path.Replace('\\', '/');
        return norm.Contains("/" + segment + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Walk up from the test binary directory to find the XBT source
    /// root at <c>Engine/Source/Programs/XBT/</c>. Mirrors the discipline
    /// in <c>XHT.Tests.RevisionCiteTests.FindXhtSourceRoot</c>.
    /// </summary>
    private static string FindXbtSourceRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "Engine", "Source", "Programs", "XBT");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return string.Empty; // sentinel; Assert.True will fail
    }
}
