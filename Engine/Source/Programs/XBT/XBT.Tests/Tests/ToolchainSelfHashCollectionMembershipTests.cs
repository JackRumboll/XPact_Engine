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
/// Audit fix R5-M3 (extended Round 7 R6-X1/R6-X2): convention check
/// enforcing that every test class which directly constructs
/// <c>XMSVCToolChain</c> or <c>XClangToolChain</c> participates in the
/// <see cref="Simgenics.XPact.XBT.Tests.ToolchainSelfHashCollection"/>
/// serial collection. The collection guarantees the test runs under the
/// xUnit serialisation guard so a parallel test mutating
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
/// fundamentally textual.
/// </para>
/// <para>
/// <b>Class-scoped attribute binding (R6-X2).</b> The detector locates
/// every class declaration in the source text and binds the
/// <c>[Collection(...)]</c> attribute to the next class declaration that
/// follows it. A construction site is mapped to the lexically enclosing
/// class by counting <c>{</c>/<c>}</c> tokens (with string/char/comment
/// awareness). This means a multi-class file in which class A carries
/// the attribute and class B (without the attribute) constructs a
/// toolchain correctly flags class B as the violator -- a guarantee the
/// previous file-scoped substring check could not provide.
/// </para>
/// <para>
/// <b>Positive control (R6-X1).</b>
/// <see cref="Detector_RejectsKnownViolatorAndAcceptsKnownAdherent"/>
/// runs the same detection logic over in-memory string fixtures with a
/// known-good adherent and a known-bad violator. The control proves the
/// detector still catches violations after any future refactor; without
/// it a typo that broke the regex would let real violators pass
/// silently because no on-disk file would match.
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
            // R6-X2: bind each construction site to its enclosing
            // class declaration so a multi-class file is correctly
            // diagnosed when one class adheres but another does not.
            IReadOnlyList<string> classesWithoutCollection = FindToolchainConstructorClassesWithoutCollectionAttribute(text);
            foreach (string className in classesWithoutCollection)
            {
                violations.Add($"{file} :: class {className}");
            }
        }

        Assert.True(violations.Count == 0,
            "Audit fix R5-M3 convention violation: the following test classes "
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
            IReadOnlyList<string> classesWithoutCollection = FindBuildModeRunCallerClassesWithoutCollectionAttribute(text);
            foreach (string className in classesWithoutCollection)
            {
                violations.Add($"{file} :: class {className}");
            }
        }

        Assert.True(violations.Count == 0,
            "Audit fix R5-M3 convention violation: the following test classes "
            + "call BuildMode.Run (which transitively instantiates a toolchain) but "
            + "are not members of the ToolchainSelfHashCollection:\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>
    /// Audit fix R6-X1 positive control. Exercises the same detection
    /// logic the on-disk tests use against in-memory string fixtures so
    /// a future refactor that broke the regex (e.g., a typo making
    /// <c>new XMSVCToolChain</c> never match) is caught immediately.
    /// Without this test the detector could silently no-op and let real
    /// violators ship.
    /// </summary>
    /// <remarks>
    /// The fixtures intentionally exercise three discriminating shapes:
    /// <list type="bullet">
    /// <item>A class that constructs a toolchain WITHOUT the attribute
    /// -- must be reported.</item>
    /// <item>A class that constructs a toolchain WITH the attribute
    /// -- must NOT be reported.</item>
    /// <item>A multi-class file where one class adheres and a second
    /// class (in the same file) constructs the toolchain without the
    /// attribute -- the violator must be reported. This is the
    /// regression test for R6-X2.</item>
    /// </list>
    /// </remarks>
    [Fact]
    public void Detector_RejectsKnownViolatorAndAcceptsKnownAdherent()
    {
        // --- Fixture 1: lone violator. ---
        string violator = @"
namespace Demo;
public sealed class FakeViolator
{
    public FakeViolator()
    {
        var t = new XMSVCToolChain(env, repoRoot: @""C:\repo"");
    }
}";

        IReadOnlyList<string> violatorReport =
            FindToolchainConstructorClassesWithoutCollectionAttribute(violator);
        Assert.Single(violatorReport);
        Assert.Equal("FakeViolator", violatorReport[0]);

        // --- Fixture 2: lone adherent. ---
        string adherent = @"
namespace Demo;
[Collection(nameof(ToolchainSelfHashCollection))]
public sealed class FakeAdherent
{
    public FakeAdherent()
    {
        var t = new XClangToolChain(env, repoRoot: @""C:\repo"");
    }
}";

        IReadOnlyList<string> adherentReport =
            FindToolchainConstructorClassesWithoutCollectionAttribute(adherent);
        Assert.Empty(adherentReport);

        // --- Fixture 3: multi-class file. R6-X2 regression. ---
        // Class A carries the attribute and does NOT construct a
        // toolchain; class B (no attribute) DOES construct one. The
        // old file-scoped substring check would have passed this file
        // because the attribute substring is present somewhere in the
        // file body. The new class-scoped check must report only B.
        string multiClass = @"
namespace Demo;

[Collection(nameof(ToolchainSelfHashCollection))]
public sealed class FakeAdherentSibling
{
    public void Unrelated() { }
}

public sealed class FakeViolatorSibling
{
    public FakeViolatorSibling()
    {
        var t = new XMSVCToolChain(env, repoRoot: @""C:\repo"");
    }
}";

        IReadOnlyList<string> multiClassReport =
            FindToolchainConstructorClassesWithoutCollectionAttribute(multiClass);
        Assert.Single(multiClassReport);
        Assert.Equal("FakeViolatorSibling", multiClassReport[0]);

        // --- Fixture 4: fully-qualified attribute form. ---
        string adherentFq = @"
namespace Demo;
[Collection(nameof(Simgenics.XPact.XBT.Tests.ToolchainSelfHashCollection))]
public sealed class FakeAdherentFq
{
    public FakeAdherentFq()
    {
        var t = new XMSVCToolChain(env, repoRoot: @""C:\repo"");
    }
}";

        IReadOnlyList<string> adherentFqReport =
            FindToolchainConstructorClassesWithoutCollectionAttribute(adherentFq);
        Assert.Empty(adherentFqReport);

        // --- Fixture 5: no construction at all -- must not report. ---
        string noConstruction = @"
namespace Demo;
public sealed class IndifferentClass
{
    public void Unrelated() { }
}";
        IReadOnlyList<string> noConstructionReport =
            FindToolchainConstructorClassesWithoutCollectionAttribute(noConstruction);
        Assert.Empty(noConstructionReport);
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

    // ----------------------------------------------------------------
    // R6-X1/R6-X2 testable detector. Encapsulates the parser logic so
    // the positive-control test exercises the SAME code path the on-
    // disk tests run, and so future maintenance changes the detector
    // in exactly one place.
    // ----------------------------------------------------------------

    /// <summary>
    /// R6-X2 testable detector. Returns the names of every class in
    /// <paramref name="source"/> that directly constructs a toolchain
    /// (matching <c>new XMSVCToolChain</c> or <c>new XClangToolChain</c>)
    /// but is NOT decorated with the
    /// <c>[Collection(nameof(ToolchainSelfHashCollection))]</c>
    /// attribute (either bare or fully-qualified form). The empty list
    /// indicates either no constructions in the source or every
    /// construction's enclosing class is in the collection.
    /// </summary>
    internal static IReadOnlyList<string> FindToolchainConstructorClassesWithoutCollectionAttribute(string source)
    {
        return FindOffendingClasses(
            source,
            constructorRegex: ToolchainConstructorRegex);
    }

    /// <summary>
    /// R6-X2 testable detector companion for the BuildMode.Run path.
    /// Same semantics as
    /// <see cref="FindToolchainConstructorClassesWithoutCollectionAttribute"/>
    /// but matches BuildMode.Run / BuildModeType.Run call sites.
    /// </summary>
    internal static IReadOnlyList<string> FindBuildModeRunCallerClassesWithoutCollectionAttribute(string source)
    {
        return FindOffendingClasses(
            source,
            constructorRegex: BuildModeRunRegex);
    }

    /// <summary>
    /// Matches <c>new XMSVCToolChain</c> / <c>new XClangToolChain</c>.
    /// Compiled once for use across both the on-disk scan and the
    /// positive-control test fixtures.
    /// </summary>
    private static readonly Regex ToolchainConstructorRegex = new(
        @"\bnew\s+(XMSVCToolChain|XClangToolChain)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches <c>BuildMode.Run(</c> / <c>BuildModeType.Run(</c>.
    /// </summary>
    private static readonly Regex BuildModeRunRegex = new(
        @"\b(BuildMode|BuildModeType)\.Run\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches <c>[Collection(nameof(...ToolchainSelfHashCollection))]</c>
    /// in either bare or namespace-qualified form.
    /// </summary>
    private static readonly Regex CollectionAttributeRegex = new(
        @"\[Collection\(nameof\((?:[A-Za-z_][A-Za-z_0-9.]*\.)?ToolchainSelfHashCollection\)\)\]",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches a class declaration, capturing the class name. Excludes
    /// using-directives and namespace declarations. Tolerates standard
    /// modifiers in any order.
    /// </summary>
    private static readonly Regex ClassDeclarationRegex = new(
        @"\bclass\s+([A-Za-z_][A-Za-z_0-9]*)",
        RegexOptions.Compiled);

    /// <summary>
    /// Core detector. Walks the source text, locates every class
    /// declaration with its body span, finds every constructor-match
    /// occurrence, and for each match returns the name of the
    /// enclosing class if that class does not carry the
    /// <c>[Collection(...)]</c> attribute. Class membership is
    /// determined by lexical containment (the match offset falls
    /// inside the class body's <c>{ }</c> span). Attribute membership
    /// is determined by scanning the source between the preceding
    /// class-body end (or the start of file) and the class's
    /// declaration token for the
    /// <see cref="CollectionAttributeRegex"/>.
    /// </summary>
    private static IReadOnlyList<string> FindOffendingClasses(string source, Regex constructorRegex)
    {
        // Step 1: strip comments and string literals so brace-counting
        // and attribute detection are not fooled by braces/attribute-
        // looking text inside comments and strings.
        string stripped = StripCommentsAndStrings(source);

        // Step 2: index every class declaration with its body span.
        List<ClassSpan> classSpans = LocateClassSpans(stripped);

        // Step 3: for every constructor match, identify the lexically
        // enclosing class and check whether that class carries the
        // attribute.
        HashSet<string> offenders = new();
        foreach (Match m in constructorRegex.Matches(stripped))
        {
            ClassSpan? enclosing = FindInnermostEnclosingClass(classSpans, m.Index);
            if (enclosing is null)
            {
                // Match at file scope (top-level statement, etc.).
                // This shouldn't happen in real test files; if it
                // does, treat as a violation against a synthetic
                // "<top-level>" pseudo-class so the failure is loud.
                offenders.Add("<top-level>");
                continue;
            }
            // Climb to the outermost containing class so the attribute
            // check matches a public top-level class. A nested helper
            // class that does the construction inherits the outer
            // class's serialisation status, which is the correct
            // semantics for xUnit's [Collection] attribute (xUnit
            // serialises on the top-level test class, not nested
            // private helpers).
            ClassSpan outermost = FindOutermostContainingClass(classSpans, enclosing.Value);
            if (!ClassHasCollectionAttribute(stripped, outermost))
            {
                offenders.Add(outermost.Name);
            }
        }

        return offenders.ToList();
    }

    /// <summary>
    /// Replace the contents of comments and string/char/verbatim/raw
    /// literals with spaces so the resulting text has the same indices
    /// as the original but no syntactic content inside those regions
    /// can confuse the brace counter or attribute regex. Newlines are
    /// preserved so error messages can use the source offset.
    /// </summary>
    /// <remarks>
    /// Replacement-with-spaces (rather than removal) is deliberate:
    /// every regex match index in <see cref="FindOffendingClasses"/>
    /// is used to locate the enclosing class span, so offsets must
    /// not shift relative to the original source.
    /// </remarks>
    private static string StripCommentsAndStrings(string source)
    {
        char[] buf = source.ToCharArray();
        int i = 0;
        int n = buf.Length;
        while (i < n)
        {
            char c = buf[i];
            // Line comment: // ... newline
            if (c == '/' && i + 1 < n && buf[i + 1] == '/')
            {
                while (i < n && buf[i] != '\n')
                {
                    buf[i] = ' ';
                    i++;
                }
                continue;
            }
            // Block comment: /* ... */
            if (c == '/' && i + 1 < n && buf[i + 1] == '*')
            {
                buf[i] = ' ';
                buf[i + 1] = ' ';
                i += 2;
                while (i + 1 < n && !(buf[i] == '*' && buf[i + 1] == '/'))
                {
                    if (buf[i] != '\n') { buf[i] = ' '; }
                    i++;
                }
                if (i + 1 < n)
                {
                    buf[i] = ' ';
                    buf[i + 1] = ' ';
                    i += 2;
                }
                continue;
            }
            // Verbatim string: @"..."" closes with a single " that is
            // not followed by another ". Embedded "" is an escaped
            // quote.
            if (c == '@' && i + 1 < n && buf[i + 1] == '"')
            {
                buf[i] = ' ';
                buf[i + 1] = ' ';
                i += 2;
                while (i < n)
                {
                    if (buf[i] == '"')
                    {
                        if (i + 1 < n && buf[i + 1] == '"')
                        {
                            buf[i] = ' ';
                            buf[i + 1] = ' ';
                            i += 2;
                            continue;
                        }
                        buf[i] = ' ';
                        i++;
                        break;
                    }
                    if (buf[i] != '\n') { buf[i] = ' '; }
                    i++;
                }
                continue;
            }
            // Interpolated verbatim: $@"..." or @$"..." -- handle the
            // same way as plain verbatim. The detector is not parsing
            // expressions so we just need to consume the literal.
            if ((c == '$' || c == '@')
                && i + 2 < n
                && (buf[i + 1] == '@' || buf[i + 1] == '$')
                && buf[i + 2] == '"')
            {
                buf[i] = ' ';
                buf[i + 1] = ' ';
                buf[i + 2] = ' ';
                i += 3;
                while (i < n)
                {
                    if (buf[i] == '"')
                    {
                        if (i + 1 < n && buf[i + 1] == '"')
                        {
                            buf[i] = ' ';
                            buf[i + 1] = ' ';
                            i += 2;
                            continue;
                        }
                        buf[i] = ' ';
                        i++;
                        break;
                    }
                    if (buf[i] != '\n') { buf[i] = ' '; }
                    i++;
                }
                continue;
            }
            // Regular string: "..." with backslash escapes.
            if (c == '"')
            {
                buf[i] = ' ';
                i++;
                while (i < n && buf[i] != '"')
                {
                    if (buf[i] == '\\' && i + 1 < n)
                    {
                        if (buf[i] != '\n') { buf[i] = ' '; }
                        i++;
                        if (i < n && buf[i] != '\n') { buf[i] = ' '; }
                        i++;
                        continue;
                    }
                    if (buf[i] == '\n') { i++; break; }
                    buf[i] = ' ';
                    i++;
                }
                if (i < n && buf[i] == '"') { buf[i] = ' '; i++; }
                continue;
            }
            // Char literal: '\?' or '?'. Replace with space.
            if (c == '\'')
            {
                buf[i] = ' ';
                i++;
                while (i < n && buf[i] != '\'')
                {
                    if (buf[i] == '\\' && i + 1 < n)
                    {
                        if (buf[i] != '\n') { buf[i] = ' '; }
                        i++;
                        if (i < n && buf[i] != '\n') { buf[i] = ' '; }
                        i++;
                        continue;
                    }
                    if (buf[i] == '\n') { i++; break; }
                    buf[i] = ' ';
                    i++;
                }
                if (i < n && buf[i] == '\'') { buf[i] = ' '; i++; }
                continue;
            }
            i++;
        }
        return new string(buf);
    }

    /// <summary>
    /// A class declaration's textual span: name, the offset of the
    /// declaration token (the <c>class</c> keyword), and the offset
    /// of the matching opening brace and its closing brace.
    /// </summary>
    private readonly record struct ClassSpan(
        string Name,
        int DeclarationOffset,
        int OpenBraceOffset,
        int CloseBraceOffset);

    /// <summary>
    /// Locate every class declaration's body span by matching the
    /// <c>class</c> keyword + name, then finding the matching opening
    /// <c>{</c> and its closing <c>}</c> via brace counting.
    /// </summary>
    private static List<ClassSpan> LocateClassSpans(string strippedSource)
    {
        List<ClassSpan> spans = new();
        foreach (Match m in ClassDeclarationRegex.Matches(strippedSource))
        {
            string name = m.Groups[1].Value;
            // Find the opening brace following the class declaration.
            // C# allows <T> and `: Base, IFoo, IBar` between the name
            // and the brace, so we scan forward until we hit either
            // '{' (a body) or ';' (a forward-declaration; not valid
            // C# for classes but defensively skip).
            int openBrace = -1;
            for (int i = m.Index + m.Length; i < strippedSource.Length; i++)
            {
                char c = strippedSource[i];
                if (c == '{')
                {
                    openBrace = i;
                    break;
                }
                if (c == ';')
                {
                    // Not a body; skip.
                    break;
                }
            }
            if (openBrace < 0)
            {
                continue;
            }
            int closeBrace = FindMatchingCloseBrace(strippedSource, openBrace);
            if (closeBrace < 0)
            {
                continue;
            }
            spans.Add(new ClassSpan(name, m.Index, openBrace, closeBrace));
        }
        return spans;
    }

    /// <summary>
    /// Given the index of an opening brace, return the index of its
    /// matching closing brace, or -1 if the braces are unbalanced.
    /// </summary>
    private static int FindMatchingCloseBrace(string strippedSource, int openBraceOffset)
    {
        int depth = 0;
        for (int i = openBraceOffset; i < strippedSource.Length; i++)
        {
            char c = strippedSource[i];
            if (c == '{') { depth++; }
            else if (c == '}')
            {
                depth--;
                if (depth == 0) { return i; }
            }
        }
        return -1;
    }

    /// <summary>
    /// Return the innermost class span that contains
    /// <paramref name="offset"/>, or null if the offset is outside
    /// every class body. "Innermost" means the class whose body span
    /// is fully contained in every other containing class's span.
    /// </summary>
    private static ClassSpan? FindInnermostEnclosingClass(List<ClassSpan> classSpans, int offset)
    {
        ClassSpan? innermost = null;
        int innermostSize = int.MaxValue;
        foreach (ClassSpan span in classSpans)
        {
            if (offset > span.OpenBraceOffset && offset < span.CloseBraceOffset)
            {
                int size = span.CloseBraceOffset - span.OpenBraceOffset;
                if (size < innermostSize)
                {
                    innermost = span;
                    innermostSize = size;
                }
            }
        }
        return innermost;
    }

    /// <summary>
    /// Return the outermost class that contains
    /// <paramref name="span"/>, or <paramref name="span"/> itself if
    /// nothing contains it. The outermost class is the one whose body
    /// span fully contains <paramref name="span"/>'s declaration
    /// offset and whose own span is not contained in any other.
    /// </summary>
    private static ClassSpan FindOutermostContainingClass(List<ClassSpan> classSpans, ClassSpan span)
    {
        ClassSpan outermost = span;
        bool changed;
        do
        {
            changed = false;
            foreach (ClassSpan candidate in classSpans)
            {
                if (candidate.Equals(outermost)) { continue; }
                if (outermost.DeclarationOffset > candidate.OpenBraceOffset
                    && outermost.CloseBraceOffset < candidate.CloseBraceOffset)
                {
                    outermost = candidate;
                    changed = true;
                    break;
                }
            }
        }
        while (changed);
        return outermost;
    }

    /// <summary>
    /// True if the source carries a
    /// <c>[Collection(nameof(ToolchainSelfHashCollection))]</c>
    /// attribute on the class indicated by <paramref name="span"/>.
    /// The attribute must appear in the region between the previous
    /// class's close-brace (or the start of file) and the target
    /// class's declaration offset -- this is the only region where a
    /// C# attribute can legitimately bind to the target class.
    /// </summary>
    /// <remarks>
    /// We do NOT search the whole file: that was the R6-X2 bug. We
    /// also do NOT search inside the class body because attributes
    /// inside the body bind to members, not the class itself.
    /// </remarks>
    private static bool ClassHasCollectionAttribute(string strippedSource, ClassSpan span)
    {
        // Lower bound: end of the immediately-preceding class body, or
        // start of file. Searching from start of file is correct only
        // when there is no preceding class because attributes between
        // a previous class's close-brace and the target class's open-
        // brace bind to whatever follows them (which is the target).
        // We compute the lower bound as the maximum close-brace offset
        // among classes whose CloseBraceOffset < span.DeclarationOffset.
        int lower = 0;
        foreach (ClassSpan other in
            CollectionsExt.WhereClassPrecedes(strippedSource, span))
        {
            if (other.CloseBraceOffset > lower && other.CloseBraceOffset < span.DeclarationOffset)
            {
                lower = other.CloseBraceOffset;
            }
        }

        // Upper bound: the target class's declaration offset
        // (exclusive). Attributes after that point are not attached
        // to this class.
        int upper = span.DeclarationOffset;
        if (upper <= lower)
        {
            return false;
        }
        string region = strippedSource.Substring(lower, upper - lower);
        return CollectionAttributeRegex.IsMatch(region);
    }

    /// <summary>
    /// Helper that enumerates every class span in
    /// <paramref name="source"/> -- used by
    /// <see cref="ClassHasCollectionAttribute"/> to find the
    /// preceding class. Recomputing the span list per call is fine
    /// here because the on-disk test sweeps tens of files, not
    /// thousands, and the input is already cached as the
    /// stripped-source string.
    /// </summary>
    private static class CollectionsExt
    {
        public static IEnumerable<ClassSpan> WhereClassPrecedes(string strippedSource, ClassSpan target)
        {
            foreach (ClassSpan other in LocateClassSpans(strippedSource))
            {
                if (!other.Equals(target)
                    && other.CloseBraceOffset < target.DeclarationOffset)
                {
                    yield return other;
                }
            }
        }
    }
}
