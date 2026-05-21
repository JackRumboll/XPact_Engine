// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Manifest;
using Simgenics.XPact.XHT.Resolver;

namespace Simgenics.XPact.XHT.Emitter;

/// <summary>
/// Final outcome of <see cref="ModuleEmitter.EmitModule"/>: the absolute
/// paths of every file the emit pass produced for the module, plus the
/// diagnostics gathered along the way. Used by <c>XHT.Entry</c>'s
/// <c>emit-module</c> mode (Phase 1e+) for the exit-code decision and by
/// downstream XBT integration tests for the action-graph contract
/// (Contract Section 10.3 <c>XHTAction</c> produced-items).
/// </summary>
/// <param name="GeneratedHeaderFiles">Absolute paths of every <c>.gen.h</c> written.</param>
/// <param name="GeneratedCppFiles">Absolute paths of every <c>.gen.cpp</c> written.</param>
/// <param name="ModuleInitCppFile">Absolute path of the per-module <c>.init.gen.cpp</c> aggregator.</param>
/// <param name="GenManifestFile">Absolute path of the per-module <c>.gen.manifest</c>.</param>
/// <param name="Diagnostics">Final diagnostics list (resolver + emitter combined).</param>
public sealed record EmitResult(
    IReadOnlyList<string> GeneratedHeaderFiles,
    IReadOnlyList<string> GeneratedCppFiles,
    string ModuleInitCppFile,
    string GenManifestFile,
    IReadOnlyList<DiagnosticRecord> Diagnostics);

/// <summary>
/// Top-level orchestrator for per-module emit per
/// <c>/Documents/XHT.html</c> Rev 7 Section 8 + Section 9.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pipeline.</b> Per Section 8:
/// </para>
/// <list type="number">
///   <item><description>
///     Group reflected types by their source header path (
///     <see cref="SourceSpan.SourceFilePath"/>).
///   </description></item>
///   <item><description>
///     For each header (including the sentinel set: every header in the
///     module's <see cref="Manifest.XbtModule.SourceFiles"/> with
///     <see cref="XbtSourceFile.IsHeader"/> = true) invoke the
///     <see cref="HeaderEmitter"/> + <see cref="SourceEmitter"/>.
///   </description></item>
///   <item><description>
///     Invoke the <see cref="ModuleInitEmitter"/> for the per-module
///     aggregator.
///   </description></item>
///   <item><description>
///     Compose a <see cref="GenManifest"/> from inputs + generated +
///     diagnostics and write it via
///     <see cref="GenManifestWriter.Write"/>.
///   </description></item>
/// </list>
/// <para>
/// <b>Determinism (Section 14).</b> Headers are iterated in
/// <see cref="StringComparer.Ordinal"/> order. Within a header,
/// reflected types sort by
/// <see cref="XhtTypeBase.FullyQualifiedName"/> ordinal. The composed
/// manifest is itself sorted on render by
/// <see cref="GenManifestWriter"/>. Two clean runs over identical input
/// produce byte-identical output for every file.
/// </para>
/// </remarks>
public sealed class ModuleEmitter
{
    private readonly EmitterContext _context;

