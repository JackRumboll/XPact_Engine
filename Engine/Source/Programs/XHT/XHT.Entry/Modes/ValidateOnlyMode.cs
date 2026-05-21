// Copyright Simgenics. All Rights Reserved.

using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Manifest;

namespace Simgenics.XPact.XHT.Entry.Modes;

/// <summary>
/// Phase 1b stub of the <c>validate-only</c> mode per
/// <c>/Documents/XHT.html</c> Rev 8 Section 1.1.
/// </summary>
/// <remarks>
/// <para>
/// The Phase 1b stub validates the manifest + module surface (the
/// load-bearing surface for the CI pre-flight wrapper) but does not
/// emit anything. Phase 1c+ runs the resolver + validators per
/// Section 6.
/// </para>
/// </remarks>
[XhtMode("validate-only")]
public sealed class ValidateOnlyMode : IToolMode
{
    /// <inheritdoc />
    public string Name => "validate-only";

    /// <inheritdoc />
    public string Description =>
        "Parse + resolve + run validators; emit no output files. Phase 1b stub.";

    /// <inheritdoc />
    public Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        try
        {
            ModuleModeOptions opts = ModuleModeOptions.Parse(args, requireOutput: false);
            return Task.FromResult(Run(opts, ct));
        }
        catch (CliArgumentException ex)
        {
            Logger.Error(ex.Message);
            return Task.FromResult(ExitCodes.CliArgumentError);
        }
    }

    private static int Run(ModuleModeOptions opts, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return ExitCodes.Cancelled;
        }

        XbtManifest manifest = XbtManifestReader.Read(opts.ManifestPath);

        XbtModule? module = XbtManifestReader.FindModule(manifest, opts.ModuleName);
        if (module is null)
        {
            // XHT004 -- Module not in manifest per /Documents/XHT.html
            // Rev 8 Section 23.2 (X-CR1 remap to exit 50). The
            // catalog-anchored code carries through so the entry-point
            // catch surfaces "error XHT004: ..." rather than the
            // generic XHT050 shim.
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.ModuleNotInManifest,
                message: $"Module '{opts.ModuleName}' not present in manifest '{opts.ManifestPath}'.");
        }

        Logger.Info(
            $"XHT validate-only: {module.Name} (Phase 1b stub - validators land in Phase 1c+)");
        return ExitCodes.Success;
    }
}
