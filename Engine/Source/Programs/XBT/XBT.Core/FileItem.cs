// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Simgenics.XPact.XBT.Core;

/// <summary>
/// Tracked, cached descriptor for a single file on disk.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors UE's <c>FileItem</c> in intent (one canonical record per absolute
/// path) but with one explicit rule the Toolchain Contract bans Unreal-style
/// behaviour around: <strong>modification time is informational only.</strong>
/// XBT.ActionGraph keys invalidation on <see cref="IoHash"/> content hashes,
/// never on <see cref="LastWriteTimeUtc"/>. See
/// <c>/Documents/XToolchainContract.html</c> Rev 13 Section 2.1 ("Engine-wide
/// invalidation policy: Timestamp-based invalidation is banned engine-wide.")
/// </para>
/// <para>
/// Construction is lazy: <see cref="ContentHash"/> is computed at most once
/// per <see cref="FileItem"/>, on first access, and cached. The factory
/// <see cref="GetItemByPath(string)"/> de-duplicates instances per absolute
/// path inside a process via a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </para>
/// </remarks>
public sealed class FileItem
{
    // Use Ordinal comparison engine-wide. Path canonicalisation
    // happens in GetItemByPath via Path.GetFullPath, which collapses
    // Windows case-insensitivity to a single canonical casing per
    // file. Linux paths are genuinely case-sensitive and MUST NOT
    // collide on case-only differences -- otherwise /foo/Bar.h and
    // /foo/bar.h share a FileItem on Linux despite being distinct
    // files on disk (cross-machine determinism trap on a CI matrix
    // that mixes Windows and Linux runners).
    private static readonly ConcurrentDictionary<string, FileItem> s_cache =
        new(StringComparer.Ordinal);

    private readonly object _hashGate = new();
    private IoHash _contentHash;
    private volatile bool _hashComputed;
    private long _length;
    private DateTime _lastWriteTimeUtc;
    private bool _statLoaded;

    /// <summary>Absolute path. UTF-8 on Linux/Android; UTF-16 on Win32 via .NET.</summary>
    public string FullPath { get; }

    /// <summary>
    /// File length in bytes. Refreshed once at first stat; if the file is
    /// modified between stat and hash, the hash governs (mtime never does).
    /// </summary>
    public long Length
    {
        get { EnsureStat(); return _length; }
    }

    /// <summary>
    /// Last write time, informational only.
    /// <strong>Never used for cache invalidation</strong> per Toolchain
    /// Contract Rev 13 Section 2.1. Surfaced for human-readable diagnostics
    /// (e.g. "what was the most recent change in this module?") and for the
    /// orphan-temp-file sweep at startup per <c>/Documents/XBT.html</c>
    /// Section 6.4 (which checks PIDs, not mtimes -- mtime is shown for
    /// log readability only).
    /// </summary>
    public DateTime LastWriteTimeUtc
    {
        get { EnsureStat(); return _lastWriteTimeUtc; }
    }

    /// <summary>
    /// BLAKE3 content hash. Computed on first access; cached for the
    /// lifetime of this <see cref="FileItem"/> instance. This is the
    /// canonical identity used by XBT.ActionGraph's invalidation rules.
    /// </summary>
    public IoHash ContentHash
    {
        get
        {
            if (_hashComputed)
            {
                return _contentHash;
            }
            lock (_hashGate)
            {
                if (_hashComputed)
                {
                    return _contentHash;
                }
                using FileStream stream = File.OpenRead(FullPath);
                _contentHash = IoHash.Compute(stream);
                Thread.MemoryBarrier();
                _hashComputed = true;
                return _contentHash;
            }
        }
    }

    private FileItem(string fullPath)
    {
        FullPath = fullPath;
    }

    /// <summary>
    /// Get or create the canonical <see cref="FileItem"/> for an absolute
    /// path. The factory de-duplicates per process so two consumers asking
    /// for the same path share a single hash cache.
    /// </summary>
    public static FileItem GetItemByPath(string fullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        string normalized = Path.GetFullPath(fullPath);
        return s_cache.GetOrAdd(normalized, static p => new FileItem(p));
    }

    /// <summary>
    /// Drop the cached file metadata (length, mtime) and the content-hash
    /// memoization. Call after externally writing to the underlying file
    /// to force a re-read on the next access. Rare; XBT generally creates
    /// a fresh <see cref="FileItem"/> via the factory rather than mutating
    /// an existing one.
    /// </summary>
    public void Invalidate()
    {
        lock (_hashGate)
        {
            _statLoaded = false;
            _hashComputed = false;
            _contentHash = default;
            _length = 0;
            _lastWriteTimeUtc = default;
        }
    }

    /// <summary>
    /// Async variant of <see cref="ContentHash"/> for I/O-bound callers
    /// who hold a <see cref="CancellationToken"/>. Reads the file via
    /// <see cref="FileStream"/> async APIs.
    /// </summary>
    public async Task<IoHash> ComputeContentHashAsync(CancellationToken cancellationToken)
    {
        if (_hashComputed)
        {
            return _contentHash;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await using FileStream stream = new(
            FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);

        // We do the actual hashing synchronously inside a non-async local
        // helper because Blake3.Hasher uses Span<byte> (a ref struct) for
        // its digest output and ref structs cannot cross await boundaries
        // in C# 12. Reads dominate cost on cold-cache files; the hash
        // itself is fast.
        IoHash hash = await ReadAndHashAsync(stream, cancellationToken).ConfigureAwait(false);

        lock (_hashGate)
        {
            _contentHash = hash;
            Thread.MemoryBarrier();
            _hashComputed = true;
            return _contentHash;
        }
    }

    private static async Task<IoHash> ReadAndHashAsync(Stream stream, CancellationToken cancellationToken)
    {
        const int chunkSize = 64 * 1024;
        byte[] buffer = new byte[chunkSize];
        using Blake3.Hasher hasher = Blake3.Hasher.New();
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, chunkSize), cancellationToken).ConfigureAwait(false)) > 0)
        {
            hasher.Update(buffer.AsSpan(0, read));
        }
        return FinalizeHasher(hasher);
    }

    private static IoHash FinalizeHasher(Blake3.Hasher hasher)
    {
        Span<byte> digest = stackalloc byte[IoHash.Length];
        hasher.Finalize(digest);
        return new IoHash(digest);
    }

    private void EnsureStat()
    {
        if (_statLoaded)
        {
            return;
        }
        lock (_hashGate)
        {
            if (_statLoaded)
            {
                return;
            }
            FileInfo info = new(FullPath);
            _length = info.Exists ? info.Length : -1L;
            _lastWriteTimeUtc = info.Exists ? info.LastWriteTimeUtc : default;
            _statLoaded = true;
        }
    }

    public override string ToString() => FullPath;
}