    /// <summary>
    /// Construct an orchestrator against the supplied context.
    /// </summary>
    /// <param name="context">Shared emitter context. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="context"/> is null.</exception>
    public ModuleEmitter(EmitterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <summary>
    /// Emit every artefact for the module.
    /// </summary>
    /// <returns>The <see cref="EmitResult"/> describing what was written + the merged diagnostics.</returns>
    public EmitResult EmitModule()
    {
        Directory.CreateDirectory(_context.OutputDirectory);

        // Round-2 audit C1: validate the manifest's mangling scheme before
        // any emit runs. An unsupported scheme produces a single XHT124
        // diagnostic up-front; the per-header / per-module emit then
        // short-circuits to an empty-result form so callers see a
        // diagnostic without an exception bubbling out.
        if (!string.Equals(
                _context.ManglingScheme,
                SymbolNaming.Phase1ManglingScheme,
                StringComparison.Ordinal))
        {
            _context.Diagnostics.Add(new DiagnosticRecord(
                Severity: DiagnosticSeverity.Error,
                Code: DiagnosticCodes.UnsupportedManglingScheme,
                Message: $"Manifest declares mangling scheme '{_context.ManglingScheme}' which this XHT build does not support (Phase 1 supports only '{SymbolNaming.Phase1ManglingScheme}'). Update XBT or rebuild XHT for the requested scheme.",
                File: null,
                Line: null,
                Column: null,
                Module: _context.Module.Name,
                Context: null));

            return new EmitResult(
                GeneratedHeaderFiles: Array.Empty<string>(),
                GeneratedCppFiles: Array.Empty<string>(),
                ModuleInitCppFile: string.Empty,
                GenManifestFile: string.Empty,
                Diagnostics: _context.Diagnostics.ToArray());
        }

        // Group reflected types by source header path. The "primary"
        // header set is the SourceFiles entries with IsHeader = true;
        // every such header gets a .gen.h + .gen.cpp pair (sentinel form
        // when it reflects no types per Section 8.4).
        IReadOnlyList<XhtTypeBase> orderedTypes
            = ResolverPipeline.OrderedTypesSnapshot(_context.ResolverContext.Symbols);

        // Filter to types declared in this module (the symbol table may
        // contain cross-module types if the caller seeded it that way;
        // we only emit per-source for the module-under-emit). Replace
        // any partial-merged canonical with its merged shape per C3
        // audit (XHT.html Section 3.3) so emit walks see the union.
        List<XhtTypeBase> moduleTypes = new();
        foreach (XhtTypeBase t in orderedTypes)
        {
            if (string.Equals(t.ModuleName, _context.Module.Name, StringComparison.Ordinal))
            {
                moduleTypes.Add(_context.ResolverContext.GetEffectiveShape(t));
            }
        }

        // Group module types by SourceFilePath; preserve ordinal order
        // of (path, FQN) so the emit walk is deterministic. For a
        // merged partial-class, the canonical (first-registered)
        // Span.SourceFilePath wins -- one .gen.h per canonical declaration.
        // The merged type's PartialSourcePaths is still emitted into
        // the .gen.manifest [Inputs] section so XBT can invalidate the
        // emit when ANY partial's source changes.
        Dictionary<string, List<XhtTypeBase>> typesByHeader = new(StringComparer.Ordinal);
        foreach (XhtTypeBase t in moduleTypes)
        {
            string headerPath = NormaliseToModuleRelative(t.Span.SourceFilePath);
            if (string.IsNullOrEmpty(headerPath))
            {
                // Synthetic types (SourceSpan.Synthetic) don't anchor to
                // a header; skip the per-header emit and let them flow
                // through the module aggregator only.
                continue;
            }
            if (!typesByHeader.TryGetValue(headerPath, out List<XhtTypeBase>? list))
            {
                list = new List<XhtTypeBase>();
                typesByHeader[headerPath] = list;
            }
            list.Add(t);
        }

        // Determine the sentinel header set: every IsHeader = true entry
        // in the manifest contributes a (sentinel-or-real) gen pair.
        List<string> sentinelHeaders = new();
        foreach (XbtSourceFile sf in _context.Module.SourceFiles)
        {
            if (sf.IsHeader)
            {
                sentinelHeaders.Add(sf.RelativePath.Replace('\\', '/'));
            }
        }
        sentinelHeaders.Sort(StringComparer.Ordinal);

        // Union the sentinel headers with any header that surfaced from
        // the AST (defensive: e.g. a test injecting types directly via
        // the symbol table without a manifest entry).
        SortedSet<string> unionHeaders = new(StringComparer.Ordinal);
        foreach (string h in sentinelHeaders) { unionHeaders.Add(h); }
        foreach (string h in typesByHeader.Keys) { unionHeaders.Add(h); }

        // Per-header emit.
        HeaderEmitter headerEmitter = new(_context);
        SourceEmitter sourceEmitter = new(_context);

        List<string> generatedHeaderFiles = new();
        List<string> generatedCppFiles = new();
        List<GenManifestEntry> generatedEntries = new();
        SortedSet<string> inputPaths = new(StringComparer.Ordinal);

        foreach (string headerPath in unionHeaders)
        {
            IReadOnlyList<XhtTypeBase> typesForHeader = typesByHeader.TryGetValue(headerPath, out List<XhtTypeBase>? l)
                ? l
                : Array.Empty<XhtTypeBase>();

            // Sort types for this header by FQN ordinal (Section 14.1).
            List<XhtTypeBase> sortedHeaderTypes = new(typesForHeader);
            sortedHeaderTypes.Sort(static (a, b) =>
                StringComparer.Ordinal.Compare(a.FullyQualifiedName, b.FullyQualifiedName));

            string genHeaderPath = headerEmitter.EmitForHeader(headerPath, sortedHeaderTypes);
            string genCppPath = sourceEmitter.EmitForHeader(headerPath, sortedHeaderTypes);

            generatedHeaderFiles.Add(genHeaderPath);
            generatedCppFiles.Add(genCppPath);

            // Per C6 audit: hash the raw bytes on disk (not the decoded
            // text) so the hash is symmetric with HashInputFile and
            // unaffected by BOM / line-ending normalisation.
            string genHeaderHash = HashOutputFile(genHeaderPath);
            string genCppHash = HashOutputFile(genCppPath);

            string genHeaderName = Path.GetFileName(genHeaderPath);
            string genCppName = Path.GetFileName(genCppPath);

            generatedEntries.Add(new GenManifestEntry(genHeaderName, genHeaderHash));
            generatedEntries.Add(new GenManifestEntry(genCppName, genCppHash));

            inputPaths.Add(headerPath);
        }

        // Per-module aggregator emit.
        ModuleInitEmitter initEmitter = new(_context);
        string initCppPath = initEmitter.EmitForModule(_context.Module.Name, moduleTypes);
        string initCppHash = HashOutputFile(initCppPath);
        string initCppName = Path.GetFileName(initCppPath);
        generatedEntries.Add(new GenManifestEntry(initCppName, initCppHash));

        // Inputs section also enumerates C# sources (every .cs in the
        // module contributes to the module aggregator's emit per
        // Section 11.4 partial-class ordering; though Phase 1e does not
        // yet attach .cs-derived types to per-header outputs, they DO
        // flow into the module aggregator so they're recorded as inputs).
        foreach (XbtSourceFile sf in _context.Module.SourceFiles)
        {
            if (sf.IsCSharp)
            {
                inputPaths.Add(sf.RelativePath.Replace('\\', '/'));
            }
        }
        // Also propagate any external CSharpSources list entries (Phase 1
        // schema empties this per Addendum item 5 but we read defensively).
        foreach (string cs in _context.Module.CSharpSources)
        {
            inputPaths.Add(cs.Replace('\\', '/'));
        }
        // Per C3 audit: include every partial-class contributing source
        // in the Inputs section so XBT's cache invalidates when ANY
        // partial changes (not just the canonical's source).
        foreach (XhtTypeBase t in moduleTypes)
        {
            if (t is XhtClass mergedCls && mergedCls.PartialSourcePaths is { Count: > 0 } paths)
            {
                foreach (string p in paths)
                {
                    string normalised = NormaliseToModuleRelative(p);
                    if (!string.IsNullOrEmpty(normalised))
                    {
                        inputPaths.Add(normalised);
                    }
                }
            }
        }

        List<GenManifestEntry> inputEntries = new(inputPaths.Count);
        foreach (string p in inputPaths)
        {
            string hash = HashInputFile(p);
            inputEntries.Add(new GenManifestEntry(p, hash));
        }

        // Compose + write the .gen.manifest.
        ImmutableArray<GenManifestDiagnostic> diagnosticsImm = ConvertDiagnostics(_context.Diagnostics);

        GenManifest manifest = new(
            XhtSchemaVersion: GenManifestWriter.CurrentSchemaVersion,
            ContractVersion: XhtVersion.ContractVersion,
            ModuleName: _context.Module.Name,
            GeneratedAtUtcIso: DateTimeStringForGenManifest(),
            Inputs: inputEntries.ToImmutableArray(),
            Generated: generatedEntries.ToImmutableArray(),
            Diagnostics: diagnosticsImm);

        string genManifestPath = Path.Combine(
            _context.OutputDirectory,
            DeriveGenManifestFileName(_context.Module));
        GenManifestWriter.Write(manifest, genManifestPath);

        return new EmitResult(
            GeneratedHeaderFiles: generatedHeaderFiles,
            GeneratedCppFiles: generatedCppFiles,
            ModuleInitCppFile: initCppPath,
            GenManifestFile: genManifestPath,
            Diagnostics: _context.Diagnostics.ToArray());
    }

    /// <summary>
    /// Filename for the per-module <c>.gen.manifest</c>. Reads
    /// <see cref="Manifest.XbtModule.GeneratedCPPFilenameBase"/> when set,
    /// falls back to the module name.
    /// </summary>
    /// <param name="module">The module entry from the manifest.</param>
    /// <returns>Just the filename, no directory components.</returns>
    public static string DeriveGenManifestFileName(XbtModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        string baseName = string.IsNullOrEmpty(module.GeneratedCPPFilenameBase)
            ? module.Name
            : module.GeneratedCPPFilenameBase;
        return baseName + ".gen.manifest";
    }

    private string NormaliseToModuleRelative(string sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            return string.Empty;
        }
        string normalized = sourcePath.Replace('\\', '/');

        // Strip the module's BaseDirectory prefix when present so the
        // grouping key matches the per-manifest IsHeader path form.
        // We accept either a prefix match (the source path is already
        // module-relative or repo-relative starting with the BaseDir)
        // OR an in-string match (the source path is absolute and the
        // BaseDir appears as a substring, the common XBT-subprocess
        // case).
        string baseDir = (_context.Module.BaseDirectory ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        if (baseDir.Length > 0)
        {
            string prefix = baseDir + "/";
            int idx = normalized.IndexOf(prefix, StringComparison.Ordinal);
            if (idx >= 0)
            {
                normalized = normalized[(idx + prefix.Length)..];
            }
        }
        // Also strip a leading slash if any.
        normalized = normalized.TrimStart('/');
        return normalized;
    }

    /// <summary>
    /// Hash a file XHT emitted by reading its raw bytes off disk. Per
    /// C6 audit (XHT.html Section 16): byte-symmetric with
    /// <see cref="HashInputFile"/> so BOM / line-ending normalisation
    /// cannot cause the two sides to disagree for byte-identical
    /// content.
    /// </summary>
    private static string HashOutputFile(string absPath)
    {
        byte[] bytes = File.ReadAllBytes(absPath);
        return IoHash.FromUtf8(bytes).Hex16();
    }

    private string HashInputFile(string relativePath)
    {
        // Phase 1e: per-input hash is the BLAKE3 of the byte content
        // when the file is present on disk; otherwise a deterministic
        // sentinel ("<missing>" hashed) so the manifest remains valid
        // for synthetic test cases where the manifest references files
        // that don't physically exist.
        string absPath = ResolveAbsoluteInputPath(relativePath);
        if (File.Exists(absPath))
        {
            // Per C6 audit: use ReadAllBytes (matches HashOutputFile)
            // so output-vs-input hash symmetry holds for byte-identical
            // content. FromUtf8 just hashes the bytes -- the name is
            // misleading; it does NOT decode UTF-8.
            byte[] bytes = File.ReadAllBytes(absPath);
            return IoHash.FromUtf8(bytes).Hex16();
        }
        // Deterministic sentinel: hash of the relative-path string.
        // Stable across runs; consumers can distinguish "missing input"
        // by comparing against this value if they care, but XBT's
        // primary signal is the [Diagnostics] section anyway.
        return IoHash.FromString("missing:" + relativePath).Hex16();
    }

    private string ResolveAbsoluteInputPath(string relativePath)
    {
        // The brief allows test-time lenient mode where source files
        // referenced in the manifest don't exist on disk. For the
        // production path we resolve via manifest.RootLocalPath /
        // module.BaseDirectory / relativePath.
        string root = _context.XbtManifest.RootLocalPath ?? string.Empty;
        string baseDir = _context.Module.BaseDirectory ?? string.Empty;

        // The relative path may already be module-relative (sentinel +
        // SourceFiles entries) or repo-relative; try the more-specific
        // form first.
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
        }
        return relativePath;
    }

