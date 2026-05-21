// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.Entry;

/// <summary>
/// Per-file reflection-marker-presence cache. Maps an absolute source
/// path to (content hash, marker-present boolean) so the marker-scan
/// short-circuits on subsequent builds when the file hasn't changed.
/// </summary>
/// <remarks>
/// <para>
/// Audit fix R7-C5: the prior implementation rescanned every header on
/// every build, costing one full file read + UTF-8 decode + substring
/// scan per <c>.h</c> + <c>.cs</c> for every module that XBT
/// considered for reflection. At engine scale (thousands of headers)
/// this dominated the discovery wall-time. The cache stores the
/// scan result keyed by the file's BLAKE3 content hash; the next
/// build short-circuits when the on-disk content hash matches.
/// </para>
/// <para>
/// <b>Storage.</b> Persisted as a binary archive at
/// <c>Intermediate/Build/&lt;Target&gt;/ReflectionMarkerCache.bin</c>,
/// alongside <c>ActionHistory.bin</c>. The format is parallel to
/// <see cref="ActionGraph.ActionHistory"/>'s single-file layout but
/// kept distinct because the cache schema is unrelated to
/// <see cref="ActionGraph.IExternalAction"/>'s property surface --
/// extending ActionHistory.bin's format with an unrelated map would
/// be a layering inversion.
/// </para>
/// <para>
/// <b>Atomicity.</b> Save() writes via
/// <see cref="FileSystemOps.AtomicWriteAllBytes"/> -- the standard
/// temp-write + fsync + atomic-rename + AV-retry pipeline. A torn
/// write leaves the previous cache intact.
/// </para>
/// <para>
/// <b>Concurrency.</b> Reads and writes are thread-safe via a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>. The cache is
/// expected to be touched from the discovery thread (single threaded
/// in current BuildMode) but the concurrent map keeps Phase 2
/// parallel-discovery refactors safe.
/// </para>
/// </remarks>
internal sealed class ReflectionMarkerCache
{
    /// <summary>Magic prefix on the file header for format identification.</summary>
    private static readonly byte[] s_magic = Encoding.ASCII.GetBytes("XRMC");

    /// <summary>
    /// File format version. Bumped on any breaking format change.
    /// </summary>
    private const int FormatVersion = 1;

