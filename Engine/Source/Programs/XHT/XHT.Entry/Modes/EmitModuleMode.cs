// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Manifest;

namespace Simgenics.XPact.XHT.Entry.Modes;

/// <summary>
/// Phase 1b stub of the <c>emit-module</c> mode per
/// <c>/Documents/XHT.html</c> Rev 5 Section 1.1.
/// </summary>
/// <remarks>
/// <para>
/// The Phase 1b stub validates the manifest + module surface and writes
/// a minimal <c>&lt;Module&gt;.gen.manifest</c> via
/// <see cref="GenManifestWriter.Write"/>. This is the load-bearing
/// Phase 1b output -- it verifies the GenManifestWriter integration works
/// end-to-end via the CLI. Phase 1c+ adds the per-header <c>.gen.h</c>,
/// per-header <c>.gen.cpp</c>, and per-module <c>.init.gen.cpp</c>
/// outputs.
/// </para>
/// <para>
/// <b>Phase 1b input-hash discipline.</b> Per the brief, every
/// <c>[Inputs]</c> entry shares the same placeholder hash computed from
/// <c>IoHash.FromUtf8(Encoding.UTF8.GetBytes("phase1b"))</c>. Phase 1c
/// computes the real BLAKE3 prefix per Section 9.2.
/// </para>
/// </remarks>
[XhtMode("emit-module")]
public sealed class EmitModuleMode : IToolMode
{
    /// <inheritdoc />
    public string Name => "emit-module";

    /// <inheritdoc />
    public string Description =>
        "Emit per-header .gen.h, .gen.cpp, the module .init.gen.cpp + .gen.manifest. Phase 1b stub.";

    /// <inheritdoc />
    public Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        try
        {
            ModuleModeOptions opts = ModuleModeOptions.Parse(args, requireOutput: true);
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
            throw new ManifestMalformedException(
                $"Module '{opts.ModuleName}' not present in manifest '{opts.ManifestPath}'.");
        }

        ct.ThrowIfCancellationRequested();

        Directory.CreateDirectory(opts.OutputDir);

        // Build the [Inputs] section from the manifest's header + C#
        // source list. Phase 1b uses a single placeholder hash per the
        // brief; Phase 1c computes real BLAKE3 prefixes.
        string placeholderHex = IoHash.FromUtf8(Encoding.UTF8.GetBytes("phase1b")).Hex16();

        ImmutableArray<GenManifestEntry>.Builder inputsBuilder
            = ImmutableArray.CreateBuilder<GenManifestEntry>();
        foreach (XbtSourceFile sf in module.SourceFiles)
        {
            if (sf.IsHeader || sf.IsCSharp)
            {
                inputsBuilder.Add(new GenManifestEntry(sf.RelativePath, placeholderHex));
            }
        }
        // Pre-Phase-1 schema's PublicHeaders / PrivateHeaders /
        // InternalHeaders / CSharpSources are empty in Phase 1 per the
        // Addendum Section 10 item 5 caveat, but we walk them for
        // forward-compat in case a later manifest emitter populates them.
        foreach (string h in module.PublicHeaders)
        {
            inputsBuilder.Add(new GenManifestEntry(h, placeholderHex));
        }
        foreach (string h in module.PrivateHeaders)
        {
            inputsBuilder.Add(new GenManifestEntry(h, placeholderHex));
        }
        foreach (string h in module.InternalHeaders)
        {
            inputsBuilder.Add(new GenManifestEntry(h, placeholderHex));
        }
        foreach (string c in module.CSharpSources)
        {
            inputsBuilder.Add(new GenManifestEntry(c, placeholderHex));
        }

        GenManifest genManifest = new(
            XhtSchemaVersion: GenManifestWriter.CurrentSchemaVersion,
            ContractVersion: XhtVersion.ContractVersion,
            ModuleName: module.Name,
            GeneratedAtUtcIso: DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            Inputs: inputsBuilder.ToImmutable(),
            Generated: ImmutableArray<GenManifestEntry>.Empty,
            Diagnostics: ImmutableArray<GenManifestDiagnostic>.Empty);

        string genManifestPath = Path.Combine(opts.OutputDir, $"{module.Name}.gen.manifest");
        GenManifestWriter.Write(genManifest, genManifestPath);

        Logger.Info(
            $"XHT emit-module: {module.Name} (Phase 1b stub - .gen.manifest emitted; .gen.h/.gen.cpp deferred to Phase 1c)");
        return ExitCodes.Success;
    }
}
