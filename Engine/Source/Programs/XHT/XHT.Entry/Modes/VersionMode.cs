// Copyright Simgenics. All Rights Reserved.

using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XHT.Core;

namespace Simgenics.XPact.XHT.Entry.Modes;

/// <summary>
/// Prints XHT's version banner (semver + contract version + .NET
/// runtime) per <c>/Documents/XHT.html</c> Rev 5 Section 1.1. Useful for
/// CI dashboards and bug reports.
/// </summary>
[XhtMode("version")]
public sealed class VersionMode : IToolMode
{
    /// <inheritdoc />
    public string Name => "version";

    /// <inheritdoc />
    public string Description => "Print XHT version, contract version, and .NET runtime metadata.";

    /// <inheritdoc />
    public Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        _ = args;
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(ExitCodes.Cancelled);
        }

        Logger.Info(XhtVersion.GetVersionString().TrimEnd());
        return Task.FromResult(ExitCodes.Success);
    }
}
