// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Simgenics.XPact.XBT.Core;

/// <summary>
/// Filesystem-level retry helpers for transient antivirus-induced file
/// lock failures on Windows. Mirrors UBT's
/// <c>FileReference.RetryOnIOException</c> pattern. Also provides
/// atomic write helpers used by every XBT producer.
/// </summary>
/// <remarks>
/// <para>
/// Audit fix R6-C5 (UBT-parity); Round-7 C-3 extended with an async
/// overload and a read-side wrapper.
/// </para>
/// <para>
/// <b>Why this exists.</b> Windows Defender (and most third-party AV
/// products) hook every just-written file for a scanning window. While
/// the scan is in flight the file is opened with a sharing mode that
/// excludes <c>MoveFileEx</c> / <c>DeleteFileW</c>, so a tight write -&gt;
/// rename or write -&gt; delete sequence -- which the atomic-write
/// pattern relies on -- raises <see cref="IOException"/> (sharing
/// violation) or <see cref="UnauthorizedAccessException"/>. The
/// transient lock typically clears within tens of milliseconds; this
/// helper sleeps and retries instead of failing the build. The
/// behaviour applies equally to <em>reads</em> of a just-written file:
/// an action graph that records a producer-action key then immediately
/// computes the produced file's content hash races the AV scan and
/// must retry on the same schedule.
/// </para>
/// <para>
/// <b>Retry schedule.</b> Four attempts total with backoff
/// <c>[100ms, 200ms, 1000ms, 5000ms]</c>, totalling 6.3 s before
/// surrender. The schedule is the same one UBT uses, balancing fast
/// recovery (the first retry catches >90% of cases per UBT's docs)
/// against not blocking the build forever on a genuinely-stuck file.
/// </para>
/// <para>
/// <b>Exception filter.</b> We retry on <see cref="IOException"/> (the
/// sharing-violation case) and <see cref="UnauthorizedAccessException"/>
/// (the AV-quarantine case). Any other exception propagates immediately
/// -- a missing-directory error, a permission-denied that is not an AV
/// lock, etc. should not be papered over.
/// </para>
/// </remarks>
public static class FileSystemOps
{
    /// <summary>
    /// Backoff schedule in milliseconds. The schedule is fixed (not
    /// exponential) so the worst-case latency is bounded and
    /// predictable; the build's progress-reporting cadence stays
    /// sensible even when several files retry in series.
    /// </summary>
    private static readonly int[] s_backoffMs = new[] { 100, 200, 1000, 5000 };

