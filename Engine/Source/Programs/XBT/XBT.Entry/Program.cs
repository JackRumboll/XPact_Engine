// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
/// terminate the process unconditionally. The first Ctrl-C emits a
/// structured info record carrying exit code 130 onto the streaming
/// JSON channel so IDE subscribers see the cancellation event in
/// real time (per <c>/Documents/XBT.html</c> Rev 4 Section 21.3).
/// </para>
/// <para>
/// CLI flags parsed at this layer (before mode dispatch):
/// <list type="bullet">
///   <item><description>
///     <c>-JsonFd=N</c> -- Linux/Android: open the JSON channel against
///     inherited FD <c>N</c> (default <c>3</c>). Value of <c>0</c>
///     disables the channel.
///   </description></item>
///   <item><description>
///     <c>-JsonStdout=true|false</c> -- Windows / Linux fallback: emit
///     JSON records on stdout prefixed with the <c>@@XBT-JSON@@ </c>
///     sentinel. Default: enabled on Windows when stdout is not a TTY
///     and no <c>-JsonFd</c> was supplied.
///   </description></item>
/// </list>
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

        // ---- CLI flag pre-pass (before mode dispatch) ------------------
        ChannelFlags channelFlags;
        string[] residualArgs;
        try
        {
            (channelFlags, residualArgs) = ParseChannelFlags(args);
        }
        catch (ArgumentException ex)
        {
            Logger.Error(ex.Message, exitCode: 10);
            return 10;
        }

        // ---- Streaming JSON channel configuration ----------------------
        ConfigureJsonChannel(channelFlags);

        Console.CancelKeyPress += (_, e) =>
        {
            int seen = Interlocked.Increment(ref ctrlCCount);
            if (seen == 1)
            {
                e.Cancel = true;
                Logger.Emit(new DiagnosticRecord
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Level     = DiagnosticLevel.Info,
                    Message   = "Cancellation requested (Ctrl-C). Press Ctrl-C again to force exit.",
                    Action    = "cancel",
                });
                cts.Cancel();
            }
            else
            {
                // Second Ctrl-C: let the runtime terminate. We do not
                // try to emit here -- the writer may already be torn
                // down and the runtime is about to kill the process.
                e.Cancel = false;
            }
        };

        try
        {
            Logger.Info(
                $"XBT starting at {DateTime.UtcNow:O}; " +
                $"mode='{(residualArgs.Length > 0 ? residualArgs[0] : "(none)")}'; " +
                $"cwd='{Environment.CurrentDirectory}'.");

            if (residualArgs.Length == 0)
            {
                Logger.Error(
                    "No mode specified. Usage: XBT <mode> [arguments]. Try 'XBT help'.",
                    exitCode: 10);
                return 10;
            }

            string modeName = residualArgs[0];
            ToolModeRegistry.ModeInfo? info = ToolModeRegistry.TryGet(modeName);
            if (info is null)
            {
                Logger.Error(
                    $"Unknown mode '{modeName}'. Try 'XBT help' for the list.",
                    exitCode: 10);
                return 10;
            }

            string[] forwarded = new string[residualArgs.Length - 1];
            Array.Copy(residualArgs, 1, forwarded, 0, forwarded.Length);

            int exit = await info.Invoker(forwarded, cts.Token).ConfigureAwait(false);
            sw.Stop();
            Logger.Info(
                $"XBT '{modeName}' completed in {sw.Elapsed.TotalSeconds:F2}s with exit code {exit}.");
            return exit;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            // Structured cancellation record carrying exit 130 so the
            // streaming JSON channel records the end-of-build event
            // per /Documents/XBT.html Rev 4 Section 21.3.
            Logger.Emit(new DiagnosticRecord
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level     = DiagnosticLevel.Error,
                Message   = $"cancelled after {sw.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)}s",
                Action    = "cancel",
                ExitCode  = 130,
            });
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

    // ---------------------------------------------------------------------
    // CLI parsing helpers (kept private; XBT.Entry is the sole owner of
    // the channel-flag surface per /Documents/XBT.html Rev 4 Section 1.4).
    // ---------------------------------------------------------------------

    private readonly record struct ChannelFlags(int? JsonFd, bool? JsonStdout);

    private static (ChannelFlags Flags, string[] Residual) ParseChannelFlags(string[] args)
    {
        int? jsonFd = null;
        bool? jsonStdout = null;
        List<string> residual = new(args.Length);

        foreach (string arg in args)
        {
            if (arg.StartsWith("-JsonFd=", StringComparison.OrdinalIgnoreCase))
            {
                string raw = arg["-JsonFd=".Length..];
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fd) || fd < 0)
                {
                    throw new ArgumentException(
                        $"Invalid -JsonFd value '{raw}'. Expected a non-negative integer (0 disables).");
                }
                jsonFd = fd;
            }
            else if (arg.StartsWith("-JsonStdout=", StringComparison.OrdinalIgnoreCase))
            {
                string raw = arg["-JsonStdout=".Length..];
                if (!bool.TryParse(raw, out bool value))
                {
                    throw new ArgumentException(
                        $"Invalid -JsonStdout value '{raw}'. Expected 'true' or 'false'.");
                }
                jsonStdout = value;
            }
            else
            {
                residual.Add(arg);
            }
        }

        return (new ChannelFlags(jsonFd, jsonStdout), residual.ToArray());
    }

    private static void ConfigureJsonChannel(ChannelFlags flags)
    {
        // Precedence per /Documents/XBT.html Rev 4 Section 21.2:
        //   1. -JsonFd=N takes priority on Linux/Android (no-op on Windows).
        //   2. -JsonStdout=true|false overrides the default on either OS.
        //   3. Default on Windows: enabled when stdout is not a TTY and
        //      no FD was provided -- avoids polluting interactive shells.
        //   4. Default on Linux/Android with no FD and no -JsonStdout: channel
        //      stays disabled (FD path is the canonical wiring).
        if (flags.JsonFd is int fd && fd > 0)
        {
            Logger.SetJsonChannelFromFd(fd);
        }

        if (flags.JsonStdout is bool stdout)
        {
            // Explicit override always wins. If FD was also set, this
            // replaces it; the launcher should not mix both modes.
            Logger.SetJsonChannelStdoutSentinel(stdout);
            return;
        }

        // No explicit override and no FD: apply the Windows-default rule.
        if (flags.JsonFd is null
            && OperatingSystem.IsWindows()
            && !Logger.IsJsonChannelEnabled)
        {
            bool stdoutIsRedirected = Console.IsOutputRedirected;
            if (stdoutIsRedirected)
            {
                Logger.SetJsonChannelStdoutSentinel(true);
            }
        }
    }
}
