// Copyright Simgenics. All Rights Reserved.

using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XIL2CPP.Core;

namespace Simgenics.XPact.XIL2CPP.Entry.Modes;

/// <summary>
/// Prints XIL2CPP's version banner (semver + contract version + .NET
/// runtime) per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 15. Useful
/// for CI dashboards and bug reports. Mirrors XHT.Entry's VersionMode.
/// </summary>
[XIL2CPPMode("version")]
public sealed class VersionMode : IToolMode
{
    /// <inheritdoc />
    public string Name => "version";

    /// <inheritdoc />
    public string Description => "Print XIL2CPP version, contract version, and .NET runtime metadata.";

    /// <inheritdoc />
    public Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        _ = args;
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(ExitCodes.Cancelled);
        }

        Logger.Info(Xil2CppVersion.GetVersionString().TrimEnd());
        return Task.FromResult(ExitCodes.Success);
    }
}
