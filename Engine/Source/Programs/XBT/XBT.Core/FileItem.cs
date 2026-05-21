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
    // Audit fix M5: every read of _hashComputed and _contentHash now
    // happens under _hashGate so the previous `volatile` modifier is
    // unnecessary. The lock provides the release semantics the boolean
    // alone could not under the .NET memory model on ARM64.
    private bool _hashComputed;
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
    /// <remarks>
    /// <para>
    /// Audit fix M5: the read path used to short-circuit on a volatile
    /// <c>_hashComputed</c> boolean and then return <c>_contentHash</c>
    /// without locking. <see cref="IoHash"/> is 32 bytes; under the .NET
    /// CLR memory model on ARM64 a reader can observe a half-written
    /// struct alongside <c>_hashComputed = true</c> because the struct
    /// store is not atomic and there is no release-barrier guarantee on
    /// the boolean flag. We now always take the per-instance lock; the
    /// performance cost is one uncontended-lock acquisition per read
    /// (hash is computed once, so subsequent reads are fast-path).
    /// </para>
    /// <para>
    /// Audit fix R7-C3: the file read is wrapped in
    /// <see cref="FileSystemOps.RetryOnTransientIOException{T}(Func{T})"/>
    /// so an AV-induced sharing violation on a just-written file is
    /// retried on the standard back-off schedule rather than surfacing
    /// as a build failure. The retry catches <see cref="IOException"/>
    /// and <see cref="UnauthorizedAccessException"/> only; real I/O
    /// errors still propagate on the final attempt.
    /// </para>
    /// </remarks>
    public IoHash ContentHash
    {
        get
        {
            lock (_hashGate)
            {
                if (_hashComputed)
                {
                    return _contentHash;
                }
                _contentHash = ComputeContentHashWithRetry(FullPath);
                _hashComputed = true;
                return _contentHash;
            }
        }
    }

    /// <summary>
    /// Audit fix R7-C3: AV-retry-wrapped sync read path. Hoisted out of
    /// the property body so the retry-helper closure captures only the
    /// path argument, not the <see cref="FileItem"/> instance.
    /// </summary>
    private static IoHash ComputeContentHashWithRetry(string fullPath)
    {
        return FileSystemOps.RetryOnTransientIOException(() =>
        {
            using FileStream stream = File.OpenRead(fullPath);
            return IoHash.Compute(stream);
        });
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
        // Audit fix M4: normalize the drive letter to uppercase on
        // Windows so two callers asking for "c:\path\foo.h" and
        // "C:\path\foo.h" get the same FileItem instance. Path.GetFullPath
        // collapses path traversal but does NOT canonicalize the drive
        // letter casing -- the case-insensitive FS treats them as one
        // file but our s_cache (StringComparer.Ordinal) would treat them
        // as two distinct entries.
        string normalized = NormalizePathForCache(fullPath);
        return s_cache.GetOrAdd(normalized, static p => new FileItem(p));
    }

    /// <summary>
    /// Audit fix M4: canonical-form helper for s_cache keys.
    /// On Windows, normalises the drive letter to uppercase so the
    /// case-insensitive filesystem maps to a single canonical key. On
    /// Linux/macOS the path is genuinely case-sensitive and we preserve
    /// the input case (e.g. <c>/foo/Bar.h</c> and <c>/foo/bar.h</c> are
    /// distinct files on disk).
    /// </summary>
    private static string NormalizePathForCache(string fullPath)
    {
        string canonical = Path.GetFullPath(fullPath);
        if (OperatingSystem.IsWindows())
        {
            if (canonical.Length >= 2 && canonical[1] == ':')
            {
                return char.ToUpperInvariant(canonical[0]) + canonical.Substring(1);
            }
        }
        return canonical;
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
    /// <remarks>
    /// <para>
    /// Audit fix M5: always guard <c>_contentHash</c> reads under the
    /// per-instance lock; no volatile-flag short-circuit. See
    /// <see cref="ContentHash"/>'s remarks for the ARM64 rationale.
    /// </para>
    /// <para>
    /// Audit fix R7-C3: the read is wrapped in
    /// <see cref="FileSystemOps.RetryOnTransientIOExceptionAsync{T}(Func{Task{T}}, CancellationToken)"/>
    /// so an AV-induced sharing violation is retried on the standard
    /// back-off schedule rather than surfacing as an action failure.
    /// The retry uses <see cref="Task.Delay(int, CancellationToken)"/>
    /// so a blocked retry does not pin a thread-pool worker (unlike
    /// the sync <see cref="Thread.Sleep(int)"/> path).
    /// </para>
    /// </remarks>
    public async Task<IoHash> ComputeContentHashAsync(CancellationToken cancellationToken)
    {
        // Fast path: if the hash was already computed, return it under
        // the lock. The lock acquisition is uncontended in the common
        // case (hash is computed once, then read many times).
        lock (_hashGate)
        {
            if (_hashComputed)
            {
                return _contentHash;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        string fullPath = FullPath;
        IoHash hash = await FileSystemOps.RetryOnTransientIOExceptionAsync(
            async () =>
            {
                await using FileStream stream = new(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    useAsync: true);
                // We do the actual hashing synchronously inside a
                // non-async local helper because Blake3.Hasher uses
                // Span<byte> (a ref struct) for its digest output and
                // ref structs cannot cross await boundaries in C# 12.
                // Reads dominate cost on cold-cache files; the hash
                // itself is fast.
                return await ReadAndHashAsync(stream, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        lock (_hashGate)
        {
            // Recheck inside the lock (another thread may have raced us).
            if (_hashComputed)
            {
                return _contentHash;
            }
            _contentHash = hash;
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

    /// <summary>
    /// Populate <see cref="_length"/> and <see cref="_lastWriteTimeUtc"/>
    /// from the file on disk; idempotent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Audit fix R3-M3: drop the volatile-boolean fast path and always
    /// acquire <see cref="_hashGate"/>. The previous implementation
    /// short-circuited on a non-volatile <c>_statLoaded == true</c>
    /// read; under the .NET CLR memory model on ARM64 a reader could
    /// observe <c>_statLoaded = true</c> alongside a stale
    /// <c>_length</c> / <c>_lastWriteTimeUtc</c> value because there is
    /// no release barrier between the field stores and the flag store.
    /// Mirrors the audit-fix-M5 treatment applied to
    /// <see cref="ContentHash"/> in Round 1: take the lock
    /// unconditionally so the field reads under the same monitor that
    /// the writes published under.
    /// </para>
    /// <para>
    /// The cost is one uncontended-lock acquisition per stat read; the
    /// stat itself is dominated by the underlying <c>FileInfo</c>
    /// syscall on first call and is a cheap field read on subsequent
    /// calls. The lock acquisition is uncontended in steady state
    /// because (1) each <see cref="FileItem"/> is read by the
    /// post-action recorder typically once per build, and (2) the
    /// per-instance lock has no cross-instance contention.
    /// </para>
    /// </remarks>
    private void EnsureStat()
    {
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
