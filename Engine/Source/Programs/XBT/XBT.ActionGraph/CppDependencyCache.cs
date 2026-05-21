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
    /// Escaping rules: a backslash at end-of-line continues the line
    /// onto the next; a backslash followed by a space is a literal space
    /// inside a path; everything else is a literal. The drive-letter
    /// colon on Windows (<c>C:\foo\bar.h</c>) is NOT treated as the
    /// target/prereq separator -- only the first non-escaped <c>:</c>
    /// that is followed by whitespace (or end-of-line) is the
    /// separator, mirroring GNU make's behaviour.
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

        // First, strip backslash-newline line continuations. The
        // sequence "\\\n" (and "\\\r\n") becomes a single space so the
        // line continues into a single logical row but token boundaries
        // are preserved.
        StringBuilder flat = new(text.Length);
        int n = text.Length;
        for (int i = 0; i < n; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < n)
            {
                char next = text[i + 1];
                if (next == '\n')
                {
                    flat.Append(' ');
                    i++;
                    continue;
                }
                if (next == '\r' && i + 2 < n && text[i + 2] == '\n')
                {
                    flat.Append(' ');
                    i += 2;
                    continue;
                }
                // Escaped space inside a path: emit a literal space but
                // mark with a sentinel character (U+FFFE -- a Unicode
                // non-character so it cannot appear in any legitimate
                // path) so the token splitter does not split on it.
                if (next == ' ')
                {
                    flat.Append('￾');
                    i++;
                    continue;
                }
                // Escaped backslash -- emit one backslash.
                if (next == '\\')
                {
                    flat.Append('\\');
                    i++;
                    continue;
                }
                // Unrecognised escape -- emit the backslash literally
                // and reprocess the next char on the next loop iteration.
                flat.Append(c);
                continue;
            }
            flat.Append(c);
        }
        string flattened = flat.ToString();

        // Now find the target/prereq boundary. The first <c>:</c> NOT
        // followed by a non-whitespace char (so we don't split on a
        // Windows drive-letter colon <c>C:</c>) is the boundary.
        int colon = FindTargetSeparator(flattened);
        if (colon < 0)
        {
            // No target/prereq separator -- treat as no prereqs.
            return Array.Empty<string>();
        }
        string prereqRegion = flattened[(colon + 1)..];

        // Tokenize on whitespace. The sentinel U+FFFE preserves escaped
        // spaces inside paths; replace it with a literal space at token
        // emit time.
        List<string> tokens = new();
        int start = -1;
        for (int i = 0; i < prereqRegion.Length; i++)
        {
            char c = prereqRegion[i];
            bool isWs = c is ' ' or '\t' or '\r' or '\n';
            if (isWs)
            {
                if (start >= 0)
                {
                    string token = prereqRegion[start..i].Replace('￾', ' ');
                    if (token.Length > 0)
                    {
                        tokens.Add(token);
                    }
                    start = -1;
                }
            }
            else
            {
                if (start < 0)
                {
                    start = i;
                }
            }
        }
        if (start >= 0)
        {
            string token = prereqRegion[start..].Replace('￾', ' ');
            if (token.Length > 0)
            {
                tokens.Add(token);
            }
        }
        return tokens;
    }

    /// <summary>
    /// Locate the target/prereq <c>:</c> separator in a flattened
    /// Makefile-format depfile. The separator is the first <c>:</c>
    /// whose right-hand neighbour is either whitespace, end-of-string,
    /// or end-of-line -- this excludes Windows drive-letter colons
    /// (<c>C:\path</c>) where the right-hand char is <c>\</c>.
    /// </summary>
    private static int FindTargetSeparator(string flattened)
    {
        for (int i = 0; i < flattened.Length; i++)
        {
            if (flattened[i] != ':')
            {
                continue;
            }
            // End of string -- treat as separator (degenerate depfile
            // with no prereqs).
            if (i + 1 >= flattened.Length)
            {
                return i;
            }
            char next = flattened[i + 1];
            if (next is ' ' or '\t' or '\r' or '\n')
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Parse an MSVC <c>/sourceDependencies</c> JSON depfile. Returns
    /// the <c>Data.Includes</c> array. The <c>Source</c> field is
    /// captured separately by the caller via the action's source path;
    /// the cache keys recorded dependencies on the source path, not on
    /// the depfile's <c>Source</c> field.
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
    ///     "ImportedModules": [ ... ],
    ///     "ImportedHeaderUnits": [ ... ]
    ///   }
    /// }
    /// </code>
    /// Version 1.0 and 1.1 are accepted verbatim. Version 1.2+ may have
    /// schema additions; we still try to read the <c>Includes</c> array
    /// (the field has been stable across the 1.x line) and emit a one-
    /// time diagnostic so operators are aware the cache may miss new
    /// dependency-source fields (e.g. C++20 module imports).
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> ParseMsvcSourceDependenciesJson(byte[] bytes, int startIndex, string diagnosticLabel)
    {
        JsonReaderOptions readerOpts = new()
        {
            MaxDepth = MaxJsonDepth,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        };
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

        // Version check -- accept 1.0 / 1.1 silently; emit a diagnostic
        // for 1.2+ so an operator can correlate a missing dep with a
        // schema change.
        if (root.TryGetProperty("Version", out JsonElement versionElement)
            && versionElement.ValueKind == JsonValueKind.String)
        {
            string? versionStr = versionElement.GetString();
            if (!string.IsNullOrEmpty(versionStr)
                && !versionStr.StartsWith("1.0", StringComparison.Ordinal)
                && !versionStr.StartsWith("1.1", StringComparison.Ordinal))
            {
                Logger.Info(
                    $"CppDependencyCache: MSVC /sourceDependencies depfile '{diagnosticLabel}' " +
                    $"declares schema version '{versionStr}' (XBT supports 1.0 / 1.1). " +
                    "Attempting to parse Includes anyway; new schema fields (e.g. C++20 module imports) " +
                    "may not be recorded for invalidation. File an XBT issue if cache misses correlate with this.");
            }
        }

        if (!root.TryGetProperty("Data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<string>();
        }

        if (!data.TryGetProperty("Includes", out JsonElement includes)
            || includes.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        List<string> result = new(includes.GetArrayLength());
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

    /// <summary>
    /// Load the archive into memory. Mirrors
    /// <see cref="ActionHistory.LoadFromDisk"/>: missing file = empty
    /// archive; corrupt/truncated/mismatched-version file = empty
    /// archive (the cache layer is allowed to lose entries on a torn
    /// archive -- the next build re-discovers everything).
    /// </summary>
    public void Load() => LoadFromDisk();

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
