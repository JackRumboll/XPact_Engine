// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Analysis.Analyzers;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tiering;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp;

/// <summary>
/// The per-module emit context the XIL2CPP emit passes (Pass 6 / Pass 7) and
/// the body-lowering rules read, per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 3.2. It bundles the immutable per-module inputs -- the Pass-2
/// <see cref="NormalizedUnit"/> (which wraps the still-authoritative Pass-1
/// binding info), the Pass-3 <see cref="Pass3Result"/>, the Pass-4
/// <see cref="TierTable"/>, the Pass-5 <see cref="ManglingTable"/>, the
/// XHT cross-language correlation table, the contract-version tag, the
/// sim-path flag, and the ABI-tag content strings -- plus the lookup helpers
/// the emitters call (<see cref="GetSemanticModel"/>,
/// <see cref="FindMangling"/>, <see cref="FindTier"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Immutable + parallel-safe.</b> Every field is read-only and every
/// lookup is a pure function of the inputs, so one context can be shared
/// across the emit of every file in the module without synchronization. The
/// emitters never mutate it; each file's mutable accumulation lives in its own
/// <see cref="CppWriter"/>.
/// </para>
/// <para>
/// <b>ABI-tag content strings.</b> <see cref="GCRootABI"/> /
/// <see cref="ExceptionABI"/> / <see cref="ManglingScheme"/> are the manifest
/// envelope values (<c>"Span-based v1"</c> / <c>"Tier1-Shim/Tier2-Direct"</c>
/// / <c>"Itanium-LengthPrefixed-v1"</c>); <see cref="AbiPins.EmitPinBlock"/>
/// reads them so each <c>.cs.cpp</c>'s envelope pins assert against the
/// manifest the module was transpiled under.
/// </para>
/// </remarks>
public sealed class EmitContext
{
    /// <summary>
    /// Construct a per-module emit context.
    /// </summary>
    /// <param name="unit">The Pass-2 normalized unit (authoritative Pass-1 binding info via <see cref="NormalizedUnit.Pass1"/>). Must not be null.</param>
    /// <param name="pass3">The Pass-3 result (the XHT correlation table is read from its singleton slot). Must not be null.</param>
    /// <param name="tierTable">The Pass-4 tier table. Must not be null.</param>
    /// <param name="manglingTable">The Pass-5 mangling table. Must not be null.</param>
    /// <param name="contractVersionTag">The contract-version short tag (WITHOUT the leading <c>_v</c>). Must not be null / empty / whitespace.</param>
    /// <param name="gcRootAbi">The GC-root ABI envelope tag content (e.g. <c>"Span-based v1"</c>). Must not be null / empty / whitespace.</param>
    /// <param name="exceptionAbi">The exception ABI envelope tag content (e.g. <c>"Tier1-Shim/Tier2-Direct"</c>). Must not be null / empty / whitespace.</param>
    /// <param name="manglingScheme">The mangling-scheme ABI envelope tag content (e.g. <c>"Itanium-LengthPrefixed-v1"</c>). Must not be null / empty / whitespace.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="unit"/>, <paramref name="pass3"/>, <paramref name="tierTable"/>, or <paramref name="manglingTable"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="contractVersionTag"/>, <paramref name="gcRootAbi"/>, <paramref name="exceptionAbi"/>, or <paramref name="manglingScheme"/> is null / empty / whitespace.</exception>
    public EmitContext(
        NormalizedUnit unit,
        Pass3Result pass3,
        TierTable tierTable,
        ManglingTable manglingTable,
        string contractVersionTag,
        string gcRootAbi,
        string exceptionAbi,
        string manglingScheme)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(pass3);
        ArgumentNullException.ThrowIfNull(tierTable);
        ArgumentNullException.ThrowIfNull(manglingTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractVersionTag);
        ArgumentException.ThrowIfNullOrWhiteSpace(gcRootAbi);
        ArgumentException.ThrowIfNullOrWhiteSpace(exceptionAbi);
        ArgumentException.ThrowIfNullOrWhiteSpace(manglingScheme);