    private readonly string _cachePath;
    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.Ordinal);

    /// <summary>Cache record: (content hash, marker present).</summary>
    /// <remarks>
    /// The content hash is the BLAKE3 of the file's bytes (as
    /// <see cref="FileItem.ContentHash"/> reports). The cache hit
    /// requires this to match the live <see cref="FileItem.ContentHash"/>
    /// so an edit that doesn't change the marker-presence boolean (e.g.
    /// adding a comment) still updates the stored hash on the next save.
    /// </remarks>
    private readonly record struct Entry(IoHash ContentHash, bool HasMarkers);

    private ReflectionMarkerCache(string cachePath)
    {
        _cachePath = cachePath;
    }

    /// <summary>
    /// Open the marker cache for the given target/configuration. The
    /// cache lives at
    /// <c>&lt;targetDir&gt;/&lt;config&gt;/ReflectionMarkerCache.bin</c>;
    /// when the file does not exist a fresh empty cache is returned.
    /// </summary>
    public static ReflectionMarkerCache Open(string targetDir, Manifest.BuildConfiguration config)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetDir);

        Directory.CreateDirectory(targetDir);
        string configDir = Path.Combine(targetDir, config.ToString());
        Directory.CreateDirectory(configDir);

        string path = Path.Combine(configDir, "ReflectionMarkerCache.bin");
        ReflectionMarkerCache cache = new(path);
        cache.LoadFromDisk();
        return cache;
    }

    /// <summary>
    /// Test hook: open a cache at an arbitrary path without imposing
    /// the <c>&lt;targetDir&gt;/&lt;config&gt;/</c> layout.
    /// </summary>
    internal static ReflectionMarkerCache OpenAtPath(string cachePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(cachePath);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

        ReflectionMarkerCache cache = new(cachePath);
        cache.LoadFromDisk();
        return cache;
    }

    /// <summary>
    /// Try to find a cached marker-presence result for
    /// <paramref name="fullPath"/> whose stored content hash matches
    /// <paramref name="contentHash"/>. Returns false on miss (stale or
    /// missing) so the caller re-scans.
    /// </summary>
    public bool TryGet(string fullPath, IoHash contentHash, out bool hasMarkers)
    {
        if (_entries.TryGetValue(fullPath, out Entry entry) && entry.ContentHash == contentHash)
        {
            hasMarkers = entry.HasMarkers;
            return true;
        }
        hasMarkers = false;
        return false;
    }

    /// <summary>
    /// Record (or update) the cached marker-presence for a file.
    /// Idempotent; safe to call concurrently from multiple discovery
    /// threads.
    /// </summary>
    public void Set(string fullPath, IoHash contentHash, bool hasMarkers)
    {
        _entries[fullPath] = new Entry(contentHash, hasMarkers);
    }

    /// <summary>
    /// Persist the cache to disk via the standard atomic-write pipeline
    /// (temp file + fsync + atomic rename + AV-retry).
    /// </summary>
    public void Save()
    {
        using MemoryStream ms = new();
        using (BinaryWriter writer = new(ms, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(s_magic);
            writer.Write(FormatVersion);

            // Snapshot the dict before serialising to avoid a concurrent
            // modification while we walk it. ConcurrentDictionary's
            // ToArray is internally locked.
            KeyValuePair<string, Entry>[] snapshot = _entries.ToArray();
            // Sort by path ordinal so the on-disk byte sequence is
            // invariant across runs (mirror of ActionHistory.Save's
            // determinism discipline).
            Array.Sort(
                snapshot,
                (a, b) => string.CompareOrdinal(a.Key, b.Key));

            writer.Write(snapshot.Length);
            foreach (KeyValuePair<string, Entry> kv in snapshot)
            {
                byte[] pathBytes = Encoding.UTF8.GetBytes(kv.Key);
                writer.Write(pathBytes.Length);
                writer.Write(pathBytes);
                writer.Write(kv.Value.ContentHash.ToByteArray());
                writer.Write(kv.Value.HasMarkers);
            }
        }
        FileSystemOps.AtomicWriteAllBytes(_cachePath, ms.ToArray());
    }

    /// <summary>
    /// Load the cache from disk. Missing file = empty cache. Corrupt
    /// or version-mismatch = empty cache (next save rewrites cleanly).
    /// </summary>
    private void LoadFromDisk()
    {
        if (!File.Exists(_cachePath))
        {
            return;
        }

        try
        {
            using FileStream stream = FileSystemOps.RetryOnTransientIOException(
                () => new FileStream(_cachePath, FileMode.Open, FileAccess.Read, FileShare.Read));
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

            int entryCount = reader.ReadInt32();
            if (entryCount < 0)
            {
                return;
            }

            for (int i = 0; i < entryCount; i++)
            {
                int pathLen = reader.ReadInt32();
                if (pathLen < 0 || pathLen > 4096)
                {
                    return;
                }
                byte[] pathBytes = reader.ReadBytes(pathLen);
                if (pathBytes.Length != pathLen)
                {
                    return;
                }
                string path = Encoding.UTF8.GetString(pathBytes);

                byte[] hashBytes = reader.ReadBytes(IoHash.Length);
                if (hashBytes.Length != IoHash.Length)
                {
                    return;
                }
                IoHash contentHash = new(hashBytes);

                bool hasMarkers = reader.ReadBoolean();
                _entries[path] = new Entry(contentHash, hasMarkers);
            }
        }
        catch (EndOfStreamException)
        {
            // Truncated -- treat as empty (cache is best-effort).
            _entries.Clear();
        }
        catch (IOException)
        {
            // Treat as empty cache; the next Save() will overwrite.
        }
    }

    /// <summary>Test hook: number of cached entries.</summary>
    internal int Count => _entries.Count;
}
