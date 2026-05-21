// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests;

/// <summary>
/// Lock-in test for the repo-wide convention that CURRENT-cite references
/// to <c>/Documents/XHT.html</c> point at the latest revision. Round 5
/// R4-MA3 introduced this regression guard after a Round-4 audit found
/// 70+ stale "Rev 5" cites across the XHT codebase.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> The scan walks every <c>.cs</c> / <c>.csproj</c> /
/// <c>.props</c> / <c>.targets</c> file under
/// <c>Engine/Source/Programs/XHT/</c> (excluding <c>obj/</c> / <c>bin/</c>)
/// and asserts that no file contains a <em>current-cite</em>
/// reference to an older revision of XHT.html. The current revision is
/// recorded at <see cref="CurrentXhtRevision"/> and must be hand-bumped
/// in lockstep with the XHT.html revision header.
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
/// <see cref="CurrentXhtRevision"/> below. The test will then fail for
/// every file that still cites the prior revision; the failure list IS
/// the sweep checklist for the round.
/// </para>
/// </remarks>
public class RevisionCiteTests
{
    /// <summary>
    /// The currently-published XHT.html revision. Hand-bump in lockstep
    /// with the XHT.html doc header on every revision.
    /// </summary>
    private const int CurrentXhtRevision = 8;

    [Fact]
    public void XHT_Source_Tree_Contains_No_StaleXHTHtml_CurrentCites()
    {
        string xhtRoot = FindXhtSourceRoot();
        Assert.True(
            Directory.Exists(xhtRoot),
            $"Could not locate the XHT source tree root (tried walking up from {AppContext.BaseDirectory}). "
            + "RevisionCiteTests must run from a build output beneath the repo root.");

        // Match the inline form "XHT.html ... Rev N" where the prose
        // between XHT.html and the Rev token is short and does NOT
        // contain another doc reference (Contract, Addendum, XBT.html,
        // etc.). A literal "Contract" or "Addendum" word in the span
        // means the "Rev N" refers to that other doc, not XHT.html.
        // Also match the line-wrapped form: "XHT.html\n/// Rev N" with
        // possibly trailing "</c>" before the newline.
        Regex inlineCite = new(
            @"XHT\.html(?:[^\n.](?!Contract|Addendum|XBT\.html|Rev 13)){0,40}?Rev\s+(\d+)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        Regex wrappedCite = new(
            @"XHT\.html(?:</c>)?\s*\r?\n[\s/]+Rev\s+(\d+)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        List<string> violations = new();
        foreach (string path in EnumerateSourceFiles(xhtRoot))
        {
            // Skip this test file itself; it documents the convention by
            // defining CurrentXhtRevision = 7 and would otherwise self-match.
            string relName = Path.GetFileName(path);
            if (relName.Equals("RevisionCiteTests.cs", StringComparison.Ordinal))
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
            + $"that should be at Rev {CurrentXhtRevision}:\n"
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
    /// Walk up from the test binary directory to find the XHT source
    /// root at <c>Engine/Source/Programs/XHT/</c>. Mirrors the discipline
    /// in <c>GeneratedCodeCompilesTests.FindXCoreIncludeRoot</c>.
    /// </summary>
    private static string FindXhtSourceRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "Engine", "Source", "Programs", "XHT");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return string.Empty; // sentinel; Assert.True will fail
    }
}
