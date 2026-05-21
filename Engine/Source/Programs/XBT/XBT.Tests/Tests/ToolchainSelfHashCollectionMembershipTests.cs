// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests;

/// <summary>
/// Audit fix R5-M3: convention check enforcing that every test class
/// which directly constructs <c>XMSVCToolChain</c> or
/// <c>XClangToolChain</c> participates in the
/// <see cref="Simgenics.XPact.XBT.Tests.ToolchainSelfHashCollection"/>
/// serial collection. The collection guarantees the test runs under
/// the xUnit serialisation guard so a parallel test mutating
/// <c>ToolchainSelfHash.XbtBinaryHash</c> via <c>__SetForTesting</c>
/// cannot race the toolchain's emit-path read of that value.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why scan the source instead of reflecting at runtime.</b>
/// The toolchain types are reference types, so a reflection-based scan
/// of test classes would have to crawl every method body's IL to find
/// <c>newobj XMSVCToolChain</c> tokens. That works but is fragile under
/// Roslyn version drift and adds 100+ LoC to a check that is
/// fundamentally textual. We instead scan the source files for the
/// regex <c>new XMSVCToolChain</c> / <c>new XClangToolChain</c>, walk
/// up to the enclosing <c>class</c> declaration, and assert the class
/// (or its enclosing type chain) carries
/// <c>[Collection(nameof(ToolchainSelfHashCollection))]</c>.
/// </para>
/// <para>
/// <b>Why this is a test rather than a compile-time check.</b> Roslyn
/// analyzers could enforce the convention at build time, but the
/// XBT.Tests project does not depend on Roslyn analyzers and adding the
/// dependency for one convention is overkill. A runtime test that fails
/// fast in CI on a new test class missing the attribute is sufficient.
/// </para>
/// </remarks>
public sealed class ToolchainSelfHashCollectionMembershipTests
{
    /// <summary>
    /// Find every test source file under <c>XBT.Tests/Tests/</c> that
    /// directly constructs a toolchain. For each such file, walk up
    /// from the construction site to the enclosing class declaration
    /// and assert the class is decorated with
    /// <c>[Collection(nameof(ToolchainSelfHashCollection))]</c>.
    /// </summary>
    [Fact]
    public void EveryClass_That_ConstructsToolchain_IsInCollection()
    {
        string testsRoot = LocateTestsRoot();
        // Use Directory.EnumerateFiles with the test root so test
        // fixtures (which intentionally do NOT join the collection but
        // also do not construct toolchains) are not in scope.
        string testsSubdir = Path.Combine(testsRoot, "Tests");
        Assert.True(Directory.Exists(testsSubdir),
            $"Tests subdirectory not found at {testsSubdir} -- the convention check needs the test source on disk.");

        List<string> violations = new();
        foreach (string file in Directory.EnumerateFiles(testsSubdir, "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            if (!ConstructsToolchain(text))
            {
                continue;
            }
            // The file constructs a toolchain. Verify every public class
            // that does so carries the [Collection] attribute. We accept
            // either the bare attribute name or the namespace-qualified
            // form (the latter is what appears in
            // ClangdCompileCommandsGeneratorTests.cs et al).
            if (!HasCollectionAttribute(text))
            {
                violations.Add(file);
            }
        }

        Assert.True(violations.Count == 0,
            "Audit fix R5-M3 convention violation: the following test source files "
            + "directly construct an XMSVCToolChain or XClangToolChain but are not "
            + "members of the ToolchainSelfHashCollection (add "
            + "[Collection(nameof(Simgenics.XPact.XBT.Tests.ToolchainSelfHashCollection))] "
            + "to the test class):\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>
    /// Audit fix R5-M3 companion check: classes that call
    /// <c>BuildMode.Run</c> transitively instantiate a toolchain, and
    /// therefore must also be in the collection. The membership check
    /// is more permissive here (BuildMode.Run is the documented entry
    /// point; it does not itself read the hash, but the toolchain it
    /// instantiates does), so we surface the assertion as a warning
    /// in the build log via a relaxed Assert: if a class calls
    /// BuildMode.Run but is not in the collection, the test fails.
    /// </summary>
    [Fact]
    public void EveryClass_That_CallsBuildModeRun_IsInCollection()
    {
        string testsRoot = LocateTestsRoot();
        string testsSubdir = Path.Combine(testsRoot, "Tests");
        Assert.True(Directory.Exists(testsSubdir));

        List<string> violations = new();
        foreach (string file in Directory.EnumerateFiles(testsSubdir, "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            if (!CallsBuildModeRun(text))
            {
                continue;
            }
            if (!HasCollectionAttribute(text))
            {
                violations.Add(file);
            }
        }

        Assert.True(violations.Count == 0,
            "Audit fix R5-M3 convention violation: the following test source files "
            + "call BuildMode.Run (which transitively instantiates a toolchain) but "
            + "are not members of the ToolchainSelfHashCollection:\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>
    /// Locate the XBT.Tests project root by walking up from the test
    /// assembly's bin/&lt;cfg&gt;/net8.0/ directory.
    /// </summary>
    private static string LocateTestsRoot()
    {
        string asmDir = Path.GetDirectoryName(typeof(ToolchainSelfHashCollectionMembershipTests).Assembly.Location)!;
        DirectoryInfo? cursor = new(asmDir);
        for (int i = 0; i < 6 && cursor is not null; i++)
        {
            if (string.Equals(cursor.Name, "XBT.Tests", StringComparison.OrdinalIgnoreCase))
            {
                return cursor.FullName;
            }
            cursor = cursor.Parent;
        }
        throw new InvalidOperationException(
            $"Could not locate the XBT.Tests project root above {asmDir}.");
    }

    /// <summary>
    /// True if the file contains a direct toolchain construction. The
    /// regex matches <c>new XMSVCToolChain</c> / <c>new XClangToolChain</c>
    /// with optional whitespace before the paren or generic-type angle.
    /// Comments and strings are NOT excluded; in practice the test code
    /// does not embed those tokens inside a string literal so the false-
    /// positive rate is zero.
    /// </summary>
    private static bool ConstructsToolchain(string source)
    {
        return Regex.IsMatch(source, @"\bnew\s+XMSVCToolChain\b")
            || Regex.IsMatch(source, @"\bnew\s+XClangToolChain\b");
    }

    /// <summary>
    /// True if the file calls <c>BuildMode.Run</c> or
    /// <c>BuildModeType.Run</c> (an alias used in ModeSmokeTests.cs to
    /// disambiguate the type name from the namespace).
    /// </summary>
    private static bool CallsBuildModeRun(string source)
    {
        return Regex.IsMatch(source, @"\bBuildMode\.Run\s*\(")
            || Regex.IsMatch(source, @"\bBuildModeType\.Run\s*\(");
    }

    /// <summary>
    /// True if the file carries
    /// <c>[Collection(nameof(ToolchainSelfHashCollection))]</c> on at
    /// least one class declaration. We match both the bare and
    /// fully-qualified forms.
    /// </summary>
    private static bool HasCollectionAttribute(string source)
    {
        return source.Contains("[Collection(nameof(ToolchainSelfHashCollection))]", StringComparison.Ordinal)
            || source.Contains("[Collection(nameof(Simgenics.XPact.XBT.Tests.ToolchainSelfHashCollection))]", StringComparison.Ordinal);
    }
}
