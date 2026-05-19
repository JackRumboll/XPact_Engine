// Copyright Simgenics. All Rights Reserved.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.Entry;

/// <summary>
/// Emits a hard-coded JSON document listing the three default targets
/// and the five build configurations from the manifest enums. Smoke
/// test for the manifest types and a placeholder for the real
/// <c>QueryTargets</c> mode that the descriptor loader will power in
/// Phase 1.2 (<c>/Documents/XBT.html</c> Section 1.1).
/// </summary>
[XBTMode("query-targets")]
public sealed class QueryTargetsMode : IToolMode<QueryTargetsMode>
{
    public static string Name => "query-targets";

    public static string Description => "List default targets and build configurations as JSON.";

    public Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        _ = args;
        cancellationToken.ThrowIfCancellationRequestedWithDiagnostic("QueryTargetsMode.ExecuteAsync");

        QueryTargetsResult result = new(
            ContractVersion: ContractVersion.Current,
            Targets: Enum.GetNames<BuildTargetType>(),
            Configurations: Enum.GetNames<BuildConfiguration>(),
            Platforms: Enum.GetNames<Platform>(),
            StationRoles: Enum.GetNames<StationRole>(),
            SimdLevels: Enum.GetNames<SimdLevel>());

        JsonSerializerOptions opts = new()
        {
            WriteIndented = true,
        };
        string json = JsonSerializer.Serialize(result, opts);
        Logger.Info(json);
        return Task.FromResult(0);
    }

    private sealed record QueryTargetsResult(
        string ContractVersion,
        string[] Targets,
        string[] Configurations,
        string[] Platforms,
        string[] StationRoles,
        string[] SimdLevels);
}
