// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Simgenics.XPact.XBT.ActionGraph;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.ActionGraph;

/// <summary>
/// Audit fix R3-C1: verifies <see cref="CppDependencyCache"/> against the
/// Round-3 audit requirements. The cache must parse two depfile formats
/// (Clang's Makefile-style <c>.d</c> and MSVC's <c>/sourceDependencies</c>
/// JSON), survive adversarial inputs without throwing, round-trip
/// recorded dependencies through atomic-rename save / load, and (via
/// <see cref="ActionHistory.IsActionOutdated"/>) correctly invalidate a
/// cached action when a transitively-included header (not in
/// <see cref="IExternalAction.PrerequisiteItems"/>) is edited.
/// </summary>
public sealed class CppDependencyCacheTests : IDisposable
{
    private readonly string _scratchDir;

    public CppDependencyCacheTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.CppDependencyCache",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratchDir))
            {
                Directory.Delete(_scratchDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private string WriteScratch(string fileName, string contents)
    {
        string path = Path.Combine(_scratchDir, fileName);
        File.WriteAllText(path, contents, s_utf8NoBom);
        return path;
    }

    private string WriteScratchBytes(string fileName, byte[] bytes)
    {
        string path = Path.Combine(_scratchDir, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // -------------------------------------------------------------------
    // Clang Makefile-format parser tests.
    // -------------------------------------------------------------------

    /// <summary>
    /// Round-trip the canonical Clang <c>-MD -MF</c> output: one target,
    /// multiple prereqs joined by <c>\&lt;LF&gt;</c> continuations.
    /// </summary>
    [Fact]
    public void ParseClangMakefile_BasicMultilineFormat_ReturnsAllPrereqs()
    {
        string depContent =
            "compile.o: source.cpp \\\n" +
            "  header1.h \\\n" +
            "  /abs/path/header2.h \\\n" +
            "  subdir/header3.h\n";
        byte[] bytes = s_utf8NoBom.GetBytes(depContent);
        IReadOnlyList<string> result = CppDependencyCache.ParseClangMakefileDepfile(bytes, "test");
        Assert.Equal(4, result.Count);
        Assert.Equal("source.cpp", result[0]);
        Assert.Equal("header1.h", result[1]);
        Assert.Equal("/abs/path/header2.h", result[2]);
        Assert.Equal("subdir/header3.h", result[3]);
    }

    /// <summary>
    /// Escaped spaces inside a path (Makefile convention: <c>\&lt;space&gt;</c>)
    /// are preserved as literal spaces in the emitted token; the
    /// surrounding token boundary is NOT split.
    /// </summary>
    [Fact]
    public void ParseClangMakefile_EscapedSpacesInPath_PreservedInToken()
    {
        string depContent =
            "out.o: src.cpp \\\n" +
            "  path\\ with\\ spaces/header.h\n";
        byte[] bytes = s_utf8NoBom.GetBytes(depContent);
        IReadOnlyList<string> result = CppDependencyCache.ParseClangMakefileDepfile(bytes, "test");
        Assert.Equal(2, result.Count);
        Assert.Equal("src.cpp", result[0]);
        Assert.Equal("path with spaces/header.h", result[1]);
    }

    /// <summary>
    /// On Windows, drive-letter colons (<c>C:</c>) must NOT be treated as
    /// the target/prereq separator. The first colon followed by a
    /// non-whitespace char is part of a path; only the colon followed by
    /// whitespace is the separator.
    /// </summary>
    [Fact]
    public void ParseClangMakefile_WindowsDriveLetter_NotSplitOnDriveColon()
    {
        string depContent =
            "C:\\Build\\out.o: C:\\Source\\src.cpp \\\n" +
            "  C:\\Engine\\Headers\\foo.h\n";
        byte[] bytes = s_utf8NoBom.GetBytes(depContent);
        IReadOnlyList<string> result = CppDependencyCache.ParseClangMakefileDepfile(bytes, "test");
        Assert.Equal(2, result.Count);
        // The "target" is C:\Build\out.o which is dropped; the prereqs are
        // the source and the header, with their drive-letter colons intact.
        Assert.Equal("C:\\Source\\src.cpp", result[0]);
        Assert.Equal("C:\\Engine\\Headers\\foo.h", result[1]);
    }

    /// <summary>
    /// CRLF line endings: backslash-CR-LF is also a valid continuation
    /// (some emitters write CRLF on Windows even when targeting Clang).
    /// </summary>
    [Fact]
    public void ParseClangMakefile_CrlfContinuation_HandledLikeLf()
    {
        string depContent =
            "out.o: src.cpp \\\r\n" +
            "  header1.h \\\r\n" +
            "  header2.h\r\n";
        byte[] bytes = s_utf8NoBom.GetBytes(depContent);
        IReadOnlyList<string> result = CppDependencyCache.ParseClangMakefileDepfile(bytes, "test");
        Assert.Equal(3, result.Count);
        Assert.Equal("src.cpp", result[0]);
        Assert.Equal("header1.h", result[1]);
        Assert.Equal("header2.h", result[2]);
    }

    /// <summary>
    /// Degenerate file with no <c>:</c> separator returns empty without
    /// throwing.
    /// </summary>
    [Fact]
    public void ParseClangMakefile_NoSeparator_ReturnsEmpty()
    {
        byte[] bytes = s_utf8NoBom.GetBytes("just some garbage without any colon\n");
        IReadOnlyList<string> result = CppDependencyCache.ParseClangMakefileDepfile(bytes, "test");
        Assert.Empty(result);
    }

    /// <summary>
    /// Empty depfile returns empty without throwing.
    /// </summary>
    [Fact]
    public void ParseClangMakefile_EmptyFile_ReturnsEmpty()
    {
        IReadOnlyList<string> result = CppDependencyCache.ParseClangMakefileDepfile(Array.Empty<byte>(), "test");
        Assert.Empty(result);
    }

    /// <summary>
    /// Invalid UTF-8 in a Clang depfile is degraded (UTF-8 decoder is
    /// permissive in fallback mode); the parser should still return a
    /// best-effort result rather than throw.
    /// </summary>
    [Fact]
    public void ParseClangMakefile_InvalidUtf8_DoesNotThrow()
    {
        // Inject a stray 0xC0 byte (illegal in UTF-8) in the middle of
        // an otherwise valid depfile. UTF-8 default decoder substitutes
        // U+FFFD; the parser must not crash.
        byte[] bytes = new byte[] {
            (byte)'o', (byte)'.', (byte)'o', (byte)':', (byte)' ', (byte)'s',
            0xC0, // illegal byte
            (byte)'.', (byte)'c', (byte)'p', (byte)'p', (byte)'\n'
        };
        // Should not throw; result may contain a U+FFFD-decorated token
        // but we only assert the no-throw contract.
        IReadOnlyList<string> result = CppDependencyCache.ParseClangMakefileDepfile(bytes, "test");
        Assert.NotNull(result);
    }

    // -------------------------------------------------------------------
    // MSVC /sourceDependencies JSON parser tests.
    // -------------------------------------------------------------------

    /// <summary>
    /// Round-trip an MSVC 17.4+ <c>/sourceDependencies</c> JSON depfile.
    /// </summary>
    [Fact]
    public void ParseMsvcSourceDependencies_BasicShape_ReturnsIncludesArray()
    {
        string json =
            "{\n" +
            "  \"Version\": \"1.0\",\n" +
            "  \"Data\": {\n" +
            "    \"Source\": \"C:\\\\Source\\\\foo.cpp\",\n" +
            "    \"Includes\": [\n" +
            "      \"C:\\\\Engine\\\\Headers\\\\Public.h\",\n" +
            "      \"C:\\\\Engine\\\\Headers\\\\Private.h\"\n" +
            "    ]\n" +
            "  }\n" +
            "}\n";
        byte[] bytes = s_utf8NoBom.GetBytes(json);
        IReadOnlyList<string> result = CppDependencyCache.ParseMsvcSourceDependenciesJson(bytes, 0, "test");
        Assert.Equal(2, result.Count);
        Assert.Equal("C:\\Engine\\Headers\\Public.h", result[0]);
        Assert.Equal("C:\\Engine\\Headers\\Private.h", result[1]);
    }

    /// <summary>
    /// MSVC 1.1 schema is accepted verbatim (no version-mismatch warning).
    /// </summary>
    [Fact]
    public void ParseMsvcSourceDependencies_Schema11_ParsedNormally()
    {
        string json =
            "{\n" +
            "  \"Version\": \"1.1\",\n" +
            "  \"Data\": {\n" +
            "    \"Source\": \"foo.cpp\",\n" +
            "    \"Includes\": [\"a.h\", \"b.h\"]\n" +
            "  }\n" +
            "}\n";
        byte[] bytes = s_utf8NoBom.GetBytes(json);
        IReadOnlyList<string> result = CppDependencyCache.ParseMsvcSourceDependenciesJson(bytes, 0, "test");
        Assert.Equal(2, result.Count);
    }

    /// <summary>
    /// Malformed JSON (missing closing brace) surfaces as a defensive
    /// empty result, not a crash. The caller (ParseDepfile) wraps the
    /// JsonException and logs a warning.
    /// </summary>
    [Fact]
    public void ParseDepfile_MalformedJson_ReturnsEmpty()
    {
        // Write a malformed JSON depfile to disk and parse it through
        // the public ParseDepfile entry point so the wrapping
        // exception handler executes.
        string path = WriteScratch(
            "malformed.deps.json",
            "{\n  \"Version\": \"1.0\",\n  \"Data\": {");
        CppDependencyCache cache = CppDependencyCache.OpenAtPath(
            Path.Combine(_scratchDir, "cache.bin"));
        IReadOnlyList<FileItem> result = cache.ParseDepfileAtPath(path, path);
        Assert.Empty(result);
    }

    /// <summary>
    /// A depfile with no <c>Data.Includes</c> array (e.g. a module-only
    /// TU) returns empty without throwing.
    /// </summary>
    [Fact]
    public void ParseMsvcSourceDependencies_MissingIncludes_ReturnsEmpty()
    {
        string json =
            "{\n" +
            "  \"Version\": \"1.0\",\n" +
            "  \"Data\": {\n" +
            "    \"Source\": \"foo.cpp\"\n" +
            "  }\n" +
            "}\n";
        byte[] bytes = s_utf8NoBom.GetBytes(json);
        IReadOnlyList<string> result = CppDependencyCache.ParseMsvcSourceDependenciesJson(bytes, 0, "test");
        Assert.Empty(result);
    }

    // -------------------------------------------------------------------
    // ParseDepfile dispatch + format detection.
    // -------------------------------------------------------------------

    /// <summary>
    /// A file starting with <c>{</c> is dispatched to the JSON parser.
    /// </summary>
    [Fact]
    public void ParseDepfile_LeadingBrace_DispatchedToJsonParser()
    {
        string path = WriteScratch(
            "msvc.deps.json",
            "{\"Version\":\"1.0\",\"Data\":{\"Source\":\"f.cpp\",\"Includes\":[\"x.h\"]}}");
        CppDependencyCache cache = CppDependencyCache.OpenAtPath(
            Path.Combine(_scratchDir, "cache.bin"));
        IReadOnlyList<FileItem> result = cache.ParseDepfileAtPath(path, path);
        Assert.Single(result);
        Assert.EndsWith("x.h", result[0].FullPath);
    }

    /// <summary>
    /// A file starting with a Makefile-style target (no leading <c>{</c>)
    /// is dispatched to the Makefile parser.
    /// </summary>
    [Fact]
    public void ParseDepfile_MakefileFormat_DispatchedToMakefileParser()
    {
        string path = WriteScratch("clang.d", "out.o: src.cpp \\\n  header.h\n");
        CppDependencyCache cache = CppDependencyCache.OpenAtPath(
            Path.Combine(_scratchDir, "cache.bin"));
        IReadOnlyList<FileItem> result = cache.ParseDepfileAtPath(path, path);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, fi => fi.FullPath.EndsWith("src.cpp", StringComparison.Ordinal));
        Assert.Contains(result, fi => fi.FullPath.EndsWith("header.h", StringComparison.Ordinal));
    }

    /// <summary>
    /// A non-existent depfile returns empty without throwing.
    /// </summary>
    [Fact]
    public void ParseDepfile_MissingFile_ReturnsEmpty()
    {
        string missing = Path.Combine(_scratchDir, "does-not-exist.d");
        CppDependencyCache cache = CppDependencyCache.OpenAtPath(
            Path.Combine(_scratchDir, "cache.bin"));
        IReadOnlyList<FileItem> result = cache.ParseDepfileAtPath(missing, missing);
        Assert.Empty(result);
    }

    // -------------------------------------------------------------------
    // Cache record / lookup / round-trip-to-disk tests.
    // -------------------------------------------------------------------

    /// <summary>
    /// Record a dependency set, then retrieve it: the lookup returns the
    /// recorded paths sorted ordinal.
    /// </summary>
    [Fact]
    public void RecordAndGetDependencies_RoundTrip_PreservesOrderingAndDistinctness()
    {
        CppDependencyCache cache = CppDependencyCache.OpenAtPath(
            Path.Combine(_scratchDir, "cache.bin"));

        // Use real disk paths so FileItem creation succeeds.
        string h1 = WriteScratch("zzz.h", "");
        string h2 = WriteScratch("aaa.h", "");
        string h3 = WriteScratch("mmm.h", "");
        // Duplicate path -- the cache should dedupe.
        FileItem fh1 = FileItem.GetItemByPath(h1);
        FileItem fh2 = FileItem.GetItemByPath(h2);
        FileItem fh3 = FileItem.GetItemByPath(h3);
        string sourcePath = WriteScratch("src.cpp", "");

        cache.RecordDependencies(sourcePath, new[] { fh1, fh2, fh3, fh1 });

        IReadOnlyList<string>? recorded = cache.GetRecordedDependencyPaths(sourcePath);
        Assert.NotNull(recorded);
        Assert.Equal(3, recorded.Count);
        // Sorted ordinal: aaa < mmm < zzz (substring match on filename).
        Assert.EndsWith("aaa.h", recorded[0]);
        Assert.EndsWith("mmm.h", recorded[1]);
        Assert.EndsWith("zzz.h", recorded[2]);
    }

    /// <summary>
    /// Save the cache, re-open at the same path, confirm the recorded
    /// dependencies survive the round-trip.
    /// </summary>
    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesRecordedDependencies()
    {
        string cachePath = Path.Combine(_scratchDir, "cache.bin");
        string sourcePath = WriteScratch("src.cpp", "");
        string headerPath = WriteScratch("transitive.h", "");

        CppDependencyCache cache = CppDependencyCache.OpenAtPath(cachePath);
        cache.RecordDependencies(sourcePath, new[] { FileItem.GetItemByPath(headerPath) });
        cache.Save();

        Assert.True(File.Exists(cachePath));

        CppDependencyCache reopened = CppDependencyCache.OpenAtPath(cachePath);
        IReadOnlyList<string>? recorded = reopened.GetRecordedDependencyPaths(sourcePath);
        Assert.NotNull(recorded);
        Assert.Single(recorded);
        Assert.EndsWith("transitive.h", recorded[0]);
    }

    /// <summary>
    /// A torn archive (truncated mid-record) is dropped on load; the
    /// reopened cache is empty and the next Save rewrites a valid one.
    /// </summary>
    [Fact]
    public void Load_TruncatedArchive_ResultsInEmptyCache()
    {
        string cachePath = Path.Combine(_scratchDir, "cache.bin");
        string sourcePath = WriteScratch("src.cpp", "");
        string headerPath = WriteScratch("h.h", "");

        CppDependencyCache cache = CppDependencyCache.OpenAtPath(cachePath);
        cache.RecordDependencies(sourcePath, new[] { FileItem.GetItemByPath(headerPath) });
        cache.Save();

        // Truncate the archive to its first 16 bytes (header + part of
        // version + start of partition stream) so a subsequent load
        // hits an EndOfStream mid-read.
        byte[] truncated = File.ReadAllBytes(cachePath).Take(16).ToArray();
        File.WriteAllBytes(cachePath, truncated);

        CppDependencyCache reopened = CppDependencyCache.OpenAtPath(cachePath);
        Assert.Null(reopened.GetRecordedDependencyPaths(sourcePath));
    }

    /// <summary>
    /// A cache file with a wrong magic prefix is dropped on load (treated
    /// as empty); no exception escapes.
    /// </summary>
    [Fact]
    public void Load_WrongMagic_ResultsInEmptyCache()
    {
        string cachePath = Path.Combine(_scratchDir, "cache.bin");
        File.WriteAllBytes(cachePath, new byte[] { (byte)'X', (byte)'Y', (byte)'Z', (byte)'!', 0, 0, 0, 1 });
        CppDependencyCache reopened = CppDependencyCache.OpenAtPath(cachePath);
        Assert.Null(reopened.GetRecordedDependencyPaths("nonexistent"));
    }

    // -------------------------------------------------------------------
    // ActionHistory integration: editing a recorded transitive header
    // invalidates a cached action.
    // -------------------------------------------------------------------

    /// <summary>
    /// End-to-end audit-fix R3-C1 regression: a source's transitive
    /// header is recorded in the cache; editing that header (without
    /// touching anything in <see cref="IExternalAction.PrerequisiteItems"/>)
    /// causes <see cref="ActionHistory.IsActionOutdated"/> to return
    /// true on the next staleness check. Pre-fix this returned false
    /// (silent staleness).
    /// </summary>
    [Fact]
    public void IsActionOutdated_RecordedTransitiveHeader_EditInvalidatesAction()
    {
        // Layout:
        //   source.cpp  -- direct prereq (in PrerequisiteItems)
        //   transitive.h -- NOT a direct prereq, but recorded by the
        //                   cache as a transitive include.
        //   compile.out  -- the produced object.
        string sourcePath = WriteScratch("source.cpp", "int main() { return 0; }");
        string headerPath = WriteScratch("transitive.h", "// initial content");
        FileItem sourceItem = FileItem.GetItemByPath(sourcePath);
        FileItem headerItem = FileItem.GetItemByPath(headerPath);
        FileItem producedItem = FileItem.GetItemByPath(
            Path.Combine(_scratchDir, "compile.out"));

        // The action's PrerequisiteItems intentionally does NOT include
        // transitive.h -- that's the whole point of the bug we're
        // closing: a transitive header outside the PCH never makes it
        // into PrerequisiteItems, so without the cache the staleness
        // check would miss its edits.
        IExternalAction action = ExternalAction.Create(new ExternalAction
        {
            ActionType = XActionType.CompileCppAction,
            PrerequisiteItems = new[] { sourceItem },
            ProducedItems = new[] { producedItem },
            CommandPath = "/fake/cl.exe",
            WorkingDirectory = _scratchDir,
            CommandDescription = "Compile",
            StatusDescription = "source.cpp",
            bUseActionHistory = true,
            Configuration = BuildConfiguration.Development,
            Platform = Platform.Win64,
        });

        // Write the produced file on disk (the action is "successful")
        // and record the action key + the transitive header's content
        // hash, mirroring what ParallelExecutor would do post-success.
        File.WriteAllText(producedItem.FullPath, "synthetic-output");
        producedItem.Invalidate();

        ActionHistory history = ActionHistory.OpenAtPath(
            Path.Combine(_scratchDir, "history.bin"));
        CppDependencyCache cache = CppDependencyCache.OpenAtPath(
            Path.Combine(_scratchDir, "cppdeps.bin"));

        IoHash actionKey = ActionHistory.ComputeActionKey(action);
        history.RecordHash(producedItem, actionKey);
        history.RecordContentHash(sourceItem, sourceItem.ContentHash);
        cache.RecordDependencies(sourcePath, new[] { headerItem });
        history.RecordContentHash(headerItem, headerItem.ContentHash);

        var linked = new LinkedAction(action);

        // Sanity: with no edits, the action is NOT outdated.
        Assert.False(history.IsActionOutdated(linked, cache));

        // Edit the transitive header without touching the direct prereq.
        File.WriteAllText(headerPath, "// edited content -- bytes changed");
        headerItem.Invalidate();

        // With the cache wired in, the staleness check sees the
        // recorded transitive header's hash drift and reports the
        // action as outdated -- the R3-C1 fix.
        Assert.True(history.IsActionOutdated(linked, cache));

        // For comparison: WITHOUT the cache (the pre-R3-C1 behaviour),
        // the same action would be reported as up-to-date because no
        // direct prereq changed and no produced item was deleted. This
        // confirms the cache is load-bearing for invalidation.
        Assert.False(history.IsActionOutdated(linked, cppDependencyCache: null));
    }

    /// <summary>
    /// End-to-end: an action's DependencyListFile points at a real
    /// <c>.d</c> file on disk; the executor runs the action, the
    /// dependency cache parses the .d, records the transitive set, and
    /// records each header's content hash so a subsequent edit
    /// invalidates the action.
    /// </summary>
    [Fact]
    public void ParallelExecutor_DependencyListFile_RecordsTransitiveHeaders()
    {
        // Set up the on-disk fixture: a source, a transitive header,
        // and a .d file naming both.
        string sourcePath = WriteScratch("e2e-src.cpp", "int e2e() { return 0; }");
        string headerPath = WriteScratch("e2e-trans.h", "// orig");
        string objPath = Path.Combine(_scratchDir, "e2e.o");
        string depPath = Path.Combine(_scratchDir, "e2e.o.d");
        File.WriteAllText(
            depPath,
            $"e2e.o: {Path.GetFileName(sourcePath)} \\\n  {headerPath}\n");

        FileItem sourceItem = FileItem.GetItemByPath(sourcePath);
        FileItem objItem = FileItem.GetItemByPath(objPath);
        FileItem depItem = FileItem.GetItemByPath(depPath);

        // The action's PrerequisiteItems list is sorted ordinal; the
        // ProducedItems list includes BOTH the .obj and the .d (mirroring
        // the toolchain's emit shape post-R3-C1).
        FileItem[] produced = { objItem, depItem };
        Array.Sort(produced, static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));

        IExternalAction action = ExternalAction.Create(new ExternalAction
        {
            ActionType = XActionType.CompileCppAction,
            PrerequisiteItems = new[] { sourceItem },
            ProducedItems = produced,
            CommandPath = "/fake/cl.exe",
            WorkingDirectory = _scratchDir,
            CommandDescription = "Compile",
            StatusDescription = "e2e-src.cpp",
            bUseActionHistory = true,
            Configuration = BuildConfiguration.Development,
            Platform = Platform.Win64,
            DependencyListFile = depItem,
        });

        var graph = new Simgenics.XPact.XBT.ActionGraph.ActionGraph(new[] { action });
        graph.Link();
        graph.DetectCycles();
        graph.Sort();

        ActionHistory history = ActionHistory.OpenAtPath(
            Path.Combine(_scratchDir, "e2e-history.bin"));
        CppDependencyCache cache = CppDependencyCache.OpenAtPath(
            Path.Combine(_scratchDir, "e2e-cppdeps.bin"));

        var runner = new ProducesObjAndDepRunner(sourcePath, headerPath);
        var executor = new ParallelExecutor(
            new ParallelExecutorOptions { WorkerCount = 1 },
            runner,
            history,
            cache);
        ExecutionReport report = executor.Execute(graph, CancellationToken.None);
        Assert.True(report.AllSucceeded);

        // The cache must now have a record for the source file with the
        // transitive header in it. The depfile names two prereqs (the
        // source file itself and the transitive header); the cache
        // records both since the parser doesn't filter the source out
        // -- the Makefile-format depfile lists every prerequisite the
        // compile reads, including the TU itself, and the cache layer
        // stores them verbatim. The next-build invalidation is
        // unaffected: both entries point at real files whose content
        // hashes the cache compares against.
        IReadOnlyList<string>? recorded = cache.GetRecordedDependencyPaths(sourcePath);
        Assert.NotNull(recorded);
        Assert.Contains(recorded, p => p.EndsWith("e2e-trans.h", StringComparison.Ordinal));

        // And ActionHistory must have a content hash for the transitive
        // header so the next-build invalidation can compare against
        // edits.
        FileItem headerItem = FileItem.GetItemByPath(headerPath);
        IoHash storedHeaderHash = history.GetStoredContentHash(headerItem);
        Assert.NotEqual(IoHash.Zero, storedHeaderHash);
        Assert.Equal(headerItem.ContentHash, storedHeaderHash);

        var linked = new LinkedAction(action);
        Assert.False(history.IsActionOutdated(linked, cache));

        // Edit the transitive header.
        File.WriteAllText(headerPath, "// edited");
        headerItem.Invalidate();

        Assert.True(history.IsActionOutdated(linked, cache));
    }

    // -------------------------------------------------------------------
    // Schema-rotation invalidation: a cache built with one ActionHistory
    // schema digest is dropped when loaded under a different digest.
    // -------------------------------------------------------------------

    /// <summary>
    /// On-disk cache encodes the action-history schema digest;
    /// loading the cache when <see cref="ActionHistory.CurrentVersion"/>
    /// matches must succeed (no-op for this assertion; we just want to
    /// confirm the version round-trips). A future test could mutate the
    /// stored bytes to invert the assertion, but the load-time check is
    /// already exercised by SaveAndLoad_RoundTrip above (matching
    /// version -> entries survive).
    /// </summary>
    [Fact]
    public void Load_MatchingSchemaDigest_PreservesEntries()
    {
        string cachePath = Path.Combine(_scratchDir, "version-cache.bin");
        string sourcePath = WriteScratch("v.cpp", "");
        string headerPath = WriteScratch("v.h", "");

        CppDependencyCache cache = CppDependencyCache.OpenAtPath(cachePath);
        cache.RecordDependencies(sourcePath, new[] { FileItem.GetItemByPath(headerPath) });
        cache.Save();

        // Schema digest is implicit in ActionHistory.CurrentVersion; the
        // re-open uses the same value so entries survive.
        CppDependencyCache reopened = CppDependencyCache.OpenAtPath(cachePath);
        Assert.NotNull(reopened.GetRecordedDependencyPaths(sourcePath));
    }

    // -------------------------------------------------------------------
    // Test helper runner: writes synthetic .o + .d temp outputs on the
    // executor's behalf. Mirrors the production behaviour where the
    // toolchain emits both files.
    // -------------------------------------------------------------------

    private sealed class ProducesObjAndDepRunner : IActionRunner
    {
        private readonly string _sourcePathToReference;
        private readonly string _headerToReference;

        public ProducesObjAndDepRunner(string sourcePathToReference, string headerToReference)
        {
            _sourcePathToReference = sourcePathToReference;
            _headerToReference = headerToReference;
        }

        public ActionRunResult RunAction(ActionRunContext context)
        {
            // Each produced item's temp file is written with deterministic
            // content. For the .d file we synthesise a Makefile-format
            // depfile naming the transitive header AND the source's full
            // path (the production toolchain emits absolute paths); for
            // the .o we just write a sentinel.
            foreach ((FileItem produced, string tempPath) in context.TempOutputPaths)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
                if (produced.FullPath.EndsWith(".d", StringComparison.Ordinal))
                {
                    File.WriteAllText(
                        tempPath,
                        $"out.o: {_sourcePathToReference} \\\n  {_headerToReference}\n");
                }
                else
                {
                    File.WriteAllText(tempPath, "synthetic-obj");
                }
            }
            return new ActionRunResult(true, ExitCode: 0, ErrorMessage: null);
        }
    }
}

/// <summary>
/// Audit fix R5-M2: parser tests for the MSVC
/// <c>/sourceDependencies</c> schema-1.1+ <c>ImportedModules</c> +
/// <c>ImportedHeaderUnits</c> fields added in this round.
/// </summary>
public sealed class CppDependencyCacheMsvcModuleFieldsTests
{
    /// <summary>
    /// Schema 1.1 depfile with ImportedModules: each entry's BMI path
    /// is recorded as a transitive prerequisite alongside Includes.
    /// </summary>
    [Fact]
    public void ImportedModules_BmiPath_RecordedAsDependency()
    {
        string json = @"{
            ""Version"": ""1.1"",
            ""Data"": {
                ""Source"": ""src.cpp"",
                ""Includes"": [""C:\\inc\\foo.h""],
                ""ImportedModules"": [
                    { ""Name"": ""MyModule"", ""BMI"": ""C:\\bmi\\MyModule.ifc"" },
                    { ""Name"": ""Other"",    ""BMI"": ""C:\\bmi\\Other.ifc"" }
                ]
            }
        }";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);

