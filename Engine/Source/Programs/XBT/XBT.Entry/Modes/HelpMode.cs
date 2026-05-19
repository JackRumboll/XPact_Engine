// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.Entry;

/// <summary>
/// Lists every registered mode. Always returns exit 0; defensive code
/// path so the operator can recover from a malformed CLI even when
/// other modes are broken.
/// </summary>
[XBTMode("help")]
public sealed class HelpMode : IToolMode<HelpMode>
{
    public static string Name => "help";

    public static string Description => "List available XBT modes.";

    public Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        _ = args;
        cancellationToken.ThrowIfCancellationRequestedWithDiagnostic("HelpMode.ExecuteAsync");

        IReadOnlyDictionary<string, ToolModeRegistry.ModeInfo> modes = ToolModeRegistry.Build();
        StringBuilder sb = new();
        sb.AppendLine("XBT -- XPact Build Tool");
        sb.AppendLine($"Contract version: {Simgenics.XPact.XBT.Manifest.ContractVersion.Current}");
        sb.AppendLine();
        sb.AppendLine("Usage: XBT <mode> [arguments]");
        sb.AppendLine();
        sb.AppendLine("Modes:");

        // Sort by name for stable output. Determinism here matters
        // because IDE wrappers may parse the help output looking for
        // mode names.
        IEnumerable<ToolModeRegistry.ModeInfo> sorted = modes.Values
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase);
        foreach (ToolModeRegistry.ModeInfo info in sorted)
        {
            sb.AppendLine(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "  {0,-16} {1}",
                    info.Name,
                    info.Description));
        }

        Logger.Info(sb.ToString().TrimEnd());
        return Task.FromResult(0);
    }
}
