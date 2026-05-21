// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Emitter;
using Simgenics.XPact.XHT.Manifest;
using Simgenics.XPact.XHT.Parser.Cpp;
using Simgenics.XPact.XHT.Parser.CSharp;
using Simgenics.XPact.XHT.Resolver;
using Simgenics.XPact.XHT.Tables;

namespace Simgenics.XPact.XHT.Entry.Modes;

/// <summary>
/// Phase 1e implementation of the <c>emit-module</c> mode per
/// <c>/Documents/XHT.html</c> Rev 8 Section 1.1 + Section 8.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pipeline.</b> The mode wires the full XHT-side flow:
/// </para>
/// <list type="number">
///   <item><description>
///     Read the XBT manifest (JSON; FBS sidecar is Phase 1c+).
///   </description></item>
///   <item><description>
///     Find the target module; missing -&gt; exit 50.
///   </description></item>
///   <item><description>
///     Construct a <see cref="SymbolTable"/> and
///     <see cref="SpecifierRegistry"/>.
///   </description></item>
///   <item><description>
///     Parse every header (<see cref="CppMarkerScanner"/>) and every C#
///     source (<see cref="CSharpMarkerWalker"/>) in the module, populating
///     the symbol table.
///   </description></item>
///   <item><description>
///     Run the seven-phase <see cref="ResolverPipeline"/>.
///   </description></item>
///   <item><description>
///     Invoke <see cref="ModuleEmitter.EmitModule"/>; that writes the
///     per-header <c>.gen.h</c> + <c>.gen.cpp</c> pairs, the per-module
///     <c>.init.gen.cpp</c> aggregator, and the per-module
///     <c>.gen.manifest</c>.
///   </description></item>
/// </list>
/// <para>
/// <b>Strict vs lenient mode.</b> The <c>-Strict=true|false</c> CLI flag
/// controls behaviour when a referenced source file is missing on disk.
/// Strict (the production default): exit 50 with diagnostic <c>XHT072</c>
/// (Required source file missing in strict mode) per /Documents/XHT.html
/// Rev 8 Section 12.3 emit-band catalog. Lenient (test default): emit an
/// <c>XHT070</c> warning per missing source and continue with an empty
/// AST for that file. Lenient mode is the path the Phase 1e test suite
/// uses against synthetic manifests where the referenced sources don't
/// physically exist.
/// </para>
/// <para>
/// <b>Round 5 R4-MA4.</b> The strict-mode-missing-source code was previously
/// emitted as <c>XHT050</c> on the <see cref="DiagnosticRecord"/> -- but
/// <c>XHT050</c> is an exit-code-shaped shim (see <see cref="ExitCodes"/>),
/// not a diagnostic-catalog entry. The dedicated emit-band diagnostic
/// <c>XHT072</c> distinguishes the missing-source-in-strict-mode case
/// from generic manifest-malformed exits and lets diagnostic-aggregating
/// CI tooling key on the source-side error rather than the operational
/// exit code.
/// </para>
/// </remarks>
[XhtMode("emit-module")]
public sealed class EmitModuleMode : IToolMode
{
    /// <inheritdoc />
    public string Name => "emit-module";

    /// <inheritdoc />
    public string Description =>
        "Emit per-header .gen.h, .gen.cpp, the module .init.gen.cpp + .gen.manifest. Phase 1e implementation.";