    /// <summary>
    /// Run <paramref name="operation"/>; on transient
    /// <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>,
    /// retry on the schedule documented in <see cref="FileSystemOps"/>.
    /// On the final attempt, the last exception is rethrown.
    /// </summary>
    /// <param name="operation">The filesystem operation to attempt.</param>
    /// <exception cref="IOException">
    /// Thrown when every retry attempt fails with an
    /// <see cref="IOException"/>.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown when every retry attempt fails with an
    /// <see cref="UnauthorizedAccessException"/>.
    /// </exception>
    public static void RetryOnTransientIOException(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (IOException) when (attempt < s_backoffMs.Length)
            {
                Thread.Sleep(s_backoffMs[attempt]);
            }
            catch (UnauthorizedAccessException) when (attempt < s_backoffMs.Length)
            {
                Thread.Sleep(s_backoffMs[attempt]);
            }
        }
    }

    /// <summary>
    /// Generic variant of <see cref="RetryOnTransientIOException(Action)"/>
    /// for read paths that return a value (file hash, file bytes, etc.).
    /// </summary>
    /// <remarks>
    /// Round-7 C-3: extended so <c>FileItem.ContentHash</c> can race the
    /// AV scan on a just-written produced file without failing the build.
    /// </remarks>
    /// <typeparam name="T">The result type produced by the operation.</typeparam>
    /// <param name="operation">The filesystem read operation to attempt.</param>
    /// <returns>The value returned by the successful attempt.</returns>
    public static T RetryOnTransientIOException<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (IOException) when (attempt < s_backoffMs.Length)
            {
                Thread.Sleep(s_backoffMs[attempt]);
            }
            catch (UnauthorizedAccessException) when (attempt < s_backoffMs.Length)
            {
                Thread.Sleep(s_backoffMs[attempt]);
            }
        }
    }

    /// <summary>
    /// Async variant of <see cref="RetryOnTransientIOException{T}(Func{T})"/>.
    /// Awaits the operation on each attempt; on transient
    /// <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>
    /// delays cooperatively via <see cref="Task.Delay(int)"/>.
    /// </summary>
    /// <remarks>
    /// Round-7 C-3: provides the async path used by
    /// <c>FileItem.ComputeContentHashAsync</c> and the future Phase 2
    /// async readers. Uses <see cref="Task.Delay(int, CancellationToken)"/>
    /// rather than <see cref="Thread.Sleep(int)"/> so a blocked retry
    /// doesn't pin a thread-pool worker.
    /// </remarks>
    public static async Task<T> RetryOnTransientIOExceptionAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (IOException) when (attempt < s_backoffMs.Length)
            {
                await Task.Delay(s_backoffMs[attempt], cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < s_backoffMs.Length)
            {
                await Task.Delay(s_backoffMs[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Async variant of <see cref="RetryOnTransientIOException(Action)"/>.
    /// </summary>
    /// <remarks>
    /// Round-7 C-3: async-friendly counterpart for callers that need a
    /// Task without a return value (e.g. a future move-into-place
    /// pipeline).
    /// </remarks>
    public static async Task RetryOnTransientIOExceptionAsync(
        Func<Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await operation().ConfigureAwait(false);
                return;
            }
            catch (IOException) when (attempt < s_backoffMs.Length)
            {
                await Task.Delay(s_backoffMs[attempt], cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < s_backoffMs.Length)
            {
                await Task.Delay(s_backoffMs[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Atomic write helper: write <paramref name="bytes"/> to a uniquely-
    /// named temp file in the destination directory, fsync, then
    /// rename over the target. The rename is atomic on every supported
    /// filesystem (NTFS, ext4, APFS); a power loss after rename cannot
    /// leave a half-written destination because fsync runs first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Round-7 C-1: shared helper used by <c>ProcessActionRunner</c>'s
    /// response-file materialization. Mirrors the
    /// <c>ManifestJson.AtomicWriteAllBytes</c> +
    /// <c>ActionHistory.Save</c> pattern verbatim (same temp-file
    /// naming convention, same AV-retry wrap on the rename) so the
    /// orphan-temp-file sweep in <c>ParallelExecutor</c> can identify
    /// and reap response-file temps from a crashed XBT run.
    /// </para>
    /// <para>
    /// Temp-file naming: <c>&lt;baseName&gt;.tmp.&lt;pid&gt;.&lt;guid&gt;</c>.
    /// The <c>&lt;guid&gt;</c> nonce is "N"-format (32 hex chars) so
    /// the orphan sweep's hex-suffix check passes. No timestamp anywhere
    /// per Toolchain Contract Rev 13.6 Section 2.1 (footgun #1 preempt).
    /// </para>
    /// </remarks>
    /// <param name="destinationPath">Absolute path to write to.</param>
    /// <param name="bytes">The bytes to write.</param>
    public static void AtomicWriteAllBytes(string destinationPath, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        ArgumentNullException.ThrowIfNull(bytes);

        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        string parent = string.IsNullOrEmpty(directory) ? "." : directory;

        int pid = Environment.ProcessId;
        string nonce = Guid.NewGuid().ToString("N");
        string baseName = Path.GetFileName(destinationPath);
        string tempPath = Path.Combine(parent, $"{baseName}.tmp.{pid}.{nonce}");

        using (FileStream fs = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        RetryOnTransientIOException(
            () => File.Move(tempPath, destinationPath, overwrite: true));
    }

    /// <summary>
    /// Atomic write helper for UTF-8 text. Equivalent to
    /// <see cref="AtomicWriteAllBytes(string, byte[])"/> with a UTF-8
    /// encoding pass; convenience overload for callers that already
    /// hold a string.
    /// </summary>
    /// <remarks>
    /// Round-7 C-1: used by <c>ProcessActionRunner</c> for the response
    /// file body (always UTF-8 per the toolchain contract -- both MSVC
    /// and Clang parse response files as UTF-8 by default).
    /// </remarks>
    /// <param name="destinationPath">Absolute path to write to.</param>
    /// <param name="text">The text to write.</param>
    public static void AtomicWriteAllText(string destinationPath, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        ArgumentNullException.ThrowIfNull(text);
        AtomicWriteAllBytes(destinationPath, System.Text.Encoding.UTF8.GetBytes(text));
    }
}
