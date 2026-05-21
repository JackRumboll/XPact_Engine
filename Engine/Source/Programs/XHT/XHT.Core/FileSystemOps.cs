// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Threading;

namespace Simgenics.XPact.XHT.Core;

/// <summary>
/// Filesystem-level retry helper for transient antivirus-induced file
/// lock failures on Windows. Mirrors
/// <c>XBT.Core.FileSystemOps.RetryOnTransientIOException</c> and the
/// UBT precedent <c>FileReference.RetryOnIOException</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Windows Defender (and most third-party AV
/// products) hook every just-written file for a scanning window. While
/// the scan is in flight the file is opened with a sharing mode that
/// excludes <c>MoveFileEx</c> / <c>DeleteFileW</c>, so a tight write -&gt;
/// rename or write -&gt; delete sequence -- which the atomic-write pattern
/// in <see cref="AtomicFile"/> relies on -- raises
/// <see cref="IOException"/> (sharing violation) or
/// <see cref="UnauthorizedAccessException"/>. The transient lock typically
/// clears within tens of milliseconds; this helper sleeps and retries
/// instead of failing the build.
/// </para>
/// <para>
/// <b>Retry schedule.</b> Four attempts total with backoff
/// <c>[100ms, 200ms, 1000ms, 5000ms]</c>, totalling 6.3 s before surrender.
/// The schedule matches XBT.Core's so the two tools have a uniform
/// AV-retry profile.
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
    /// exponential) so the worst-case latency is bounded and predictable;
    /// the build's progress-reporting cadence stays sensible even when
    /// several files retry in series.
    /// </summary>
    private static readonly int[] s_backoffMs = new[] { 100, 200, 1000, 5000 };

    /// <summary>
    /// Run <paramref name="operation"/>; on transient
    /// <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>,
    /// retry on the schedule documented above. On the final attempt, the
    /// last exception is rethrown.
    /// </summary>
    /// <param name="operation">The filesystem operation to attempt. Must not be null.</param>
    /// <param name="operationDescription">
    /// Optional human-readable description for diagnostic messages. Not
    /// emitted by this method directly; callers in
    /// <see cref="AtomicFile"/> may attach the description to any
    /// exception they re-throw after the retry exhausts.
    /// </param>
    /// <exception cref="ArgumentNullException">If <paramref name="operation"/> is null.</exception>
    /// <exception cref="IOException">When every retry attempt fails with an <see cref="IOException"/>.</exception>
    /// <exception cref="UnauthorizedAccessException">When every retry attempt fails with an <see cref="UnauthorizedAccessException"/>.</exception>
    public static void RetryOnTransientIOException(Action operation, string? operationDescription = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _ = operationDescription; // reserved for future diagnostic enrichment

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
}
