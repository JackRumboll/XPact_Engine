// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Manifest;
using Simgenics.XPact.XHT.Resolver.Phases;
using Simgenics.XPact.XHT.Tables;

namespace Simgenics.XPact.XHT.Resolver;

/// <summary>
/// Top-level orchestrator for the seven-active-phase resolve pipeline per
/// <c>/Documents/XHT.html</c> Rev 5 Section 5.1 + Section 5.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase ordering.</b> Phases are invoked in the strict Rev 4 sequence
/// (Section 5.2 ordered list): <c>Pairings -> InvalidCheck ->
/// BindSuperAndBases -> RecursiveStructCheck -> ResolveBases ->
/// Properties -> Final</c>. The orchestrator enforces ordering: a
/// caller cannot skip ahead or run a phase twice via the public surface
/// of this type.
/// </para>
/// <para>
/// <b>Parallelism (Section 11.2).</b> Phase 1d ships sequential. Phases
/// 2-6 are per-header parallel-friendly per Section 22.1 (6 of 7
/// parallel; Final serial). Per-phase parallelism is added later
/// without changing the public contract.
/// </para>
/// <para>
/// <b>Determinism.</b> All phases walk types via
/// <see cref="OrderedTypesSnapshot"/> which sorts by
/// <see cref="Language"/> then by
/// <see cref="XhtTypeBase.FullyQualifiedName"/> (ordinal) so diagnostic
/// emission ordering is reproducible across runs regardless of the
/// underlying <c>ConcurrentDictionary</c> enumeration order.
/// </para>
/// </remarks>
public sealed class ResolverPipeline
{
    private readonly ResolverContext _ctx;

    /// <summary>
    /// Construct a pipeline against the supplied populated symbol table.
    /// The diagnostics list is created internally and exposed via
    /// <see cref="ResolveAll"/> / <see cref="ResolveUpTo"/> return values.
    /// </summary>
    /// <param name="symbols">Populated symbol table from the parser pass; must not be null.</param>
    /// <param name="specifierRegistry">Specifier registry; must not be null.</param>
    /// <param name="manifest">XBT manifest; must not be null.</param>
    /// <param name="moduleName">The module being resolved; must not be null.</param>
    public ResolverPipeline(
        SymbolTable symbols,
        ISpecifierRegistry specifierRegistry,
        XbtManifest manifest,
        string moduleName)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(specifierRegistry);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(moduleName);

        List<DiagnosticRecord> diagnostics = new();
        _ctx = new ResolverContext(symbols, specifierRegistry, manifest, moduleName, diagnostics);
    }

    /// <summary>
    /// The mutable resolver context (read-only public surface). Tests
    /// + Phase 1e emitter inspect the populated maps via this property.
    /// </summary>
    public ResolverContext Context => _ctx;

    /// <summary>
    /// Run every phase in sequence. Returns the accumulated diagnostics
    /// list. The list is appended-to; callers may consume it as the
    /// full diagnostic stream from this resolve.
    /// </summary>
    /// <returns>The diagnostics produced across all seven phases.</returns>
    public IReadOnlyList<DiagnosticRecord> ResolveAll()
    {
        return ResolveUpTo(ResolvePhase.Final);
    }

    /// <summary>
    /// Run phases up to and including the supplied
    /// <paramref name="phase"/>. Phases run in the canonical order even
    /// when callers ask for a mid-pipeline stop. Convenient for unit
    /// tests that want to inspect the
    /// <see cref="ResolverContext"/> state after a specific phase.
    /// </summary>
    /// <param name="phase">The last phase to run. <see cref="ResolvePhase.None"/> is a no-op.</param>
    /// <returns>Accumulated diagnostics from the phases that ran.</returns>
    public IReadOnlyList<DiagnosticRecord> ResolveUpTo(ResolvePhase phase)
    {
        if (phase == ResolvePhase.None)
        {
            return _ctx.Diagnostics;
        }

        Run(ResolvePhase.Pairings, phase, new StepResolvePairings());
        Run(ResolvePhase.InvalidCheck, phase, new StepResolveInvalidCheck());
        Run(ResolvePhase.BindSuperAndBases, phase, new StepBindSuperAndBases());
        Run(ResolvePhase.RecursiveStructCheck, phase, new StepRecursiveStructCheck());
        Run(ResolvePhase.ResolveBases, phase, new StepResolveBases());
        Run(ResolvePhase.Properties, phase, new StepResolveProperties());
        Run(ResolvePhase.Final, phase, new StepResolveFinal());

        return _ctx.Diagnostics;
    }

    private void Run(ResolvePhase phaseToRun, ResolvePhase stopAt, IResolverStep step)
    {
        if (phaseToRun > stopAt)
        {
            return;
        }

        step.Execute(_ctx);
        _ctx.CurrentPhase = phaseToRun;
    }

    /// <summary>
    /// Walk <paramref name="symbols"/> in deterministic order
    /// (language ascending, fully-qualified name ordinal-ascending). The
    /// snapshot is materialised so the caller can iterate without
    /// holding a ConcurrentDictionary enumerator. Per Section 5.1
    /// determinism note + the brief's diagnostic-ordering rule.
    /// </summary>
    /// <param name="symbols">The symbol table to snapshot. Must not be null.</param>
    /// <returns>Deterministically-ordered snapshot of the table.</returns>
    public static IReadOnlyList<XhtTypeBase> OrderedTypesSnapshot(SymbolTable symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        List<XhtTypeBase> all = new();
        foreach (XhtTypeBase t in symbols.AllTypes)
        {
            all.Add(t);
        }

        all.Sort(static (a, b) =>
        {
            int byLang = ((int)a.Language).CompareTo((int)b.Language);
            if (byLang != 0)
            {
                return byLang;
            }
            return string.CompareOrdinal(a.FullyQualifiedName, b.FullyQualifiedName);
        });

        return all;
    }
}

/// <summary>
/// Common contract for one phase of the pipeline. Implementations are
/// stateless per-invocation; phase output flows through
/// <see cref="ResolverContext"/>.
/// </summary>
internal interface IResolverStep
{
    /// <summary>Run the phase against the supplied context.</summary>
    /// <param name="ctx">The resolver context. Must not be null.</param>
    void Execute(ResolverContext ctx);
}
