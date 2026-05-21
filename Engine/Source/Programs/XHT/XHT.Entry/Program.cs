// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Entry.Modes;
using Simgenics.XPact.XHT.Manifest;

namespace Simgenics.XPact.XHT.Entry;

/// <summary>
/// XHT process entry point. Parses the first positional argument as the
/// mode name, looks it up in <see cref="ToolModeRegistry"/>, dispatches
/// <see cref="IToolMode.ExecuteAsync"/>, and returns the mode's exit
/// code per <c>/Documents/XHT.html</c> Rev 7 Section 1.3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ctrl-C handling.</b> <see cref="Console.CancelKeyPress"/> signals
/// the <see cref="CancellationTokenSource"/> threaded through every
/// mode. The first Ctrl-C sets <c>e.Cancel = true</c> so the active
/// mode gets a chance to clean up; a second Ctrl-C lets the runtime
/// terminate the process unconditionally (mirrors XBT.Entry's
/// discipline per XBT.html Section 1.1).
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
/// <b>Exit codes (per Section 1.3):</b>
/// <list type="bullet">
///   <item><description>0 -- success.</description></item>
///   <item><description>10 -- CLI argument error (unknown mode / malformed flag).</description></item>
///   <item><description>50 -- manifest malformed (incl. module-not-in-manifest per X-CR1 remap).</description></item>
///   <item><description>62 -- XHT internal failure (uncaught exception).</description></item>
///   <item><description>130 -- cancelled by user (Ctrl-C).</description></item>
/// </list>
/// </para>
/// </remarks>
public static class Program
{
    /// <summary>
    /// Process entry point. Returns the exit code per Section 1.3 of
    /// <c>/Documents/XHT.html</c> Rev 7.
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

            // -JsonFd= activation per /Documents/XHT.html Rev 7 Section 1.4.
            // The Logger configures itself from the FD; we do not own the
            // FileStream's lifetime past the mode invocation (the OS
            // closes inherited FDs on process exit). Phase 1c may extend
            // this with -JsonStdout=true|false; Phase 1b matches the
            // FD-only surface XHT.html spec'd at Section 1.4.
            JsonChannelMode mode = Logger.DecideJsonChannelMode(pre.JsonFd ?? -1);
            // Phase 1b: we surface the channel decision via the public
            // API but do not open a writer here (no inherited FD wiring
            // surface ships yet -- Phase 1c). The decision call has the
            // side-effect of validating the mode flag for tests.
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
                // No positional argument: route to help (matches XBT's
                // discipline and the spec's "XHT.exe with no args
                // invokes help" note at Section 1.1).
                IToolMode helpMode = ResolveOrThrow("help");
                return await helpMode.ExecuteAsync(System.Array.Empty<string>(), cts.Token).ConfigureAwait(false);
            }

            string modeName = residualArgs[0];
            IToolMode? selected = ToolModeRegistry.Resolve(modeName);
            if (selected is null)
            {
                Logger.Error(
                    $"error XHT001: Unknown mode '{modeName}'. Try 'xht help' for the list.");
                return ExitCodes.CliArgumentError;
            }

            string[] forwarded = new string[residualArgs.Length - 1];
            Array.Copy(residualArgs, 1, forwarded, 0, forwarded.Length);

            return await selected.ExecuteAsync(forwarded, cts.Token).ConfigureAwait(false);
        }
        catch (CliArgumentException ex)
        {
            Logger.Error($"error XHT001: {ex.Message}");
            return ExitCodes.CliArgumentError;
        }
        catch (ManifestMalformedException ex)
        {
            // Surface the catalog-anchored diagnostic code carried on the
            // exception (e.g. XHT001 manifest-not-found, XHT002
            // ContractVersion mismatch, XHT003 verifier-limit violation,
            // XHT004 module-not-in-manifest, XHT005 .gen.manifest
            // CV mismatch -- the full catalog per /Documents/XHT.html
            // Rev 7 Section 23.2).
            //
            // Round 5 R4-CR1: every ManifestMalformedException throw site
            // in XHT.Manifest, XHT.Entry.Modes, etc. now carries a
            // DiagnosticCode. A null code here therefore indicates a
            // code-side bug (a new throw site was added without anchoring
            // it to a catalog entry), not a runtime user-input case --
            // surface it as an XHT900 internal compiler error so the
            // developer who introduced the un-anchored throw site fixes
            // it rather than the operator chasing a generic XHT050 ghost.
            // Exit code 50 (ManifestMalformed) per Section 1.3 still
            // applies because the underlying condition really is a
            // malformed-manifest case; only the diagnostic-code anchor
            // is missing.
            if (ex.DiagnosticCode is null)
            {
                Logger.Error(
                    $"error {DiagnosticCodes.InternalCompilerError}: internal compiler error -- "
                    + "ManifestMalformedException thrown without a "
                    + "DiagnosticCode anchor. This is a code-side bug; "
                    + "report at https://github.com/Simgenics/XPact-Engine/issues "
                    + $"with the message and stack trace below. Underlying message: {ex.Message}");
                Logger.Error(ex.ToString());
                return ExitCodes.ManifestMalformed;
            }
            Logger.Error($"error {ex.DiagnosticCode}: {ex.Message}");
            return ExitCodes.ManifestMalformed;
        }
        catch (OperationCanceledException)
        {
            Logger.Error("error XHT130: cancelled by user");
            return ExitCodes.Cancelled;
        }
        catch (Exception ex)
        {
            // Per Section 1.3: every other uncaught exception is an
            // internal failure (62). Emit the type + message in MSBuild
            // format and the stack trace as a follow-up error line for
            // diagnostic capture.
            Logger.Error(
                $"error XHT062: XHT internal failure: {ex.GetType().FullName}: {ex.Message}");
            Logger.Error(ex.ToString());
            return ExitCodes.XhtInternalFailure;
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
                $"Built-in XHT mode '{name}' is not registered; "
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