        Unit = unit;
        Pass3 = pass3;
        TierTable = tierTable;
        ManglingTable = manglingTable;
        ContractVersionTag = contractVersionTag;
        GCRootABI = gcRootAbi;
        ExceptionABI = exceptionAbi;
        ManglingScheme = manglingScheme;
        XhtCorrelationTable = pass3.GetSingleton<XhtCorrelationTable>();
    }

    /// <summary>The Pass-2 normalized unit (authoritative Pass-1 binding info via <see cref="NormalizedUnit.Pass1"/>).</summary>
    public NormalizedUnit Unit { get; }

    /// <summary>The Pass-3 semantic-analysis result.</summary>
    public Pass3Result Pass3 { get; }

    /// <summary>The Pass-4 tier table.</summary>
    public TierTable TierTable { get; }

    /// <summary>The Pass-5 mangling table.</summary>
    public ManglingTable ManglingTable { get; }

    /// <summary>
    /// The XHT cross-language correlation table (which C# types XHT already
    /// produces FClass scaffolding for), read from the Pass-3 singleton slot.
    /// Null when the Pass-3 analyzer did not run / found no manifest.
    /// </summary>
    public XhtCorrelationTable? XhtCorrelationTable { get; }

    /// <summary>The contract-version short tag (WITHOUT the leading <c>_v</c>).</summary>
    public string ContractVersionTag { get; }

    /// <summary>The module name being emitted.</summary>
    public string ModuleName => Unit.Pass1.ModuleName;

    /// <summary>
    /// True iff the module is a SimPath-determinism module (the Pass-1
    /// <see cref="Pass1Result.IsSimPath"/> flag). The emitters gate the
    /// sim-path safe-point / NewObject guards on this.
    /// </summary>
    public bool IsSimPath => Unit.Pass1.IsSimPath;

    /// <summary>The GC-root ABI envelope tag content (e.g. <c>"Span-based v1"</c>).</summary>
    public string GCRootABI { get; }

    /// <summary>The exception ABI envelope tag content (e.g. <c>"Tier1-Shim/Tier2-Direct"</c>).</summary>
    public string ExceptionABI { get; }

    /// <summary>The mangling-scheme ABI envelope tag content (e.g. <c>"Itanium-LengthPrefixed-v1"</c>).</summary>
    public string ManglingScheme { get; }

    /// <summary>
    /// Get the semantic model for one of the module's parsed trees (delegates
    /// to the authoritative Pass-1 cached model).
    /// </summary>
    /// <param name="tree">A syntax tree from the module's parsed files. Must not be null.</param>
    /// <returns>The cached semantic model.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="tree"/> is null.</exception>
    public SemanticModel GetSemanticModel(SyntaxTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return Unit.Pass1.GetSemanticModel(tree);
    }

    /// <summary>
    /// Look up the Pass-5 mangling record for <paramref name="id"/>, or null
    /// when the module has no row for it.
    /// </summary>
    /// <param name="id">The stable id to look up.</param>
    /// <returns>The mangling record, or null.</returns>
    public ManglingRecord? FindMangling(StableId id) => ManglingTable.Find(id);

    /// <summary>
    /// Look up the Pass-4 tier for <paramref name="id"/>. Returns
    /// <see cref="FunctionTier.Tier1"/> (the conservative default) when the
    /// tier table has no row for the id, so a caller never accidentally treats
    /// an unknown function as the zero-overhead Tier 2.
    /// </summary>
    /// <param name="id">The stable id to look up.</param>
    /// <returns>The function tier (Tier 1 when not found).</returns>
    public FunctionTier FindTier(StableId id)
    {
        foreach (TierClassification c in TierTable.Classifications)
        {
            if (StringComparer.Ordinal.Equals(c.Id.Value, id.Value))
            {
                return c.Tier;
            }
        }
        return FunctionTier.Tier1;
    }
}
