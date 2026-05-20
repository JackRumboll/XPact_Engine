// Copyright Simgenics. All Rights Reserved.

using System;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.Entry;

/// <summary>
/// Phase 2 wrapper mode for XHT (XPact Header Tool) per
/// <c>/Documents/XBT.html</c> Section 1.1 + Section 1.4. The actual XHT
/// binary lands in Phase 2; until then this mode exists so the CLI
/// surface is honest about the mode name and emits the canonical
/// "tool not found" exit code instead of an "unknown mode" error.
/// </summary>
/// <remarks>
/// <para>
/// Audit fix R4-M8: previously <c>xbt run-xht</c> resolved to the
/// "unknown mode" path (exit 10), which leaked an implementation detail
/// (the mode was unimplemented). The mode now registers under the
/// canonical name and returns exit 24 with a clear stderr message
/// pointing at the Phase 2 schedule. The exit code 24 has the mnemonic
/// <c>PluginNotFound</c> in <see cref="Manifest.ContractSurface.ExitCodes"/>
/// per Toolchain Contract Rev 13 Section 13.1; we reuse it for
/// "tool not found" because the Phase 1 surface is the closest fit
/// available -- both mean "a named entry point was requested but the
/// implementation is not present". When Phase 2 adds a dedicated
/// <c>ToolNotFound</c> code, this mode flips to that mnemonic.
/// </para>
/// </remarks>
[XBTMode("run-xht")]
public sealed class RunXHTMode : IToolMode<RunXHTMode>
{
    public static string Name => "run-xht";

    public static string Description =>
        "(Phase 2 wrapper) Invoke the XHT header tool. Currently returns exit 24 -- XHT lands in Phase 2.";

    /// <summary>
    /// The exit code this mode returns. Public so the smoke test can
    /// assert against a single source of truth; mirrors
    /// <see cref="Manifest.ContractSurface.ExitCodes"/> code 24
    /// ("PluginNotFound", reused here for "tool not found" pending a
    /// dedicated Phase 2 mnemonic).
    /// </summary>
    public const int ToolNotFoundExitCode = 24;

    public Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        _ = args;
        cancellationToken.ThrowIfCancellationRequestedWithDiagnostic("RunXHTMode.ExecuteAsync");

        Logger.Error(
            "XHT not yet implemented (Phase 2). See /Documents/XToolchainContract.html " +
            "Section 0 for the Phase 2 schedule.",
            exitCode: ToolNotFoundExitCode,
            new DiagnosticContext { Action = "run-xht" });
        return Task.FromResult(ToolNotFoundExitCode);
    }
}