    private static ImmutableArray<GenManifestDiagnostic> ConvertDiagnostics(IReadOnlyList<DiagnosticRecord> records)
    {
        if (records is null || records.Count == 0)
        {
            return ImmutableArray<GenManifestDiagnostic>.Empty;
        }
        ImmutableArray<GenManifestDiagnostic>.Builder builder
            = ImmutableArray.CreateBuilder<GenManifestDiagnostic>(records.Count);
        foreach (DiagnosticRecord r in records)
        {
            string severity = r.Severity switch
            {
                DiagnosticSeverity.Error => "error",
                DiagnosticSeverity.Warning => "warning",
                DiagnosticSeverity.Info => "info",
                _ => "info",
            };
            // Normalise the File path to forward slashes for manifest
            // determinism. Per M15 audit: messages are reversibly
            // escaped by the writer (GenManifestWriter.EscapeMessage
            // handles backslash, newline, and comma); the previous
            // destructive ',' -> ';' replacement has been removed.
            // File paths with embedded commas are unusual but legal --
            // if one shows up we forward-slash normalize and let the
            // writer's ValidateString surface the genuine "comma in
            // file path" error rather than silently mutating the data.
            string? file = r.File;
            if (file is not null)
            {
                file = file.Replace('\\', '/');
                // Defensive: replace commas with their URL-encoded form
                // %2C so the writer's no-comma rule still holds. This
                // is reversible (consumers can decode %2C), unlike the
                // previous destructive '_' substitution.
                if (file.Contains(','))
                {
                    file = file.Replace(",", "%2C");
                }
            }
            string message = r.Message ?? string.Empty;
            builder.Add(new GenManifestDiagnostic(
                Severity: severity,
                Code: r.Code,
                File: file,
                Line: r.Line,
                Column: r.Column,
                Message: message));
        }
        return builder.ToImmutable();
    }

    private static string DateTimeStringForGenManifest()
    {
        // Per XHT.html Section 9.2: the GeneratedAtUtc field is
        // informational; the ProducedAtUtcDeterministic = 0 line in the
        // manifest body is the byte-identical surface. For Phase 1e we
        // emit a deterministic sentinel ("1970-01-01T00:00:00Z") so the
        // entire manifest body is byte-identical across runs. Operators
        // wanting wall-clock timestamps can set an explicit override
        // via a future CLI flag.
        return "1970-01-01T00:00:00Z";
    }
}
