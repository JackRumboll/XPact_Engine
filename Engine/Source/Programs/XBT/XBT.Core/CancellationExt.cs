// Copyright Simgenics. All Rights Reserved.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Simgenics.XPact.XBT.Core;

/// <summary>
/// Extensions for <see cref="CancellationToken"/> consumption that propagate
/// the per-<c>/Documents/XBT.html</c> Section 20.3 cancellation discipline:
/// every async I/O respects the token, every check produces a diagnostic
/// that tells the operator where in the build the cancel landed.
/// </summary>
public static class CancellationExt
{
    /// <summary>
    /// Throws <see cref="OperationCanceledException"/> if cancellation has
    /// been requested, attaching a context message that names where in the
    /// build the check fired. Mirrors UBT's deliberate-act cancellation
    /// model but with mandatory context (UBT swallowed cancellation in
    /// unhelpful places; XBT's policy is "always tell the operator").
    /// </summary>
    public static void ThrowIfCancellationRequestedWithDiagnostic(
        this CancellationToken cancellationToken,
        string contextMessage)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            Logger.Info($"Cancellation observed: {contextMessage}");
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Wait for a task to complete with cancellation. If the cancellation
    /// fires before the task completes, the wait short-circuits with an
    /// <see cref="OperationCanceledException"/> but the underlying task
    /// is left to drain (caller decides whether to await its completion
    /// outside the cancellation path). A grace period -- if specified --
    /// is given to let the task finish cleanly before the wait abandons.
    /// </summary>
    public static async Task WaitWithCancellation(
        this Task task,
        CancellationToken cancellationToken,
        TimeSpan? gracePeriod = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (!cancellationToken.CanBeCanceled)
        {
            await task.ConfigureAwait(false);
            return;
        }

        TaskCompletionSource<bool> cancelTcs = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using (cancellationToken.Register(static state =>
        {
            ((TaskCompletionSource<bool>)state!).TrySetResult(true);
        }, cancelTcs))
        {
            Task completed = await Task.WhenAny(task, cancelTcs.Task).ConfigureAwait(false);
            if (completed == task)
            {
                await task.ConfigureAwait(false);
                return;
            }
        }

        // Cancellation fired before the task completed.
        if (gracePeriod is { } grace && grace > TimeSpan.Zero)
        {
            Task graceWait = Task.WhenAny(task, Task.Delay(grace, CancellationToken.None));
            await graceWait.ConfigureAwait(false);
            if (task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return;
            }
            Logger.Warning($"Task did not complete within grace period of {grace}; abandoning wait.");
        }

        cancellationToken.ThrowIfCancellationRequestedWithDiagnostic("WaitWithCancellation");
    }
}