    /// <inheritdoc />
    public Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        try
        {
            EmitModuleOptions opts = EmitModuleOptions.Parse(args);
            return Task.FromResult(Run(opts, ct));
        }
        catch (CliArgumentException ex)
        {
            Logger.Error(ex.Message);
            return Task.FromResult(ExitCodes.CliArgumentError);
        }
    }

    private static int Run(EmitModuleOptions opts, CancellationToken ct)
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
            // Rev 8 Section 23.2. The X-CR1 remap (Rev 3) routed this to
            // exit 50 (manifest-coherence concern); the catalog-anchored
            // code carries through so the entry-point catch surfaces
            // "error XHT004: ..." instead of the generic XHT050 shim.
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.ModuleNotInManifest,
                message: $"Module '{opts.ModuleName}' not present in manifest '{opts.ManifestPath}'.");
        }

        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(opts.OutputDir);

        // Set up parse-time infrastructure.
        SymbolTable symbols = new();
        SpecifierRegistry registry = new(registerBuiltIns: true);
        List<DiagnosticRecord> diagnostics = new();
        // Accumulates C# partial-class duplicates across walker
        // invocations so the resolver can merge them in the Pairings
        // phase per C3 audit (XHT.html Section 3.3).
        List<XhtClass> extraPartials = new();

        // Parse C++ headers (in deterministic ordinal order).
        List<string> headerPaths = new();
        foreach (XbtSourceFile sf in module.SourceFiles)
        {
            if (sf.IsHeader)
            {
                headerPaths.Add(sf.RelativePath.Replace('\\', '/'));
            }
        }
        headerPaths.Sort(StringComparer.Ordinal);

        foreach (string headerRel in headerPaths)
        {
            ct.ThrowIfCancellationRequested();
            string absPath = ResolveAbsolutePath(manifest, module, headerRel);
            if (!File.Exists(absPath))
            {
                if (opts.Strict)
                {
                    Logger.EmitDiagnostic(new DiagnosticRecord(
                        DiagnosticSeverity.Error,
                        Code: DiagnosticCodes.EmitSourceMissing,
                        Message: $"Source header '{headerRel}' not found at '{absPath}'.",
                        File: headerRel,
                        Module: module.Name));
                    return ExitCodes.ManifestMalformed;
                }
                // Lenient: warn + continue with empty content.
                diagnostics.Add(new DiagnosticRecord(
                    DiagnosticSeverity.Warning,
                    Code: DiagnosticCodes.EmitSourceWarning,
                    Message: $"Source header '{headerRel}' not found on disk; emitting sentinel only.",
                    File: headerRel,
                    Module: module.Name));
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(absPath);
            }
            catch (IOException ex)
            {
                if (opts.Strict)
                {
                    Logger.EmitDiagnostic(new DiagnosticRecord(
                        DiagnosticSeverity.Error,
                        Code: DiagnosticCodes.EmitSourceMissing,
                        Message: $"Failed to read header '{headerRel}': {ex.Message}",
                        File: headerRel,
                        Module: module.Name));
                    return ExitCodes.ManifestMalformed;
                }
                diagnostics.Add(new DiagnosticRecord(
                    DiagnosticSeverity.Warning,
                    Code: DiagnosticCodes.EmitSourceWarning,
                    Message: $"Header '{headerRel}' read failed in lenient mode; skipping: {ex.Message}",
                    File: headerRel,
                    Module: module.Name));
                continue;
            }

            CppMarkerScanner scanner = new(absPath, text, module.Name, registry, symbols);
            scanner.Scan();
            foreach (DiagnosticRecord d in scanner.Diagnostics)
            {
                diagnostics.Add(d);
            }
        }

        // Parse C# sources (in deterministic ordinal order; Section 11.4).
        IReadOnlyList<string> csPaths = CSharpSourceEnumerator.EnumerateOrdered(module);
        foreach (string csRel in csPaths)
        {
            ct.ThrowIfCancellationRequested();
            string absPath = ResolveAbsolutePath(manifest, module, csRel);
            if (!File.Exists(absPath))
            {
                if (opts.Strict)
                {
                    Logger.EmitDiagnostic(new DiagnosticRecord(
                        DiagnosticSeverity.Error,
                        Code: DiagnosticCodes.EmitSourceMissing,
                        Message: $"Source file '{csRel}' not found at '{absPath}'.",
                        File: csRel,
                        Module: module.Name));
                    return ExitCodes.ManifestMalformed;
                }
                diagnostics.Add(new DiagnosticRecord(
                    DiagnosticSeverity.Warning,
                    Code: DiagnosticCodes.EmitSourceWarning,
                    Message: $"C# source '{csRel}' not found on disk; skipping.",
                    File: csRel,
                    Module: module.Name));
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(absPath);
            }
            catch (IOException ex)
            {
                if (opts.Strict)
                {
                    Logger.EmitDiagnostic(new DiagnosticRecord(
                        DiagnosticSeverity.Error,
                        Code: DiagnosticCodes.EmitSourceMissing,
                        Message: $"Failed to read C# source '{csRel}': {ex.Message}",
                        File: csRel,
                        Module: module.Name));
                    return ExitCodes.ManifestMalformed;
                }
                diagnostics.Add(new DiagnosticRecord(
                    DiagnosticSeverity.Warning,
                    Code: DiagnosticCodes.EmitSourceWarning,
                    Message: $"C# source '{csRel}' read failed in lenient mode; skipping: {ex.Message}",
                    File: csRel,
                    Module: module.Name));
                continue;
            }

            CSharpMarkerWalker walker = new(absPath, text, module.Name, registry, symbols);
            walker.Walk();
            foreach (DiagnosticRecord d in walker.Diagnostics)
            {
                diagnostics.Add(d);
            }
            // C3 audit: feed every C# partial-class duplicate to the
            // resolver so the pairings phase can merge them properly.
            extraPartials.AddRange(walker.ExtraPartials);
        }

        ct.ThrowIfCancellationRequested();

        // Resolver pipeline.
        ResolverPipeline pipeline = new(symbols, registry, manifest, module.Name);
        // Seed the resolver context with any partial-class duplicates
        // the walker(s) collected so the Pairings phase can merge them.
        pipeline.Context.ExtraPartials.AddRange(extraPartials);
        IReadOnlyList<DiagnosticRecord> resolverDiagnostics = pipeline.ResolveAll();
        foreach (DiagnosticRecord d in resolverDiagnostics)
        {
            diagnostics.Add(d);
        }

        ct.ThrowIfCancellationRequested();

        // Emit.
        EmitterContext emitCtx = new(
            ResolverContext: pipeline.Context,
            XbtManifest: manifest,
            Module: module,
            OutputDirectory: opts.OutputDir,
            Diagnostics: diagnostics);

        ModuleEmitter emitter = new(emitCtx);
        EmitResult result = emitter.EmitModule();

        // Surface any error-severity diagnostics on stderr / JSON channel
        // (the GenManifest.[Diagnostics] section already records them on
        // disk; this surfacing is for the human-visible side per
        // XHT.html Section 12.1).
        int errorCount = 0;
        foreach (DiagnosticRecord d in result.Diagnostics)
        {
            if (d.Severity == DiagnosticSeverity.Error)
            {
                errorCount++;
                Logger.EmitDiagnostic(d);
            }
        }

        if (errorCount > 0)
        {
            return ExitCodes.XhtInternalFailure;
        }

        Logger.Info(string.Format(
            CultureInfo.InvariantCulture,
            "XHT emit-module: {0} (generated {1} headers + {2} cpp + 1 init + 1 manifest)",
            module.Name,
            result.GeneratedHeaderFiles.Count,
            result.GeneratedCppFiles.Count));
        return ExitCodes.Success;
    }

    private static string ResolveAbsolutePath(XbtManifest manifest, XbtModule module, string relativePath)
    {
        string root = manifest.RootLocalPath ?? string.Empty;
        string baseDir = module.BaseDirectory ?? string.Empty;
        // Try module-relative first (the common case).
        if (!string.IsNullOrEmpty(root))
        {
            string moduleRelative = Path.Combine(root, baseDir, relativePath);
            if (File.Exists(moduleRelative))
            {
                return moduleRelative;
            }
            string repoRelative = Path.Combine(root, relativePath);
            if (File.Exists(repoRelative))
            {
                return repoRelative;
            }
            // Per M14 audit: when neither resolves, return a composite
            // path string that lists BOTH candidates so the upstream
            // diagnostic naming "not found at <path>" surfaces both
            // attempted locations. We use a single space separator so
            // the message reads as a sentence.
            return moduleRelative + " (also tried: " + repoRelative + ")";
        }
        return relativePath;
    }
}

