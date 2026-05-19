// Copyright Simgenics. All Rights Reserved.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.Entry;

/// <summary>
/// XBT process entry point. Reads the first positional argument as the
/// mode name, looks it up in <see cref="ToolModeRegistry"/>, dispatches
/// <see cref="IToolMode{TMode}.ExecuteAsync"/>, and returns the mode's
/// exit code per the surface in Toolchain Contract Rev 13 Section 13.
/// </summary>
/// <remarks>
/// <para>
/// Ctrl-C handling: <see cref="Console.CancelKeyPress"/> signals the
/// <see cref="CancellationTokenSource"/> threaded through every mode.
/// We set <c>e.Cancel = true</c> on the first Ctrl-C so the current
/// mode gets a chance to clean up; a second Ctrl-C lets the runtime
/// terminate the process unconditionally.
/// </para>
/// <para>
/// Exit codes:
/// <list type="bullet">
///   <item><description>0 -- success</description></item>
///   <item><description>1 -- generic failure (uncaught exception)</description></item>
///   <item><description>10 -- CLI argument error (mode missing / not found)</description></item>
///   <item><description>130 -- Ctrl-C (POSIX SIGINT convention)</description></item>
///   <item><description>(others delegated to the mode itself)</description></item>
/// </list>
/// </para>
/// </remarks>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Stopwatch sw = Stopwatch.StartNew();
        using CancellationTokenSource cts = new();
        int ctrlCCount = 0;

        Console.CancelKeyPress += (_, e) =>
        {
            int seen = Interlocked.Increment(ref ctrlCCount);
            if (seen == 1)
            {
                e.Cancel = true;
                Logger.Info("Cancellation requested (Ctrl-C). Press Ctrl-C again to force exit.");
                cts.Cancel();
            }
            else
            {
                // Second Ctrl-C: let the runtime terminate.
                e.Cancel = false;
            }
        };

        try
        {
            Logger.Info(
                $"XBT starting at {DateTime.UtcNow:O}; " +
                $"mode='{(args.Length > 0 ? args[0] : "(none)")}'; " +
                $"cwd='{Environment.CurrentDirectory}'.");

            if (args.Length == 0)
            {
                Logger.Error(
                    "No mode specified. Usage: XBT <mode> [arguments]. Try 'XBT help'.",
                    exitCode: 10);
                return 10;
            }

            string modeName = args[0];
            ToolModeRegistry.ModeInfo? info = ToolModeRegistry.TryGet(modeName);
            if (info is null)
            {
                Logger.Error(
                    $"Unknown mode '{modeName}'. Try 'XBT help' for the list.",
                    exitCode: 10);
                return 10;
            }

            string[] forwarded = new string[args.Length - 1];
            Array.Copy(args, 1, forwarded, 0, forwarded.Length);

            int exit = await info.Invoker(forwarded, cts.Token).ConfigureAwait(false);
            sw.Stop();
            Logger.Info(
                $"XBT '{modeName}' completed in {sw.Elapsed.TotalSeconds:F2}s with exit code {exit}.");
            return exit;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            Logger.Info($"XBT cancelled after {sw.Elapsed.TotalSeconds:F2}s.");
            return 130;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Logger.Error(
                $"Unhandled exception after {sw.Elapsed.TotalSeconds:F2}s: " +
                $"{ex.GetType().FullName}: {ex.Message}",
                exitCode: 1);
            // Stack trace on stderr for diagnostic capture.
            Logger.Error(ex.ToString());
            return 1;
        }
    }
}
