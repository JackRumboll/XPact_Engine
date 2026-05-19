// Copyright Simgenics. All Rights Reserved.

using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.Entry;

/// <summary>
/// Build orchestration entry point. Stub at Phase 1; the action graph,
/// toolchain abstraction, descriptor evaluator, and the actual build
/// pipeline land in Phase 1.2 per the master plan + XBT.html.
/// </summary>
[XBTMode("build")]
public sealed class BuildMode : IToolMode<BuildMode>
{
    public static string Name => "build";

    public static string Description => "Discover, validate, manifest, and compile (Phase 1.2; stub at Phase 1).";

    public Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        _ = args;
        cancellationToken.ThrowIfCancellationRequestedWithDiagnostic("BuildMode.ExecuteAsync");

        Logger.Error(
            "BuildMode is not yet implemented (Phase 1 foundation; build orchestration arrives in Phase 1.2).",
            exitCode: 1);
        return Task.FromResult(1);
    }
}