/// <summary>
/// Parsed CLI options for the <c>emit-module</c> mode, including the
/// Phase 1e <c>-Strict=true|false</c> flag controlling missing-source
/// handling.
/// </summary>
/// <param name="ManifestPath">Absolute path to the XBT manifest JSON.</param>
/// <param name="ManifestBinPath">Absolute path to the FBS sidecar (optional).</param>
/// <param name="ModuleName">Module to emit.</param>
/// <param name="OutputDir">Output directory for the gen.* files.</param>
/// <param name="JsonFd">Streaming JSON channel FD (optional).</param>
/// <param name="NoMutexWait">True when <c>-NoMutexWait</c> is set.</param>
/// <param name="Strict">When true (production default), missing source files exit 50; when false, the emit pass tolerates missing files and emits sentinels.</param>
internal sealed record EmitModuleOptions(
    string ManifestPath,
    string? ManifestBinPath,
    string ModuleName,
    string OutputDir,
    int? JsonFd,
    bool NoMutexWait,
    bool Strict)
{
    /// <summary>
    /// Parse <paramref name="args"/> into a populated
    /// <see cref="EmitModuleOptions"/>. Accepts every flag the shared
    /// <see cref="ModuleModeOptions"/> accepts, plus
    /// <c>-Strict=true|false</c> (default <c>true</c>).
    /// </summary>
    /// <param name="args">CLI arguments.</param>
    /// <returns>Parsed options.</returns>
    /// <exception cref="CliArgumentException">On any missing required flag or malformed value.</exception>
    public static EmitModuleOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // Split the -Strict flag out of the args; pass the rest through
        // to the shared parser. Default Strict = true (production).
        bool strict = true;
        List<string> filtered = new(args.Length);
        foreach (string arg in args)
        {
            if (arg.StartsWith("-Strict=", StringComparison.OrdinalIgnoreCase))
            {
                string raw = arg["-Strict=".Length..];
                if (bool.TryParse(raw, out bool parsed))
                {
                    strict = parsed;
                    continue;
                }
                throw new CliArgumentException($"Invalid -Strict value '{raw}'. Expected true or false.");
            }
            filtered.Add(arg);
        }

        ModuleModeOptions inner = ModuleModeOptions.Parse(filtered.ToArray(), requireOutput: true);

        return new EmitModuleOptions(
            ManifestPath: inner.ManifestPath,
            ManifestBinPath: inner.ManifestBinPath,
            ModuleName: inner.ModuleName,
            OutputDir: inner.OutputDir,
            JsonFd: inner.JsonFd,
            NoMutexWait: inner.NoMutexWait,
            Strict: strict);
    }
}
