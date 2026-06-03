// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Tiering;

namespace Simgenics.XPact.XIL2CPP.Emit.Output;

/// <summary>
/// XIL2CPP Pass 7 (output write) per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 3.2: atomically write every emitted <c>&lt;stem&gt;.cs.h</c> +
/// <c>&lt;stem&gt;.cs.cpp</c> pair plus the enriched
/// <c>TierTable.partial.&lt;Module&gt;.json</c> (the Pass-4 table joined to the
/// Pass-5 mangling table) into the intermediate output root, returning the
/// list of written paths.
/// </summary>
/// <remarks>
/// <para>
/// <b>Output layout.</b> The <c>.cs.h</c> / <c>.cs.cpp</c> files are written
/// under <c>&lt;intermediateRoot&gt;/Transpiled/&lt;stem&gt;</c> (the stem
/// derived from <see cref="EmitResult.SourceRelativePath"/>, preserving its
/// directory structure so two same-named files in different folders do not
/// collide), and the enriched tier table is written as
/// <c>&lt;intermediateRoot&gt;/TierTable.partial.&lt;Module&gt;.json</c>. The
/// caller supplies <c>intermediateRoot</c> already resolved to the per-module
/// intermediate directory (mirroring
/// <c>TranspileModuleMode.ResolveIntermediateRoot</c>).
/// </para>
/// <para>
/// <b>Atomic + deterministic.</b> Every file is written through
/// <see cref="AtomicFileWriter"/> (temp-then-rename, UTF-8 no-BOM, LF
/// newlines preserved). The emit results are written in their supplied order,
/// then the tier table, so the returned path list is deterministic for a
/// deterministic input order (the next-wave emitter produces the results in
/// canonical file order).
/// </para>
/// <para>
/// <b>Logging.</b> Progress + the per-module completion telemetry go through
/// the process-global <see cref="Logger"/> (the XIL2CPP logger is a static
/// class, so it is referenced directly rather than passed as an instance).
/// </para>
/// </remarks>
public static class Pass7Writer
{
    /// <summary>The <c>Transpiled/</c> sub-directory the <c>.cs.h</c> / <c>.cs.cpp</c> pairs are written under (Section 3.2 Pass 7 layout).</summary>
    public const string TranspiledSubdirectory = "Transpiled";

    /// <summary>The header output extension appended to a source stem.</summary>
    public const string HeaderExtension = ".cs.h";

    /// <summary>The source output extension appended to a source stem.</summary>
    public const string SourceExtension = ".cs.cpp";

    /// <summary>
    /// Write every emitted pair + the enriched tier table under
    /// <paramref name="intermediateRoot"/>, returning the absolute paths
    /// written (headers + sources in <paramref name="results"/> order, then the
    /// tier table).
    /// </summary>
    /// <param name="results">The emitted <c>.cs.h</c> / <c>.cs.cpp</c> pairs (in canonical order). Must not be null.</param>
    /// <param name="manglingTable">The Pass-5 mangling table (the <c>manglingV1</c> source for the enriched tier table). Must not be null.</param>
    /// <param name="tierTable">The Pass-4 tier table. Must not be null.</param>
    /// <param name="intermediateRoot">The per-module intermediate output root (already resolved). Must not be null / empty / whitespace.</param>
    /// <returns>The absolute paths written, in write order.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="results"/>, <paramref name="manglingTable"/>, or <paramref name="tierTable"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="intermediateRoot"/> is null / empty / whitespace.</exception>
    public static IReadOnlyList<string> WriteOutputs(
        IReadOnlyList<EmitResult> results,
        ManglingTable manglingTable,
        TierTable tierTable,
        string intermediateRoot)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(manglingTable);
        ArgumentNullException.ThrowIfNull(tierTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(intermediateRoot);

        string root = Path.GetFullPath(intermediateRoot);
        string transpiledDir = Path.Combine(root, TranspiledSubdirectory);

        List<string> written = new();

        foreach (EmitResult result in results)
        {
            string stem = DeriveStem(result.SourceRelativePath);

            string headerPath = Path.Combine(transpiledDir, stem + HeaderExtension);
            AtomicFileWriter.WriteAllText(headerPath, result.HeaderContent);
            written.Add(Path.GetFullPath(headerPath));

            string sourcePath = Path.Combine(transpiledDir, stem + SourceExtension);
            AtomicFileWriter.WriteAllText(sourcePath, result.SourceContent);
            written.Add(Path.GetFullPath(sourcePath));
        }

        // Enriched TierTable.partial.<Module>.json (Pass 4 + Pass 5 join).
        string tierTablePath = Path.Combine(
            root,
            string.Create(CultureInfo.InvariantCulture, $"TierTable.partial.{tierTable.ModuleName}.json"));
        byte[] tierTableBytes = EnrichedTierTableSerializer.SerializeToUtf8Bytes(tierTable, manglingTable);
        AtomicFileWriter.WriteAllBytes(tierTablePath, tierTableBytes);
        written.Add(Path.GetFullPath(tierTablePath));

        Logger.Info(string.Create(
            CultureInfo.InvariantCulture,
            $"XIL2CPP Pass 7: wrote {results.Count} transpiled pair(s) + the enriched tier table for module '{tierTable.ModuleName}' ({written.Count} files)."));

        return written;
    }

    /// <summary>
    /// Derive the output stem from a source-relative path: drop a trailing
    /// <c>.cs</c> extension (the <c>.cs.h</c> / <c>.cs.cpp</c> extensions are
    /// appended by the writer), normalise back-slashes to forward-slashes, and
    /// return the result with its directory structure preserved. A
    /// <c>Game/HealthPickup.cs</c> input yields the stem
    /// <c>Game/HealthPickup</c> (so the outputs are
    /// <c>Game/HealthPickup.cs.h</c> + <c>Game/HealthPickup.cs.cpp</c>).
    /// </summary>
    /// <param name="sourceRelativePath">The source-relative path. Must not be null / empty / whitespace.</param>
    /// <returns>The output stem (forward-slashed; no <c>.cs</c> extension).</returns>
    /// <exception cref="ArgumentException">If <paramref name="sourceRelativePath"/> is null / empty / whitespace.</exception>
    public static string DeriveStem(string sourceRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRelativePath);

        string normalized = sourceRelativePath.Replace('\\', '/');
        if (normalized.EndsWith(".cs", StringComparison.Ordinal))
        {
            normalized = normalized[..^3];
        }
        return normalized;
    }
}
