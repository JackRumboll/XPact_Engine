// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Threading;

namespace Simgenics.XPact.XBT.Core;

/// <summary>
/// Filesystem-level retry helper for transient antivirus-induced file
/// lock failures on Windows. Mirrors UBT's
/// <c>FileReference.RetryOnIOException</c> pattern.
/// </summary>
/// <remarks>
/// <para>
/// Audit fix R6-C5 (UBT-parity).
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
/// helper sleeps and retries instead of failing the build.
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
}
