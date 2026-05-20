// Copyright Simgenics. All Rights Reserved.

using System;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.Entry;

/// <summary>
/// Phase 2 wrapper mode for XIL2CPP (XPact .NET-to-C++ transpiler) per
/// <c>/Documents/XBT.html</c> Section 1.4. The actual XIL2CPP binary
/// lands in Phase 2; until then this mode exists so the CLI surface is
/// honest about the mode name and emits the canonical "tool not found"
/// exit code instead of an "unknown mode" error.
/// </summary>
/// <remarks>
/// Audit fix R4-M8: see <see cref="RunXHTMode"/> for the rationale.
/// Same exit code surface and same "Phase 2" stderr message.
/// </remarks>
[XBTMode("run-xil2cpp")]
public sealed class RunXIL2CPPMode : IToolMode<RunXIL2CPPMode>
{
    public static string Name => "run-xil2cpp";

    public static string Description =>
        "(Phase 2 wrapper) Invoke the XIL2CPP transpiler. Currently returns exit 24 -- XIL2CPP lands in Phase 2.";

    /// <summary>The exit code this mode returns. See <see cref="RunXHTMode.ToolNotFoundExitCode"/>.</summary>
    public const int ToolNotFoundExitCode = 24;

    public Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        _ = args;
        cancellationToken.ThrowIfCancellationRequestedWithDiagnostic("RunXIL2CPPMode.ExecuteAsync");

        Logger.Error(
            "XIL2CPP not yet implemented (Phase 2). See /Documents/XToolchainContract.html " +
            "Section 0 for the Phase 2 schedule.",
            exitCode: ToolNotFoundExitCode,
            new DiagnosticContext { Action = "run-xil2cpp" });
        return Task.FromResult(ToolNotFoundExitCode);
    }
}