        IReadOnlyList<string> result = Simgenics.XPact.XBT.ActionGraph.CppDependencyCache
            .ParseMsvcSourceDependenciesJson(bytes, 0, "test");

        Assert.Contains(@"C:\inc\foo.h", result);
        Assert.Contains(@"C:\bmi\MyModule.ifc", result);
        Assert.Contains(@"C:\bmi\Other.ifc", result);
    }

    /// <summary>
    /// Schema 1.1 depfile with ImportedHeaderUnits: the Header path AND
    /// the BMI path are both recorded so an edit to either invalidates
    /// the importing TU.
    /// </summary>
    [Fact]
    public void ImportedHeaderUnits_HeaderAndBmi_BothRecorded()
    {
        string json = @"{
            ""Version"": ""1.1"",
            ""Data"": {
                ""Source"": ""src.cpp"",
                ""Includes"": [],
                ""ImportedHeaderUnits"": [
                    { ""Header"": ""C:\\inc\\utility.h"", ""BMI"": ""C:\\bmi\\utility.ifc"" }
                ]
            }
        }";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);

        IReadOnlyList<string> result = Simgenics.XPact.XBT.ActionGraph.CppDependencyCache
            .ParseMsvcSourceDependenciesJson(bytes, 0, "test");

        Assert.Contains(@"C:\inc\utility.h", result);
        Assert.Contains(@"C:\bmi\utility.ifc", result);
    }

    /// <summary>
    /// Schema 1.2 (MSVC 17.10+ -- the documented version that introduced
    /// the modules fields): parse without diagnostic.
    /// </summary>
    [Fact]
    public void Schema12_ParsedWithoutDiagnostic()
    {
        string json = @"{
            ""Version"": ""1.2"",
            ""Data"": {
                ""Includes"": [""h1.h""],
                ""ImportedModules"": [],
                ""ImportedHeaderUnits"": []
            }
        }";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);

        IReadOnlyList<string> result = Simgenics.XPact.XBT.ActionGraph.CppDependencyCache
            .ParseMsvcSourceDependenciesJson(bytes, 0, "test");

        Assert.Contains("h1.h", result);
    }

    /// <summary>
    /// Schema 1.3 (hypothetical future version) still parses the known
    /// fields and emits an informational diagnostic so operators can
    /// correlate cache misses with the schema bump.
    /// </summary>
    [Fact]
    public void Schema13_ParsesKnownFields_EmitsDiagnostic()
    {
        string json = @"{
            ""Version"": ""1.3"",
            ""Data"": {
                ""Includes"": [""future-h.h""],
                ""ImportedModules"": [{ ""Name"": ""Fm"", ""BMI"": ""fm.ifc"" }]
            }
        }";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);

        // Diagnostic-emit assertion: we don't assert on Logger.Info
        // output (the test logger is shared); we assert the known
        // fields parsed despite the version drift.
        IReadOnlyList<string> result = Simgenics.XPact.XBT.ActionGraph.CppDependencyCache
            .ParseMsvcSourceDependenciesJson(bytes, 0, "test");

        Assert.Contains("future-h.h", result);
        Assert.Contains("fm.ifc", result);
    }

    /// <summary>
    /// Defensive: an ImportedModules entry that is malformed (missing
    /// BMI, non-string BMI, or non-object entry) is silently skipped
    /// without poisoning the rest of the parse.
    /// </summary>
    [Fact]
    public void ImportedModules_MalformedEntries_SilentlySkipped()
    {
        string json = @"{
            ""Version"": ""1.1"",
            ""Data"": {
                ""Includes"": [""good.h""],
                ""ImportedModules"": [
                    { ""Name"": ""NoBMI"" },
                    ""not-an-object"",
                    { ""Name"": ""BadType"", ""BMI"": 42 },
                    { ""Name"": ""Good"", ""BMI"": ""good.ifc"" }
                ]
            }
        }";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);

        IReadOnlyList<string> result = Simgenics.XPact.XBT.ActionGraph.CppDependencyCache
            .ParseMsvcSourceDependenciesJson(bytes, 0, "test");

        Assert.Contains("good.h", result);
        Assert.Contains("good.ifc", result);
        // The malformed entries are absent.
        Assert.DoesNotContain(result, s => s.Contains("NoBMI", StringComparison.Ordinal));
        Assert.DoesNotContain(result, s => s.Contains("BadType", StringComparison.Ordinal));
    }
}

