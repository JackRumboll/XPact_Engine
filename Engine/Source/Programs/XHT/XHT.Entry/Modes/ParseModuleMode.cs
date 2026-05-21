// Copyright Simgenics. All Rights Reserved.

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Manifest;

namespace Simgenics.XPact.XHT.Entry.Modes;

/// <summary>
/// Phase 1b stub of the <c>parse-module</c> mode per
/// <c>/Documents/XHT.html</c> Rev 5 Section 1.1.
/// </summary>
/// <remarks>
/// <para>
/// The Phase 1b stub validates the manifest + module surface (so the
/// CLI parser, manifest reader, and module-not-found exit code 50 path
/// are fully wired) and emits a 4-byte placeholder <c>&lt;Module&gt;.tokens.bin</c>
/// stub file via <see cref="AtomicFile.WriteAllBytes"/>. Phase 1c replaces
/// the placeholder with the real token-stream Brotli-compressed AST
/// cache.
/// </para>
/// </remarks>
[XhtMode("parse-module")]
public sealed class ParseModuleMode : IToolMode
{
    /// <summary>
    /// 4-byte placeholder content for the Phase 1b stub
    /// <c>tokens.bin</c>. The format slot is reserved for the real
    /// header magic <c>0x58 0x48 0x54 0x42</c> ("XHTB" -- XHT tokens
    /// binary) that Phase 1c writes to identify the file format on
    /// reload.
    /// </summary>
    private static readonly byte[] s_placeholderTokensBin = new byte[] { 0x58, 0x48, 0x54, 0x42 };

    /// <inheritdoc />
    public string Name => "parse-module";

    /// <inheritdoc />
    public string Description =>
        "Parse one module's headers + .cs files; write the AST cache (tokens.bin). Phase 1b stub.";

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

        // Read manifest -- throws ManifestMalformedException on any
        // schema / format failure; Program.cs catches and maps to 50.
        XbtManifest manifest = XbtManifestReader.Read(opts.ManifestPath);

        XbtModule? module = XbtManifestReader.FindModule(manifest, opts.ModuleName);
        if (module is null)
        {
            // Per XHT.html Rev 5 Section 1.3 X-CR1 remap: module-not-in-
            // manifest is a manifest-coherence concern and exits 50, not
            // XBT's RulesCompileFailed code 30.
            throw new ManifestMalformedException(
                $"Module '{opts.ModuleName}' not present in manifest '{opts.ManifestPath}'.");
        }

        ct.ThrowIfCancellationRequested();

        // Phase 1b stub: emit a placeholder tokens.bin via the atomic-
        // write path so the load-bearing AtomicFile.WriteAllBytes
        // surface gets exercised end-to-end via the CLI. Phase 1c
        // writes the real Brotli-compressed token stream here.
        Directory.CreateDirectory(opts.OutputDir);
        string tokensPath = Path.Combine(opts.OutputDir, $"{module.Name}.tokens.bin");
        AtomicFile.WriteAllBytes(tokensPath, s_placeholderTokensBin);

        Logger.Info(
            $"XHT parse-module: {module.Name} (Phase 1b stub - tokens.bin emitted as placeholder)");
        return ExitCodes.Success;
    }
}
