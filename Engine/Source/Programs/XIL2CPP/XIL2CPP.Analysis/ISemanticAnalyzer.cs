// Copyright Simgenics. All Rights Reserved.

using Simgenics.XPact.XIL2CPP.Normalization;

namespace Simgenics.XPact.XIL2CPP.Analysis;

/// <summary>
/// A single Pass-3 semantic analyzer per <c>/Documents/XIL2CPP.html</c>
/// Rev 4 Section 3.2. Each analysis (e.g. the <c>XIL2CPP001</c>
/// XObject-new diagnostic, sim-path banned-API enumeration, reference-store
/// site enumeration, container-site enumeration, lambda-capture analysis,
/// async/iterator-site enumeration, the generic-instantiation closed walk,
/// reflection consumption, cross-module callee NoThrow lookup) is
/// implemented as one analyzer in its own file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Auto-discovery.</b> <see cref="Pass3Driver"/> reflection-discovers
/// every non-abstract <see cref="ISemanticAnalyzer"/> implementation in the
/// <c>XIL2CPP.Analysis</c> assembly that has a public parameterless
/// constructor, sorts them deterministically by <see cref="Name"/>, and
/// runs each against the shared <see cref="Pass3ResultBuilder"/>. To add an
/// analyzer a later agent simply declares a class implementing this
/// interface in the production assembly -- no registration edit, no driver
/// edit.
/// </para>
/// <para>
/// <b>Reads the annotation layer, keeps binding info valid.</b> An analyzer
/// reads the <see cref="NormalizedUnit"/> (the Pass-2 lowering annotations
/// plus the still-authoritative Pass-1 compilation / semantic models via
/// <see cref="NormalizedUnit.Pass1"/>), gathers metadata into its OWN result
/// type appended through <see cref="Pass3ResultBuilder.Add{T}(T)"/> /
/// <see cref="Pass3ResultBuilder.SetSingleton{T}(T)"/>, and surfaces
/// diagnostics through <see cref="Pass3ResultBuilder.AddDiagnostic"/>.
/// </para>
/// <para>
/// <b>Determinism.</b> An analyzer MUST visit nodes / symbols in a stable
/// order (source-declaration / span order) and MUST NOT depend on ambient
/// hash ordering, <c>DateTime</c>, or <c>Random</c> (gates
/// X-IL2CPP-MANGLE-DET / X-IL2CPP-CSPATH-DET, Section 9.9).
/// </para>
/// </remarks>
public interface ISemanticAnalyzer
{
    /// <summary>
    /// A stable, unique, human-readable name for this analyzer. It is the
    /// deterministic ordering key <see cref="Pass3Driver"/> sorts by
    /// (ordinal), so two builds run the analyzers in an identical order.
    /// Must be non-null / non-empty and unique across the assembly.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Analyze the normalized unit and write metadata + diagnostics into the
    /// shared <paramref name="builder"/>.
    /// </summary>
    /// <param name="unit">The Pass-2 normalized unit (lowering annotations + authoritative Pass-1 binding info). Must not be null.</param>
    /// <param name="builder">The shared, mutable Pass-3 result builder all analyzers write into. Must not be null.</param>
    void Analyze(NormalizedUnit unit, Pass3ResultBuilder builder);
}
