// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Blake3;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.ActionGraph;

/// <summary>
/// Per-source transitive-header dependency map. Mirrors UBT's
/// <c>CppDependencyCache</c> (per <c>Engine/Source/Programs/UnrealBuildTool/System/CppDependencyCache.cs</c>)
/// in intent (post-compile depfile parse + recorded-header invalidation
/// on the next build) but with the Round-3 audit fixes applied --
/// notably the Round-3 closure of the silent-staleness bug where editing
/// a transitively-included header that was not in the PCH did NOT
/// invalidate the cached <c>.obj</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this cache exists.</b> The action-graph's
/// <see cref="IExternalAction.PrerequisiteItems"/> is fixed at action-
/// emission time and only contains the source file (+ PCH artefacts if
/// bound). Transitively-included headers not in the PCH are absent.
/// <see cref="ActionHistory.IsActionOutdated"/> consults only
/// PrerequisiteItems' content-hashes, so editing a transitively-included
/// header not in the PCH would NOT invalidate the cached <c>.obj</c> --
/// silent staleness. Toolchains emit a depfile alongside the <c>.obj</c>
/// (Clang's <c>-MD -MF</c>; MSVC's <c>/sourceDependencies</c>); the
/// post-compile parse here discovers the transitive header set, records
/// each header's content hash, and turns the next build's staleness check
/// into a complete invalidation signal.
/// </para>
/// <para>
/// <b>Storage layout.</b> Partitioned binary archive mirroring
/// <see cref="ActionHistory"/>: 256 partitions keyed on BLAKE3 byte-0 of
/// the source's full path; per-partition lock so concurrent post-compile
/// records do not serialize cross-partition. Stored at
/// <c>&lt;targetDir&gt;/&lt;config&gt;/CppDependencyCache.bin</c>; atomic
/// rename via <see cref="FileSystemOps.RetryOnTransientIOException"/>;
/// fsync before rename so a power loss after rename cannot leave a torn
/// archive.
/// </para>
/// <para>
/// <b>Parsers.</b> Two depfile formats are supported:
/// </para>
/// <list type="bullet">
///   <item>Makefile-format <c>.d</c> (Clang <c>-MD -MF</c>). One target,
///   one-or-more prereq lines joined by <c>\&lt;LF&gt;</c>; escaped
///   spaces (<c>\&lt;space&gt;</c>) preserved; drive-letter colons not
///   split.</item>
///   <item>MSVC <c>/sourceDependencies</c> JSON (MSVC 17.4+). Version
///   field 1.0 / 1.1 / 1.2 accepted; the <c>Includes</c> array is the
///   transitive header set.</item>
/// </list>
/// <para>
/// <b>Invalidation flow.</b> After a successful action,
/// <see cref="ParallelExecutor"/> calls <see cref="ParseDepfile"/> on
/// <see cref="IExternalAction.DependencyListFile"/> (when non-null), then
/// <see cref="RecordDependencies"/> for the action's source-output pair.
/// In parallel it calls <see cref="ActionHistory.RecordContentHash"/> for
/// every discovered header so the next build's
/// <see cref="ActionHistory.IsActionOutdated"/> can compare the live
/// header content against the recorded hash and force a rebuild on edit.
/// </para>
/// </remarks>
public sealed class CppDependencyCache
{
    /// <summary>
    /// Partition count -- matches <see cref="ActionHistory.PartitionCount"/>
    /// so paths route to the same partition index across both caches.
    /// </summary>
    public const int PartitionCount = 256;

    /// <summary>Magic prefix on the file header for format identification.</summary>
    private static readonly byte[] s_magic = Encoding.ASCII.GetBytes("XCDC");

    /// <summary>
    /// File format version. v1 = initial layout: source-key map with each
    /// entry holding the BLAKE3 of the depfile + a length-prefixed list
    /// of recorded header full paths.
    /// </summary>
    private const int FormatVersion = 1;

    /// <summary>
    /// Maximum JSON depth for the MSVC <c>/sourceDependencies</c> parser.
    /// Mirrors the manifest-reader hardening: 64 is well above the actual
    /// schema (depth 4) and bounds adversarial nesting.
    /// </summary>
    private const int MaxJsonDepth = 64;