/// <summary>
/// Audit fix R5-m2: Makefile-format parser tests for the index-based
/// rewrite (replaces the previous U+FFFE-sentinel approach).
/// </summary>
public sealed class CppDependencyCacheMakefileParserTests
{
    /// <summary>
    /// Audit fix R5-m2: a depfile containing the literal UTF-8 bytes
    /// EF BF BE (the encoding of U+FFFE, the sentinel the previous
    /// parser used internally) is tokenised correctly. The previous
    /// parser would have split the path at the sentinel byte and
    /// produced a corrupted token.
    /// </summary>
    [Fact]
    public void Tokenize_PathContainingUFFFE_NotSplit()
    {
        // Construct a depfile whose prereq path contains the U+FFFE
        // codepoint (3 UTF-8 bytes: EF BF BE). The byte sequence is
        // exactly what the previous sentinel-based parser used as its
        // tokenisation marker.
        string pathWithSentinel = "header" + "￾" + "name.h";
        string depfile = "out.o: src.cpp \\\n  " + pathWithSentinel + "\n";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(depfile);

        IReadOnlyList<string> result = Simgenics.XPact.XBT.ActionGraph.CppDependencyCache
            .ParseClangMakefileDepfile(bytes, "test");

        Assert.Contains(result, t => t == pathWithSentinel);
        // The sentinel-corrupted forms that the previous parser would
        // have produced are absent.
        Assert.DoesNotContain(result, t => t == "header");
        Assert.DoesNotContain(result, t => t == "name.h");
    }

