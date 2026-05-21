// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XHT.Core;

namespace Simgenics.XPact.XHT.Entry.Modes;

/// <summary>
/// Lists every registered XHT mode with a one-line description per
/// <c>/Documents/XHT.html</c> Rev 5 Section 1.1. Always returns
/// <see cref="ExitCodes.Success"/>; defensive code path so the operator
/// can recover from a malformed CLI even when other modes are broken.
/// </summary>
[XhtMode("help")]
public sealed class HelpMode : IToolMode
{
    /// <inheritdoc />
    public string Name => "help";

    /// <inheritdoc />
    public string Description => "List available XHT modes.";

    /// <inheritdoc />
    public Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        _ = args;
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(ExitCodes.Cancelled);
        }

        IReadOnlyDictionary<string, IToolMode> modes = ToolModeRegistry.Build();
        StringBuilder sb = new();
        sb.AppendLine(CultureInfo.InvariantCulture, $"XHT (XPact Header Tool) v{XhtVersion.Semver}");
        sb.AppendLine();
        sb.AppendLine("Usage: xht <mode> [options]");
        sb.AppendLine();
        sb.AppendLine("Modes:");

        // Sort by name for stable output. Determinism here matters
        // because IDE wrappers may parse the help output looking for
        // mode names.
        IEnumerable<IToolMode> sorted = modes.Values
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase);
        foreach (IToolMode info in sorted)
        {
            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "  {0,-16} {1}",
                info.Name,
                info.Description));
        }

        sb.AppendLine();
        sb.AppendLine("See /Documents/XHT.html for full documentation.");

        Logger.Info(sb.ToString().TrimEnd());
        return Task.FromResult(ExitCodes.Success);
    }
}
