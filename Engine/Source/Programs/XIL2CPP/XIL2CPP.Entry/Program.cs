// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Entry.Modes;

namespace Simgenics.XPact.XIL2CPP.Entry;

/// <summary>
/// XIL2CPP process entry point. Parses the first positional argument as
/// the mode name, looks it up in <see cref="ToolModeRegistry"/>, dispatches
/// <see cref="IToolMode.ExecuteAsync"/>, and returns the mode's exit code
/// per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 15 (CLI surface) +
/// the Toolchain Contract Section 13.1 exit-code table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ctrl-C handling.</b> <see cref="Console.CancelKeyPress"/> signals
/// the <see cref="CancellationTokenSource"/> threaded through every
/// mode. The first Ctrl-C sets <c>e.Cancel = true</c> so the active
/// mode gets a chance to clean up; a second Ctrl-C lets the runtime
/// terminate the process unconditionally (mirrors XHT.Entry's
/// discipline).
/// </para>
/// <para>
/// <b>CLI shortcut flags (parsed before mode dispatch):</b>
/// <list type="bullet">
///   <item><description>
///     <c>--help</c> / <c>-h</c> -- routes to <see cref="HelpMode"/>
///     even when no positional argument is supplied.
///   </description></item>
///   <item><description>
///     <c>--version</c> / <c>-v</c> -- routes to <see cref="VersionMode"/>.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <b>Exit codes (Phase 6.a subset, per Contract Section 13.1):</b>
/// <list type="bullet">
///   <item><description>0 -- success.</description></item>
///   <item><description>10 -- CLI argument error (unknown mode / malformed flag).</description></item>
///   <item><description>63 -- XIL2CPP internal failure (uncaught exception, surfaced as an XIL2CPP900 ICE).</description></item>
///   <item><description>130 -- cancelled by user (Ctrl-C).</description></item>
/// </list>
/// The manifest-malformed (50) catch branch is added in a later sub-phase
/// once the XIL2CPP.Manifest types exist; Phase 6.a does not reference them.
/// </para>
/// </remarks>
public static class Program
{
    /// <summary>
    /// Process entry point. Returns the exit code per the Toolchain
    /// Contract Section 13.1 surface.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        using CancellationTokenSource cts = new();
        int ctrlCCount = 0;

