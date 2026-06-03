// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XIL2CPP.Core;

namespace Simgenics.XPact.XIL2CPP.Entry.Modes;

/// <summary>
/// Lists every registered XIL2CPP mode with a one-line description per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 15. Always returns
/// <see cref="ExitCodes.Success"/>; defensive code path so the operator
/// can recover from a malformed CLI even when other modes are broken.
/// Mirrors XHT.Entry's HelpMode.
/// </summary>
[XIL2CPPMode("help")]
public sealed class HelpMode : IToolMode
{
    /// <inheritdoc />
    public string Name => "help";

    /// <inheritdoc />
    public string Description => "List available XIL2CPP modes.";

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
        sb.AppendLine(CultureInfo.InvariantCulture, $"XIL2CPP (XPact IL-to-C++ transpiler) v{Xil2CppVersion.Semver}");
        sb.AppendLine();
        sb.AppendLine("Usage: xil2cpp <mode> [options]");
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
        sb.AppendLine("See /Documents/XIL2CPP.html for full documentation.");

        Logger.Info(sb.ToString().TrimEnd());
        return Task.FromResult(ExitCodes.Success);
    }
}
