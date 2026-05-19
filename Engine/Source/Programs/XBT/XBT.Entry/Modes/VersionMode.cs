// Copyright Simgenics. All Rights Reserved.

using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.Entry;

/// <summary>
/// Prints diagnostic version metadata for XBT itself. Useful for bug
/// reports and IDE-side toolchain compatibility checks
/// (<c>MinimumToolchainVersion</c> per Toolchain Contract Rev 13
/// Section 9.1).
/// </summary>
[XBTMode("version")]
public sealed class VersionMode : IToolMode<VersionMode>
{
    public static string Name => "version";

    public static string Description => "Print XBT, .NET, BLAKE3, and contract version metadata.";

    public Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        _ = args;
        cancellationToken.ThrowIfCancellationRequestedWithDiagnostic("VersionMode.ExecuteAsync");

        Assembly entry = typeof(VersionMode).Assembly;
        string xbtVersion = entry.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                            ?? entry.GetName().Version?.ToString()
                            ?? "0.0.0";

        StringBuilder sb = new();
        sb.AppendLine($"XBT version:        {xbtVersion}");
        sb.AppendLine($"Contract version:   {ContractVersion.Current}");
        sb.AppendLine($".NET runtime:       {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"OS:                 {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"Process arch:       {RuntimeInformation.ProcessArchitecture.ToString().ToLower(CultureInfo.InvariantCulture)}");

        // BLAKE3 library version -- introspect the Blake3 assembly the
        // XBT.Core module pulled in. The package version is the
        // authoritative value; Assembly.GetName().Version reflects the
        // assembly version (typically 0.0.0.0 for cleanly-deterministic
        // libraries) so we prefer the InformationalVersion attribute.
        Assembly? blake3 = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Blake3");
        if (blake3 is not null)
        {
            string blake3Version = blake3.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                                   ?? blake3.GetName().Version?.ToString()
                                   ?? "unknown";
            sb.AppendLine($"Blake3 library:     {blake3Version}");
        }

        Logger.Info(sb.ToString().TrimEnd());
        return Task.FromResult(0);
    }
}