        Console.CancelKeyPress += (_, e) =>
        {
            int seen = Interlocked.Increment(ref ctrlCCount);
            if (seen == 1)
            {
                e.Cancel = true;
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
            // Pre-pass for the --help / --version / -JsonFd shortcuts and
            // to strip them out of the args passed downstream.
            (PreFlags pre, string[] residualArgs) = ParsePreFlags(args);

            // -JsonFd= activation per /Documents/XIL2CPP.html Rev 4
            // Section 12. The Logger decides the channel mode from the FD;
            // we do not own the FileStream's lifetime past the mode
            // invocation (the OS closes inherited FDs on process exit).
            // Phase 6.a surfaces the channel decision via the public API
            // but does not open a writer here (no inherited FD wiring
            // surface ships yet -- a later sub-phase). The decision call
            // has the side-effect of validating the mode flag for tests.
            JsonChannelMode mode = Logger.DecideJsonChannelMode(pre.JsonFd ?? -1);
            _ = mode;

            // Help / version shortcut routing.
            if (pre.HelpShortcut)
            {
                IToolMode helpMode = ResolveOrThrow("help");
                return await helpMode.ExecuteAsync(residualArgs, cts.Token).ConfigureAwait(false);
            }
            if (pre.VersionShortcut)
            {
                IToolMode versionMode = ResolveOrThrow("version");
                return await versionMode.ExecuteAsync(residualArgs, cts.Token).ConfigureAwait(false);
            }

            if (residualArgs.Length == 0)
            {
                // No positional argument: route to help (matches XHT's
                // discipline -- "xil2cpp with no args invokes help").
                IToolMode helpMode = ResolveOrThrow("help");
                return await helpMode.ExecuteAsync(System.Array.Empty<string>(), cts.Token).ConfigureAwait(false);
            }

            string modeName = residualArgs[0];
            IToolMode? selected = ToolModeRegistry.Resolve(modeName);
            if (selected is null)
            {
                Logger.Error(
                    $"error {DiagnosticCodes.LoggerSentinel}: Unknown mode '{modeName}'. Try 'xil2cpp help' for the list.");
                return ExitCodes.CliArgumentError;
            }

            string[] forwarded = new string[residualArgs.Length - 1];
            Array.Copy(residualArgs, 1, forwarded, 0, forwarded.Length);

            return await selected.ExecuteAsync(forwarded, cts.Token).ConfigureAwait(false);
        }
        catch (CliArgumentException ex)
        {
            Logger.Error($"error {DiagnosticCodes.LoggerSentinel}: {ex.Message}");
            return ExitCodes.CliArgumentError;
        }
        catch (OperationCanceledException)
        {
            Logger.Error($"error {DiagnosticCodes.LoggerSentinel}: cancelled by user");
            return ExitCodes.Cancelled;
        }
        catch (Exception ex)
        {
            // Per Contract Section 13.1 every other uncaught exception is an
            // XIL2CPP internal failure (63). Surface it as an XIL2CPP900 ICE
            // so the developer who introduced the un-handled path fixes it
            // rather than the operator chasing a generic exit. Emit the
            // type + message in MSBuild format and the stack trace as a
            // follow-up error line for issue-tracker triage.
            Logger.Error(
                $"error {DiagnosticCodes.InternalCompilerError}: internal compiler error -- "
                + $"unhandled {ex.GetType().FullName}. This is an XIL2CPP-side bug; "
                + "report at https://github.com/Simgenics/XPact-Engine/issues "
                + $"with the message and stack trace below. Underlying message: {ex.Message}");
            Logger.Error(ex.ToString());
            return ExitCodes.Xil2CppInternalFailure;
        }
    }

    /// <summary>
    /// Resolve a built-in mode by name, throwing on failure. Used by the
    /// shortcut paths where the mode is statically expected to exist.
    /// </summary>
    private static IToolMode ResolveOrThrow(string name)
    {
        IToolMode? m = ToolModeRegistry.Resolve(name);
        if (m is null)
        {
            throw new InvalidOperationException(
                $"Built-in XIL2CPP mode '{name}' is not registered; "
                + "this indicates a build configuration error.");
        }
        return m;
    }

    /// <summary>
    /// Pre-pass CLI flags parsed before mode dispatch. Carries the
    /// optional <c>-JsonFd=</c> activation flag and the help / version
    /// shortcuts.
    /// </summary>
    private readonly record struct PreFlags(int? JsonFd, bool HelpShortcut, bool VersionShortcut);

    private static (PreFlags Flags, string[] Residual) ParsePreFlags(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        int? jsonFd = null;
        bool helpShortcut = false;
        bool versionShortcut = false;
        List<string> residual = new(args.Length);

        foreach (string arg in args)
        {
            if (arg.StartsWith("-JsonFd=", StringComparison.OrdinalIgnoreCase))
            {
                string raw = arg["-JsonFd=".Length..];
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fd) || fd < 0)
                {
                    throw new CliArgumentException(
                        $"Invalid -JsonFd value '{raw}'. Expected a non-negative integer (0 disables).");
                }
                jsonFd = fd;
            }
            else if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase)
                  || arg.Equals("-h", StringComparison.OrdinalIgnoreCase))
            {
                helpShortcut = true;
            }
            else if (arg.Equals("--version", StringComparison.OrdinalIgnoreCase)
                  || arg.Equals("-v", StringComparison.OrdinalIgnoreCase))
            {
                versionShortcut = true;
            }
            else
            {
                residual.Add(arg);
            }
        }

        return (new PreFlags(jsonFd, helpShortcut, versionShortcut), residual.ToArray());
    }
}
