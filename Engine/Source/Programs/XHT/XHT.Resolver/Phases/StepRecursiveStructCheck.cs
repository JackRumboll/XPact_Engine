// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Text;
using Simgenics.XPact.XHT.AST;

namespace Simgenics.XPact.XHT.Resolver.Phases;

/// <summary>
/// Phase 4 (<see cref="ResolvePhase.RecursiveStructCheck"/>): detect
/// cycles in the inheritance graph via a <c>TopologicalStructVisit</c>
/// walk over <see cref="ResolverContext.ResolvedSupers"/>. Mirrors UHT's
/// <c>UhtSession.cs:2880-2932</c> per <c>/Documents/XHT.html</c> Rev 5
/// Section 5.5.
/// </summary>
/// <remarks>
/// <para>
/// <b>Algorithm.</b> For each struct (and each class, per UHT precedent
/// -- the cycle check walks any <see cref="XhtTypeBase"/> with a
/// populated <c>Super</c>), walk the chain using
/// <see cref="ResolverContext.ResolvedSupers"/> and record every node
/// visited in a depth-first traversal. A revisit of a node already on
/// the current chain is a cycle: emit
/// <see cref="DiagnosticCodes.RecursiveStructCycle"/> (XHT105) naming
/// the offending chain.
/// </para>
/// <para>
/// <b>Scope.</b> Phase 1d covers the Super-chain case (the load-bearing
/// UE-precedent path at <c>UhtSession.cs:2880-2932</c>). The
/// field-types-also-walked case (UHT does this too) is deferred to a
/// later phase pass where property-type resolution feeds back into the
/// graph; the current pipeline runs RecursiveStructCheck before
/// Properties, so we only see the Super graph at this point.
/// </para>
/// </remarks>
internal sealed class StepRecursiveStructCheck : IResolverStep
{
    /// <inheritdoc />
    public void Execute(ResolverContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        IReadOnlyList<XhtTypeBase> ordered = ResolverPipeline.OrderedTypesSnapshot(ctx.Symbols);

        // Track which types have been confirmed cycle-free so we don't
        // re-emit diagnostics for chains that share a prefix.
        HashSet<XhtTypeBase> verified = new();

        // Track which types have already produced a cycle diagnostic
        // so the same cycle does not emit N times (once per chain
        // member). UHT emits one diagnostic per detected cycle root.
        HashSet<XhtTypeBase> diagnosedCycle = new();

        for (int i = 0; i < ordered.Count; i++)
        {
            XhtTypeBase t = ordered[i];

            // Only types with a chain are interesting. Classes and
            // structs both qualify; interfaces too but they rarely
            // self-cycle in practice.
            if (!ctx.ResolvedSupers.ContainsKey(t))
            {
                continue;
            }

            if (verified.Contains(t))
            {
                continue;
            }

            DetectCycle(ctx, t, verified, diagnosedCycle);
        }
    }

    private static void DetectCycle(
        ResolverContext ctx,
        XhtTypeBase start,
        HashSet<XhtTypeBase> verified,
        HashSet<XhtTypeBase> diagnosedCycle)
    {
        // Use an ordered list so the diagnostic can name the cycle.
        List<XhtTypeBase> chain = new();
        HashSet<XhtTypeBase> chainSet = new();

        XhtTypeBase current = start;
        chain.Add(current);
        chainSet.Add(current);

        while (ctx.ResolvedSupers.TryGetValue(current, out XhtTypeBase? super))
        {
            if (chainSet.Contains(super))
            {
                // Cycle detected. Build the names list including the
                // closing edge so the user sees the full path.
                chain.Add(super);
                if (!diagnosedCycle.Contains(super))
                {
                    StringBuilder sb = new();
                    int cycleStart = chain.IndexOf(super);
                    for (int j = cycleStart; j < chain.Count; j++)
                    {
                        if (j > cycleStart)
                        {
                            sb.Append(" -> ");
                        }
                        sb.Append(chain[j].FullyQualifiedName);
                    }

                    PhaseHelpers.Error(
                        ctx,
                        DiagnosticCodes.RecursiveStructCycle,
                        $"Inheritance cycle detected: {sb}",
                        start.Span);

                    // Mark every node in the cycle as diagnosed so a
                    // later starting point that re-walks the cycle
                    // does not re-emit.
                    for (int j = cycleStart; j < chain.Count; j++)
                    {
                        diagnosedCycle.Add(chain[j]);
                    }
                }
                return;
            }

            chain.Add(super);
            chainSet.Add(super);
            current = super;
        }

        // Walked the chain to the root without cycling. Mark every
        // node verified.
        foreach (XhtTypeBase t in chain)
        {
            verified.Add(t);
        }
    }
}