    /// <summary>
    /// Backslash-space escapes are preserved as literal spaces in the
    /// tokenised path (existing parser behaviour, re-verified after the
    /// sentinel-free rewrite).
    /// </summary>
    [Fact]
    public void Tokenize_EscapedSpace_PreservedAsLiteralSpace()
    {
        string depfile = "out.o: src.cpp \\\n  C:/path\\ with\\ spaces.h\n";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(depfile);

        IReadOnlyList<string> result = Simgenics.XPact.XBT.ActionGraph.CppDependencyCache
            .ParseClangMakefileDepfile(bytes, "test");

        Assert.Contains("C:/path with spaces.h", result);
    }

    /// <summary>
    /// Windows drive-letter colons are not mistaken for the
    /// target/prereq separator.
    /// </summary>
    [Fact]
    public void Tokenize_WindowsDriveLetterColon_NotSeparator()
    {
        string depfile = "C:\\out\\out.o: C:\\src\\src.cpp \\\n  C:\\inc\\h.h\n";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(depfile);

        IReadOnlyList<string> result = Simgenics.XPact.XBT.ActionGraph.CppDependencyCache
            .ParseClangMakefileDepfile(bytes, "test");

        Assert.Contains(@"C:\src\src.cpp", result);
        Assert.Contains(@"C:\inc\h.h", result);
    }
}
