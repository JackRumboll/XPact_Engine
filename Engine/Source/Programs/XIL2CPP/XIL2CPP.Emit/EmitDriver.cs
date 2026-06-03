// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Emit.Output;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tiering;

namespace Simgenics.XPact.XIL2CPP.Emit;

/// <summary>
/// The top-level XIL2CPP emit driver (Pass 5 -&gt; Pass 6 -&gt; Pass 7) per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2: run Pass 5 (mangling) over
/// the normalized unit to build the <see cref="ManglingTable"/>, build the
/// per-module <see cref="EmitContext"/>, run Pass 6 (C++ emit) to produce the
/// per-file <see cref="EmitResult"/> set, then run Pass 7 (output write) to land
/// every <c>.cs.h</c> / <c>.cs.cpp</c> pair + the enriched tier table under the
/// intermediate root.
/// </summary>
/// <remarks>
/// <para>
/// <b>ABI envelope (Contract Phase-1 locked values).</b> The
/// <see cref="EmitContext"/>'s GC-root / exception / mangling-scheme envelope
/// tag content strings are the Contract-frozen Phase-1 values
/// (<see cref="GcRootAbi"/> / <see cref="ExceptionAbi"/> /
/// <see cref="ManglingScheme"/>). The manifest the module was transpiled under
/// carries the same three strings (validated by the manifest reader's
/// ContractVersion gate); the <see cref="AbiPins.EmitPinBlock"/> emits each as a
/// compile-time <c>static_assert</c> against the runtime macro, so a drift
/// between manifest + runtime fails the C++ compile rather than miscompiling.
/// Keeping the values as named constants here (rather than re-reading the
/// manifest) keeps the driver self-contained while the ContractVersion gate
/// guards against drift.
/// </para>
/// <para>
/// <b>Logging.</b> Progress + the per-module completion telemetry go through the
/// process-global <see cref="Logger"/> (a static class, referenced directly --
/// the same discipline <see cref="Pass7Writer"/> follows).
/// </para>
/// <para>
/// <b>Determinism.</b> Pass 5 / Pass 6 / Pass 7 are each deterministic; the
/// driver threads them with no ambient state, so two runs over the same inputs
/// produce byte-identical files (gate X-IL2CPP-CSPATH-DET).
/// </para>
/// </remarks>
public static class EmitDriver
{
    /// <summary>The Contract Phase-1 GC-root ABI envelope tag content.</summary>
    public const string GcRootAbi = "Span-based v1";

    /// <summary>The Contract Phase-1 exception ABI envelope tag content.</summary>
    public const string ExceptionAbi = "Tier1-Shim/Tier2-Direct";

    /// <summary>The Contract Phase-1 mangling-scheme ABI envelope tag content.</summary>
    public const string ManglingScheme = "Itanium-LengthPrefixed-v1";

    /// <summary>
    /// Run the emit driver (Pass 5 -&gt; Pass 6 -&gt; Pass 7) for one module,
    /// writing every <c>.cs.h</c> / <c>.cs.cpp</c> pair + the enriched tier
    /// table under <paramref name="intermediateRoot"/>.
    /// </summary>
    /// <param name="unit">The Pass-2 normalized unit (wraps the authoritative Pass-1 binding info). Must not be null.</param>
    /// <param name="pass3">The Pass-3 semantic-analysis result (the XHT correlation table is read from it). Must not be null.</param>
    /// <param name="tierTable">The Pass-4 tier table. Must not be null.</param>
    /// <param name="contractVersionTag">The contract-version short tag the mangle is computed under (WITHOUT the leading <c>_v</c>). Must not be null / empty / whitespace.</param>
    /// <param name="intermediateRoot">The per-module intermediate output root the writer lands the files under. Must not be null / empty / whitespace.</param>
    /// <returns>The driver result: the written-file list, the Pass-5 forward-commit diagnostics, and whether any error-severity diagnostic was collected.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="unit"/>, <paramref name="pass3"/>, or <paramref name="tierTable"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="contractVersionTag"/> or <paramref name="intermediateRoot"/> is null / empty / whitespace.</exception>
    public static EmitDriverResult Run(
        NormalizedUnit unit,
        Pass3Result pass3,
        TierTable tierTable,
        string contractVersionTag,
        string intermediateRoot)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(pass3);
        ArgumentNullException.ThrowIfNull(tierTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractVersionTag);
        ArgumentException.ThrowIfNullOrWhiteSpace(intermediateRoot);

        // Pass 5: mangling assignment (collect the forward-commit diagnostics).
        List<DiagnosticRecord> diagnostics = new();
        ManglingTable manglingTable = Pass5Driver.Run(unit, tierTable, contractVersionTag, diagnostics);

        // Build the per-module emit context.
        EmitContext context = new(
            unit,
            pass3,
            tierTable,
            manglingTable,
            contractVersionTag,
            GcRootAbi,
            ExceptionAbi,
            ManglingScheme);

        // Pass 6: C++ emit (one EmitResult per source file, canonical order).
        Pass6Driver pass6 = new();
        IReadOnlyList<EmitResult> results = pass6.Run(context);

        // Pass 7: output write (atomic; returns the written paths).
        IReadOnlyList<string> written = Pass7Writer.WriteOutputs(
            results, manglingTable, tierTable, intermediateRoot);

        bool hasErrors = false;
        foreach (DiagnosticRecord d in diagnostics)
        {
            if (d.Severity == DiagnosticSeverity.Error)
            {
                hasErrors = true;
                break;
            }
        }

        Logger.Info(string.Create(
            CultureInfo.InvariantCulture,
            $"XIL2CPP emit (Pass 5-7): module '{unit.Pass1.ModuleName}' emitted {results.Count} transpiled pair(s); wrote {written.Count} file(s)."));

        return new EmitDriverResult(written, diagnostics, hasErrors);
    }
}

/// <summary>
/// The product of <see cref="EmitDriver.Run"/>: the list of files the emit
/// passes wrote, the diagnostics collected during the run, and whether any of
/// them was error-severity.
/// </summary>
/// <param name="WrittenFiles">The absolute paths written (the Pass-7 <c>.cs.h</c> / <c>.cs.cpp</c> pairs + the enriched tier table, in write order).</param>
/// <param name="Diagnostics">The diagnostics collected during emit (the Pass-5 forward-commit mangle diagnostics).</param>
/// <param name="HasErrors">True iff any diagnostic in <see cref="Diagnostics"/> is error-severity.</param>
public sealed record EmitDriverResult(
    IReadOnlyList<string> WrittenFiles,
    IReadOnlyList<DiagnosticRecord> Diagnostics,
    bool HasErrors);