    /// <summary>
    /// Maximum size in bytes XBT will read from a depfile. Mirrors the
    /// manifest-reader 100 MB ceiling. A depfile larger than this is
    /// almost certainly corrupt or adversarial; we surface a defensive
    /// diagnostic and treat the parse as empty.
    /// </summary>
    private const int MaxDepfileBytes = 100 * 1024 * 1024;

    /// <summary>
    /// Maximum path length for a single recorded header. 4096 mirrors
    /// the <see cref="ActionHistory.LoadFromDisk"/> path-length cap.
    /// </summary>
    private const int MaxPathLength = 4096;

    private readonly string _archivePath;
    private readonly Partition[] _partitions = new Partition[PartitionCount];

    /// <summary>
    /// Open the cache for the given target/configuration. The archive
    /// lives at <c>&lt;targetDir&gt;/&lt;config&gt;/CppDependencyCache.bin</c>;
    /// if the file does not exist a fresh empty cache is returned.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="targetDir"/> is null or empty.</exception>
    public static CppDependencyCache Open(string targetDir, BuildConfiguration config)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetDir);

        Directory.CreateDirectory(targetDir);
        string configDir = Path.Combine(targetDir, config.ToString());
        Directory.CreateDirectory(configDir);

        string path = Path.Combine(configDir, "CppDependencyCache.bin");
        CppDependencyCache cache = new(path);
        cache.LoadFromDisk();
        return cache;
    }

    /// <summary>
    /// Test hook: open a cache archive at an arbitrary path without
    /// imposing the <c>&lt;targetDir&gt;/&lt;config&gt;/</c> layout.
    /// </summary>
    internal static CppDependencyCache OpenAtPath(string archivePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);

        CppDependencyCache cache = new(archivePath);
        cache.LoadFromDisk();
        return cache;
    }

    private CppDependencyCache(string archivePath)
    {
        _archivePath = archivePath;
        for (int i = 0; i < PartitionCount; i++)
        {
            _partitions[i] = new Partition();
        }
    }

    /// <summary>
    /// Parse a depfile and return the transitive header set as
    /// <see cref="FileItem"/>s. Returns an empty list when the file does
    /// not exist, is empty, or is malformed -- the caller treats an empty
    /// parse as "no transitive headers were declared" rather than as a
    /// hard failure (a stale depfile from a crashed compile must not
    /// fail the next build's startup).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Format detection: the first non-whitespace byte decides. <c>{</c>
    /// is the MSVC JSON parser; anything else is the Makefile parser.
    /// Both parsers are defensive against malformed input -- a corrupt
    /// depfile produces an empty result + a one-time diagnostic, never an
    /// unhandled exception.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="depfileItem"/> is null.</exception>
    public IReadOnlyList<FileItem> ParseDepfile(FileItem depfileItem)
    {
        ArgumentNullException.ThrowIfNull(depfileItem);
        return ParseDepfileAtPath(depfileItem.FullPath, depfileItem.FullPath);
    }

    /// <summary>
    /// Test-friendly overload that takes a raw path. Production callers
    /// should go through the <see cref="FileItem"/>-typed overload so
    /// the FileItem identity is preserved.
    /// </summary>
    internal IReadOnlyList<FileItem> ParseDepfileAtPath(string depfilePath, string diagnosticLabel)
    {
        ArgumentException.ThrowIfNullOrEmpty(depfilePath);

        if (!File.Exists(depfilePath))
        {
            return Array.Empty<FileItem>();
        }

        byte[] bytes;
        try
        {
            FileInfo info = new(depfilePath);
            if (info.Length > MaxDepfileBytes)
            {
                Logger.Warning(
                    $"CppDependencyCache: depfile '{diagnosticLabel}' exceeds {MaxDepfileBytes / (1024 * 1024)} MiB ({info.Length} bytes); " +
                    "treating as empty. The compile's transitive header set will not be recorded for next-build invalidation.");
                return Array.Empty<FileItem>();
            }
            bytes = FileSystemOps.RetryOnTransientIOException(() => File.ReadAllBytes(depfilePath));
        }
        catch (IOException ex)
        {
            Logger.Warning(
                $"CppDependencyCache: cannot read depfile '{diagnosticLabel}': {ex.GetType().Name}: {ex.Message}. " +
                "The compile's transitive header set will not be recorded for next-build invalidation.");
            return Array.Empty<FileItem>();
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.Warning(
                $"CppDependencyCache: cannot read depfile '{diagnosticLabel}': {ex.GetType().Name}: {ex.Message}. " +
                "The compile's transitive header set will not be recorded for next-build invalidation.");
            return Array.Empty<FileItem>();
        }

        if (bytes.Length == 0)
        {
            return Array.Empty<FileItem>();
        }

        // Skip whitespace bytes (Makefile-format depfiles often start with
        // the target name, but a UTF-8 BOM or leading whitespace is also
        // possible from some emitters). The first non-whitespace byte
        // decides between the two formats.
        int firstNonWs = SkipLeadingWhitespaceAndBom(bytes);
        if (firstNonWs >= bytes.Length)
        {
            return Array.Empty<FileItem>();
        }
        bool isJson = bytes[firstNonWs] == (byte)'{';

        IReadOnlyList<string> rawPaths;
        try
        {
            rawPaths = isJson
                ? ParseMsvcSourceDependenciesJson(bytes, firstNonWs, diagnosticLabel)
                : ParseClangMakefileDepfile(bytes, diagnosticLabel);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            Logger.Warning(
                $"CppDependencyCache: failed to parse depfile '{diagnosticLabel}': {ex.GetType().Name}: {ex.Message}. " +
                "The compile's transitive header set will not be recorded for next-build invalidation.");
            return Array.Empty<FileItem>();
        }

        if (rawPaths.Count == 0)
        {
            return Array.Empty<FileItem>();
        }

        List<FileItem> items = new(rawPaths.Count);
        foreach (string raw in rawPaths)
        {
            if (string.IsNullOrEmpty(raw))
            {
                continue;
            }
            if (raw.Length > MaxPathLength)
            {
                // Adversarial / corrupt path. Skip the entry but keep
                // parsing the rest of the file; a single bad line should
                // not poison the entire depfile.
                continue;
            }
            string absolute;
            try
            {
                absolute = Path.IsPathRooted(raw)
                    ? Path.GetFullPath(raw)
                    : Path.GetFullPath(raw, Path.GetDirectoryName(depfilePath) ?? Environment.CurrentDirectory);
            }
            catch (ArgumentException)
            {
                // Path containing invalid chars -- skip silently. The
                // depfile's emitter is the toolchain; an invalid char
                // here is a compiler bug, not user input.
                continue;
            }
            catch (PathTooLongException)
            {
                continue;
            }
            items.Add(FileItem.GetItemByPath(absolute));
        }
        return items;
    }

    private static int SkipLeadingWhitespaceAndBom(ReadOnlySpan<byte> bytes)
    {
        int i = 0;
        // UTF-8 BOM (EF BB BF).
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            i = 3;
        }
        while (i < bytes.Length)
        {
            byte b = bytes[i];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                i++;
                continue;
            }
            break;
        }
        return i;
    }

    /// <summary>
    /// Parse a Makefile-format depfile (Clang's <c>-MD -MF</c> output).
    /// Format:
    /// <code>
    /// output.o: source.cpp \
    ///   header1.h \
    ///   header2.h
    /// </code>
    /// Returns the prereq list (everything after the first <c>:</c>);
    /// the target token itself is discarded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Audit fix R5-m2.</b> The previous parser used a U+FFFE Unicode
    /// non-character as a sentinel between an escaped-space marker and
    /// the post-tokenisation reverse-replace step. That technique
    /// breaks if any legitimate depfile byte sequence happens to encode
    /// U+FFFE in UTF-8 (bytes <c>0xEF 0xBF 0xBE</c>) -- the parser
    /// would split the token at the sentinel and produce a corrupt
    /// path. Adversarial filesystem entries are the textbook trigger,
    /// but a path containing a literal U+FFFE byte sequence in a
    /// Unicode-aware codebase is sufficient. The parser is now
    /// index-based: it tokenises directly off the byte-decoded char
    /// stream without round-tripping through a sentinel substitution,
    /// modelled on UBT's <c>TryReadMakefileToken</c> approach
    /// (Engine/Source/Programs/UnrealBuildTool/System/CppDependencyCache.cs).
    /// </para>
    /// <para>
    /// Escaping rules: a backslash at end-of-line continues the line
    /// onto the next; a backslash followed by a space is a literal space
    /// inside a path; <c>\\</c> is a literal backslash; everything else
    /// is treated as a literal backslash followed by the next char. The
    /// drive-letter colon on Windows (<c>C:\foo\bar.h</c>) is NOT
    /// treated as the target/prereq separator -- only the first
    /// non-escaped <c>:</c> followed by whitespace (or end-of-line) is
    /// the separator, mirroring GNU make's behaviour.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> ParseClangMakefileDepfile(byte[] bytes, string diagnosticLabel)
    {
        // Decode as UTF-8. Clang and GCC emit ASCII paths verbatim; UTF-8
        // is a strict superset and the cheapest decode.
        string text;
        try
        {
            text = Encoding.UTF8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Logger.Warning(
                $"CppDependencyCache: Makefile-format depfile '{diagnosticLabel}' contains invalid UTF-8; treating as empty.");
            return Array.Empty<string>();
        }

        // Step 1: locate the target/prereq boundary. The first ':'
        // followed by whitespace / EOL is the separator. We scan the
        // raw text but honour escape semantics so a backslash-escaped
        // colon (rare but legal in GNU make) is not treated as the
        // separator.
        int colon = FindTargetSeparator(text);
        if (colon < 0)
        {
            return Array.Empty<string>();
        }

        // Step 2: tokenise the prereq region with an index-based
        // state machine that consumes one logical character per loop.
        // The state machine respects:
        //   - whitespace separates tokens
        //   - '\\' + LF (or '\\' + CR + LF) is a line continuation (whitespace)
        //   - '\\' + space is a literal space inside the current token
        //   - '\\' + '\\' is a literal backslash inside the current token
        //   - any other '\\' + char emits both the backslash and the char
        //     literally (matches GNU make's behaviour for unrecognised
        //     escapes; the only escape sequences the Makefile depfile
        //     emitter is documented to produce are the three above).
        List<string> tokens = new();
        StringBuilder current = new();
        bool inToken = false;
        int n = text.Length;
        for (int i = colon + 1; i < n; i++)
        {
            char c = text[i];

            if (c == '\\' && i + 1 < n)
            {
                char next = text[i + 1];
                if (next == '\n')
                {
                    // Line continuation: flush the current token and
                    // treat as whitespace.
                    if (inToken)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                        inToken = false;
                    }
                    i++;
                    continue;
                }
                if (next == '\r' && i + 2 < n && text[i + 2] == '\n')
                {
                    if (inToken)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                        inToken = false;
                    }
                    i += 2;
                    continue;
                }
                if (next == ' ')
                {
                    // Escaped space -- part of the current path token.
                    current.Append(' ');
                    inToken = true;
                    i++;
                    continue;
                }
                if (next == '\\')
                {
                    current.Append('\\');
                    inToken = true;
                    i++;
                    continue;
                }
                // Unrecognised escape: emit the backslash literally and
                // let the next-iteration handler process the next char.
                current.Append('\\');
                inToken = true;
                continue;
            }

            if (c is ' ' or '\t' or '\r' or '\n')
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
                continue;
            }

            // Literal character.
            current.Append(c);
            inToken = true;
        }
        if (inToken)
        {
            tokens.Add(current.ToString());
        }
        return tokens;
    }

    /// <summary>
    /// Locate the target/prereq <c>:</c> separator in a Makefile-format
    /// depfile. The separator is the first <c>:</c> whose right-hand
    /// neighbour is either whitespace, end-of-string, or end-of-line --
    /// this excludes Windows drive-letter colons (<c>C:\path</c>) where
    /// the right-hand char is <c>\</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Audit fix R5-m2: scans the raw input text directly rather than a
    /// pre-flattened buffer with sentinel substitutions, so the parser
    /// never round-trips through a U+FFFE marker. Backslash-newline
    /// line continuations are not unwrapped here because the target is
    /// a single line in every Makefile-format depfile emitted by GCC /
    /// Clang -- the multi-line layout only appears in the prereq
    /// region, which the caller tokenises with its own line-continuation
    /// handling.
    /// </para>
    /// </remarks>
    private static int FindTargetSeparator(string text)
    {
        int n = text.Length;
        for (int i = 0; i < n; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < n)
            {
                // Skip an escaped colon, escaped space, or any other
                // two-char escape; the escape's second char cannot
                // form the target/prereq separator.
                i++;
                continue;
            }
            if (c != ':')
            {
                continue;
            }
            if (i + 1 >= n)
            {
                return i;
            }
            char next = text[i + 1];
            if (next is ' ' or '\t' or '\r' or '\n')
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Parse an MSVC <c>/sourceDependencies</c> JSON depfile. Returns
    /// the union of <c>Data.Includes</c>, <c>Data.ImportedModules</c>
    /// (each entry's <c>BMI</c> path), and <c>Data.ImportedHeaderUnits</c>
    /// (each entry's <c>Header</c> path; falls back to <c>BMI</c> when
    /// the header path is absent). The <c>Source</c> field is captured
    /// separately by the caller via the action's source path; the cache
    /// keys recorded dependencies on the source path, not on the
    /// depfile's <c>Source</c> field.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Schema (MSVC 17.4+):
    /// </para>
    /// <code>
    /// {
    ///   "Version": "1.0",
    ///   "Data": {
    ///     "Source": "path/to/source.cpp",
    ///     "ProvidedModule": "...",
    ///     "Includes": ["path/to/header1.h", "path/to/header2.h", ...],
    ///     "ImportedModules": [ { "Name": "MyModule", "BMI": "...ifc" }, ... ],
    ///     "ImportedHeaderUnits": [ { "Header": "path/to/header.h", "BMI": "...ifc" }, ... ]
    ///   }
    /// }
    /// </code>
    /// <para>
    /// <b>Audit fix R5-M2.</b> Round 4 audit surfaced that the parser
    /// previously read <c>Includes</c> only and silently dropped
    /// <c>ImportedModules</c> + <c>ImportedHeaderUnits</c>. Editing a
    /// header that participates in a C++20 module (header unit) or
    /// regenerating a BMI used by an importing TU would NOT invalidate
    /// the cached .obj, producing silent staleness as soon as XPact code
    /// adopts C++20 modules. The parser now treats the BMI path (for
    /// imported modules) and the header-unit's <c>Header</c> path (with
    /// BMI fallback) as additional transitive prerequisites recorded
    /// alongside <c>Includes</c>.
    /// </para>
    /// <para>
    /// Version 1.0 / 1.1 / 1.2 are all accepted; 1.3+ emits a diagnostic
    /// because new schema fields may exist that the parser does not
    /// know about. The Includes / ImportedModules / ImportedHeaderUnits
    /// fields have been stable across the 1.x line.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> ParseMsvcSourceDependenciesJson(byte[] bytes, int startIndex, string diagnosticLabel)
    {
        ReadOnlySpan<byte> span = bytes.AsSpan(startIndex);
        using JsonDocument doc = JsonDocument.Parse(span.ToArray(), new JsonDocumentOptions
        {
            MaxDepth = MaxJsonDepth,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });

        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<string>();
        }

        // Version check -- accept 1.0 / 1.1 / 1.2 silently; emit a
        // diagnostic for 1.3+ so an operator can correlate a missing dep
        // with a schema change. (The 1.2 line introduces the
        // ImportedModules / ImportedHeaderUnits arrays the parser now
        // handles; treating it as silently-accepted matches the
        // documented schema-stability range.)
        if (root.TryGetProperty("Version", out JsonElement versionElement)
            && versionElement.ValueKind == JsonValueKind.String)
        {
            string? versionStr = versionElement.GetString();
            if (!string.IsNullOrEmpty(versionStr)
                && !versionStr.StartsWith("1.0", StringComparison.Ordinal)
                && !versionStr.StartsWith("1.1", StringComparison.Ordinal)
                && !versionStr.StartsWith("1.2", StringComparison.Ordinal))
            {
                Logger.Info(
                    $"CppDependencyCache: MSVC /sourceDependencies depfile '{diagnosticLabel}' " +
                    $"declares schema version '{versionStr}' (XBT supports 1.0 / 1.1 / 1.2). " +
                    "Parsing Includes, ImportedModules.BMI, and ImportedHeaderUnits.Header anyway; " +
                    "any newly-added dependency-source field may not be recorded for invalidation. " +
                    "File an XBT issue if cache misses correlate with this.");
            }
        }

        if (!root.TryGetProperty("Data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<string>();
        }

        List<string> result = new();

        // Includes: array of string paths.
        if (data.TryGetProperty("Includes", out JsonElement includes)
            && includes.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in includes.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                string? raw = entry.GetString();
                if (!string.IsNullOrEmpty(raw))
                {
                    result.Add(raw);
                }
            }
        }

        // Audit fix R5-M2: ImportedModules is an array of objects, each
        // with a {Name, BMI} pair. The BMI (a .ifc on Windows) is the
        // build artefact whose content drives the importing TU's
        // staleness -- recording it as a transitive prerequisite lets
        // the next-build content-hash check detect a regenerated BMI.
        if (data.TryGetProperty("ImportedModules", out JsonElement importedModules)
            && importedModules.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in importedModules.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                if (entry.TryGetProperty("BMI", out JsonElement bmi)
                    && bmi.ValueKind == JsonValueKind.String)
                {
                    string? raw = bmi.GetString();
                    if (!string.IsNullOrEmpty(raw))
                    {
                        result.Add(raw);
                    }
                }
            }
        }

        // Audit fix R5-M2: ImportedHeaderUnits entries carry a Header
        // path (the .h that was imported as a header unit) AND a BMI
        // path (the precompiled-header-unit artefact). Either edit must
        // invalidate the importer; record both. The Header path is the
        // primary signal (an edit to the underlying .h is what user-
        // facing changes do); BMI fallback covers the rare case where
        // an emitter omits Header.
        if (data.TryGetProperty("ImportedHeaderUnits", out JsonElement headerUnits)
            && headerUnits.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in headerUnits.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                bool addedHeader = false;
                if (entry.TryGetProperty("Header", out JsonElement header)
                    && header.ValueKind == JsonValueKind.String)
                {
                    string? raw = header.GetString();
                    if (!string.IsNullOrEmpty(raw))
                    {
                        result.Add(raw);
                        addedHeader = true;
                    }
                }
                if (entry.TryGetProperty("BMI", out JsonElement bmi)
                    && bmi.ValueKind == JsonValueKind.String)
                {
                    string? raw = bmi.GetString();
                    if (!string.IsNullOrEmpty(raw))
                    {
                        result.Add(raw);
                    }
                }
                _ = addedHeader; // documented for readers; behaviour: both fields contribute when present.
            }
        }

        return result;
    }

    /// <summary>
    /// Record the discovered header set against a source-output pair.
    /// Subsequent <see cref="GetRecordedDependencies"/> calls on the same
    /// source return the recorded set; the next-build staleness check
    /// uses the set to consult <see cref="ActionHistory.GetStoredContentHash"/>
    /// for each header.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="sourceFullPath"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="discoveredHeaders"/> is null.</exception>
    public void RecordDependencies(string sourceFullPath, IReadOnlyList<FileItem> discoveredHeaders)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceFullPath);
        ArgumentNullException.ThrowIfNull(discoveredHeaders);

        // Canonical-form the input headers: distinct + sorted ordinal so
        // the partition's recorded value is deterministic regardless of
        // depfile emit order (Clang emits headers in include-encounter
        // order; MSVC emits them in lexicographic order; the cache key
        // must produce the same recorded set for byte-identical builds).
        string[] uniqueSorted = discoveredHeaders
            .Where(h => h is not null)
            .Select(h => h.FullPath)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        Partition partition = _partitions[PartitionOf(sourceFullPath)];
        partition.Set(sourceFullPath, uniqueSorted);
    }

    /// <summary>
    /// Return the previously-recorded dependency set for a source, or
    /// null when no entry exists. The returned list is sorted by full
    /// path with <see cref="StringComparer.Ordinal"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="sourceFullPath"/> is null or empty.</exception>
    public IReadOnlyList<FileItem>? GetRecordedDependencies(string sourceFullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceFullPath);
        Partition partition = _partitions[PartitionOf(sourceFullPath)];
        if (!partition.TryGet(sourceFullPath, out string[]? rawPaths) || rawPaths is null)
        {
            return null;
        }
        FileItem[] result = new FileItem[rawPaths.Length];
        for (int i = 0; i < rawPaths.Length; i++)
        {
            result[i] = FileItem.GetItemByPath(rawPaths[i]);
        }
        return result;
    }

    /// <summary>
    /// Test-only accessor: return the raw recorded path strings without
    /// materialising <see cref="FileItem"/>s. Used to assert on-disk
    /// round-trip without dragging the FileItem cache into the test.
    /// </summary>
    internal IReadOnlyList<string>? GetRecordedDependencyPaths(string sourceFullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceFullPath);
        Partition partition = _partitions[PartitionOf(sourceFullPath)];
        if (!partition.TryGet(sourceFullPath, out string[]? rawPaths) || rawPaths is null)
        {
            return null;
        }
        return rawPaths;
    }

    // Audit fix R5-M4: the previous public Load() surface allowed a
    // caller to re-enter LoadFromDisk on an already-populated cache.
    // The mid-load return paths leave staged partitions empty (they
    // live in a local), but the previously-loaded partitions remain
    // populated, producing a half-old-half-new view. The public API
    // is now removed entirely: callers must go through Open() /
    // OpenAtPath() which construct a fresh cache + load exactly once.
    // No production code calls Load() post-construction (verified via
    // repo-wide search), so making this private is a net simplification
    // rather than a breaking change.

    private void LoadFromDisk()
    {
        if (!File.Exists(_archivePath))
        {
            return;
        }

        Dictionary<string, string[]>[]? staged = null;
        try
        {
            using FileStream stream = FileSystemOps.RetryOnTransientIOException(
                () => new FileStream(_archivePath, FileMode.Open, FileAccess.Read, FileShare.Read));
            using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);

            byte[] magic = reader.ReadBytes(4);
            if (magic.Length != 4 || !magic.AsSpan().SequenceEqual(s_magic))
            {
                return;
            }

            int format = reader.ReadInt32();
            if (format != FormatVersion)
            {
                return;
            }

            // Action-history-aligned schema digest: when IExternalAction
            // (which drives ActionHistory.CurrentVersion) rotates, the
            // dependency cache also invalidates so the two caches stay
            // in lockstep.
            byte[] versionBytes = reader.ReadBytes(IoHash.Length);
            if (versionBytes.Length != IoHash.Length)
            {
                return;
            }
            IoHash storedVersion = new(versionBytes);
            if (storedVersion != ActionHistory.CurrentVersion)
            {
                return;
            }

            int partitionCount = reader.ReadInt32();
            if (partitionCount != PartitionCount)
            {
                return;
            }

            staged = new Dictionary<string, string[]>[partitionCount];
            for (int i = 0; i < partitionCount; i++)
            {
                staged[i] = new Dictionary<string, string[]>(StringComparer.Ordinal);
                int entryCount = reader.ReadInt32();
                if (entryCount < 0)
                {
                    return;
                }
                for (int j = 0; j < entryCount; j++)
                {
                    int sourcePathLen = reader.ReadInt32();
                    if (sourcePathLen < 0 || sourcePathLen > MaxPathLength)
                    {
                        return;
                    }
                    byte[] sourcePathBytes = reader.ReadBytes(sourcePathLen);
                    if (sourcePathBytes.Length != sourcePathLen)
                    {
                        return;
                    }
                    string sourcePath = Encoding.UTF8.GetString(sourcePathBytes);

                    int headerCount = reader.ReadInt32();
                    if (headerCount < 0 || headerCount > 1_000_000)
                    {
                        // Adversarial entry: a >1M-header depfile cannot
                        // be legitimate (UE's largest TU touches ~3k
                        // headers transitively). Abort the load.
                        return;
                    }
                    string[] headers = new string[headerCount];
                    for (int k = 0; k < headerCount; k++)
                    {
                        int headerLen = reader.ReadInt32();
                        if (headerLen < 0 || headerLen > MaxPathLength)
                        {
                            return;
                        }
                        byte[] headerBytes = reader.ReadBytes(headerLen);
                        if (headerBytes.Length != headerLen)
                        {
                            return;
                        }
                        headers[k] = Encoding.UTF8.GetString(headerBytes);
                    }
                    staged[i][sourcePath] = headers;
                }
            }

            // Commit -- atomic across partitions because no other thread
            // can be reading the cache mid-LoadFromDisk (the cache is
            // constructed and then handed out; load happens inside the
            // constructor's caller).
            for (int i = 0; i < partitionCount; i++)
            {
                foreach ((string path, string[] headers) in staged[i])
                {
                    _partitions[i].Set(path, headers);
                }
            }
        }
        catch (EndOfStreamException)
        {
            // Torn read -- staged partitions live in locals and never
            // reached _partitions; clear is defence-in-depth.
            ClearAllPartitions();
        }
        catch (IOException)
        {
            // Treat as empty archive.
        }
    }

    /// <summary>
    /// Persist the archive to disk. Mirrors
    /// <see cref="ActionHistory.Save"/>: write to a sibling
    /// <c>.tmp.&lt;pid&gt;.&lt;guid&gt;</c> file, fsync, rename. The
    /// rename is atomic on every supported filesystem; a torn write
    /// leaves the previous archive intact on disk.
    /// </summary>
    public void Save()
    {
        string directory = Path.GetDirectoryName(_archivePath)!;
        int pid = Environment.ProcessId;
        string nonce = Guid.NewGuid().ToString("N");
        string tempPath = Path.Combine(directory, $"CppDependencyCache.bin.tmp.{pid}.{nonce}");

        // Snapshot every partition under its lock. Mirrors
        // ActionHistory.Save's per-partition snapshot pattern.
        IReadOnlyDictionary<string, string[]>[] snapshots =
            new IReadOnlyDictionary<string, string[]>[PartitionCount];
        System.Threading.Tasks.Parallel.For(0, PartitionCount, i =>
        {
            _partitions[i].Snapshot(out snapshots[i]);
        });

        using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(s_magic);
                writer.Write(FormatVersion);
                writer.Write(ActionHistory.CurrentVersion.ToByteArray());
                writer.Write(PartitionCount);

                for (int i = 0; i < PartitionCount; i++)
                {
                    IReadOnlyDictionary<string, string[]> entries = snapshots[i];
                    writer.Write(entries.Count);
                    foreach ((string path, string[] headers) in entries.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    {
                        byte[] sourcePathBytes = Encoding.UTF8.GetBytes(path);
                        writer.Write(sourcePathBytes.Length);
                        writer.Write(sourcePathBytes);

                        writer.Write(headers.Length);
                        foreach (string h in headers)
                        {
                            byte[] hBytes = Encoding.UTF8.GetBytes(h);
                            writer.Write(hBytes.Length);
                            writer.Write(hBytes);
                        }
                    }
                }
            }
            stream.Flush(flushToDisk: true);
        }

        FileSystemOps.RetryOnTransientIOException(
            () => File.Move(tempPath, _archivePath, overwrite: true));
    }

    private void ClearAllPartitions()
    {
        for (int i = 0; i < _partitions.Length; i++)
        {
            _partitions[i].Clear();
        }
    }

    /// <summary>
    /// Map a source path to its partition index. Mirrors
    /// <see cref="ActionHistory.PartitionOf"/> so the same path routes to
    /// the same partition slot in both caches.
    /// </summary>
    private static int PartitionOf(string fullPath)
    {
        Span<byte> digest = stackalloc byte[IoHash.Length];
        ReadOnlySpan<byte> utf8 = Encoding.UTF8.GetBytes(fullPath);
        using Hasher hasher = Hasher.New();
        hasher.Update(utf8);
        hasher.Finalize(digest);
        return digest[0];
    }

    /// <summary>
    /// In-memory map for a single partition. Mirrors the
    /// <see cref="ActionHistory"/> partition shape but stores recorded
    /// header paths (string[]) instead of producer/content hashes.
    /// </summary>
    private sealed class Partition
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, string[]> _map = new(StringComparer.Ordinal);

        public bool TryGet(string path, out string[]? headers)
        {
            lock (_gate)
            {
                if (_map.TryGetValue(path, out string[]? stored))
                {
                    headers = stored;
                    return true;
                }
                headers = null;
                return false;
            }
        }

        public void Set(string path, string[] headers)
        {
            lock (_gate)
            {
                _map[path] = headers;
            }
        }

        public void Snapshot(out IReadOnlyDictionary<string, string[]> snapshot)
        {
            lock (_gate)
            {
                snapshot = new Dictionary<string, string[]>(_map, StringComparer.Ordinal);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _map.Clear();
            }
        }
    }
}
