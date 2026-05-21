// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Text;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;

namespace Simgenics.XPact.XHT.Resolver.Phases;

/// <summary>
/// Phase 6 (<see cref="ResolvePhase.RecursiveStructCheck"/>; Rev 6 phase
/// order per Round-2 audit C3): detect cycles via depth-first walks
/// over BOTH the <c>Super</c> chain AND the value-typed-field-reference
/// graph. Mirrors UHT's <c>UhtSession.cs:2880-2932</c>
/// (<c>TopologicalStructVisit</c>) but extends to field types per
/// <c>/Documents/XHT.html</c> Rev 8 Section 5.5.
/// </summary>
/// <remarks>
/// <para>
/// <b>Algorithm.</b> For each reflected struct / class:
/// </para>
/// <list type="number">
///   <item><description>Walk the <c>Super</c> chain via
///   <see cref="ResolverContext.ResolvedSupers"/>. A revisit of a node
///   already on the current chain is a cycle.</description></item>
///   <item><description>For every reflected property whose resolved
///   type is itself a value-typed reflected type (i.e., a struct, not
///   a pointer/reference), walk into that struct's chain too. A revisit
///   is again a cycle.</description></item>
/// </list>
/// <para>
/// Pointer / reference properties (<c>Foo*</c>, <c>const Foo&amp;</c>)
/// do NOT create value-typed cycles -- the indirection breaks the cycle
/// at the byte level. Container properties (<c>TArray&lt;T&gt;</c>) also
/// don't create direct cycles because the container's storage is
/// heap-allocated. The walker tracks the <c>IsContainer</c> +
/// pointer/reference suffix on the property's <c>TypeIdentifier</c> and
/// SKIPS those edges, mirroring UHT's <c>UhtScriptStruct.cs</c>
/// recursion-check semantics.
/// </para>
/// <para>
/// <b>Why Rev 6 reordered.</b> Round-2 audit C3 surfaced that the
/// previous Rev 5 placement (between <c>BindSuperAndBases</c> and
/// <c>ResolveBases</c>) had no access to <c>ResolvedPropertyTypes</c>
/// -- the resolver hadn't yet looked up field types. Moving the phase
/// AFTER <c>Properties</c> gives the walker full access to the
/// resolved field-type graph.
/// </para>
/// </remarks>
internal sealed class StepRecursiveStructCheck : IResolverStep
{
    /// <inheritdoc />
    public void Execute(ResolverContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        IReadOnlyList<XhtTypeBase> ordered = ResolverPipeline.OrderedTypesSnapshot(ctx.Symbols);

        // Track which types have produced a cycle diagnostic so the same
        // cycle does not re-emit N times. UHT emits one diagnostic per
        // detected cycle root.
        HashSet<XhtTypeBase> diagnosedCycle = new();

        for (int i = 0; i < ordered.Count; i++)
        {
            XhtTypeBase t = ordered[i];

            // Only structs and classes participate in the value-typed
            // cycle check. Enums / interfaces / delegates are excluded.
            if (t is not XhtStruct && t is not XhtClass)
            {
                continue;
            }

            DetectCycleFrom(ctx, t, diagnosedCycle);
        }
    }

    /// <summary>
    /// Depth-first traversal from a single starting type. Walks both
    /// the <c>Super</c> chain and value-typed property references. The
    /// first cycle encountered emits XHT105 naming the offending path.
    /// </summary>
    private static void DetectCycleFrom(
        ResolverContext ctx,
        XhtTypeBase start,
        HashSet<XhtTypeBase> diagnosedCycle)
    {
        // chain: ordered list of types on the current traversal path.
        // chainSet: O(1) membership check for chain.
        // visited: pruning set so we don't re-walk a subtree we already
        //   explored (and already proved cycle-free) from a different
        //   starting point.
        List<XhtTypeBase> chain = new();
        HashSet<XhtTypeBase> chainSet = new();
        HashSet<XhtTypeBase> visited = new();

        if (Walk(ctx, start, chain, chainSet, visited, diagnosedCycle))
        {
            return;
        }
    }

    /// <summary>
    /// Visit one type. Returns true if a cycle has been emitted in this
    /// subtree (caller short-circuits further siblings, since UHT-style
    /// "one diagnostic per cycle" semantics are easier to honour when
    /// every node in the cycle is marked diagnosed up-front).
    /// </summary>
    private static bool Walk(
        ResolverContext ctx,
        XhtTypeBase current,
        List<XhtTypeBase> chain,
        HashSet<XhtTypeBase> chainSet,
        HashSet<XhtTypeBase> visited,
        HashSet<XhtTypeBase> diagnosedCycle)
    {
        // If we already proved this type cycle-free from an earlier
        // starting point, prune.
        if (visited.Contains(current))
        {
            return false;
        }

        // Cycle: current is already on the path. Emit XHT105 naming the
        // sub-path from first occurrence through current.
        if (chainSet.Contains(current))
        {
            int cycleStart = chain.IndexOf(current);

            // Already-diagnosed cycles don't re-emit.
            bool alreadyDiagnosed = false;
            for (int j = cycleStart; j < chain.Count; j++)
            {
                if (diagnosedCycle.Contains(chain[j]))
                {
                    alreadyDiagnosed = true;
                    break;
                }
            }
            if (alreadyDiagnosed)
            {
                return true;
            }

            StringBuilder sb = new();
            for (int j = cycleStart; j < chain.Count; j++)
            {
                if (j > cycleStart) { sb.Append(" -> "); }
                sb.Append(chain[j].FullyQualifiedName);
            }
            // Close the cycle visually.
            sb.Append(" -> ");
            sb.Append(current.FullyQualifiedName);

            PhaseHelpers.Error(
                ctx,
                DiagnosticCodes.RecursiveStructCycle,
                $"Recursive cycle detected: {sb}",
                current.Span);

            for (int j = cycleStart; j < chain.Count; j++)
            {
                diagnosedCycle.Add(chain[j]);
            }
            return true;
        }

        // Push onto the chain.
        chain.Add(current);
        chainSet.Add(current);

        // Walk into the Super chain first (one super at most).
        if (ctx.ResolvedSupers.TryGetValue(current, out XhtTypeBase? super) && super is not null)
        {
            if (Walk(ctx, super, chain, chainSet, visited, diagnosedCycle))
            {
                // A cycle was emitted; unwind the chain (don't claim
                // visited for the current node since the diagnostic
                // already fired).
                chain.RemoveAt(chain.Count - 1);
                chainSet.Remove(current);
                return true;
            }
        }

        // Walk into value-typed property references. Per Round-2 audit
        // C3: container / pointer / reference properties don't create
        // value-typed cycles (the indirection breaks the byte-level
        // recursion); only direct value-typed field references count.
        IReadOnlyList<XhtProperty>? props = current switch
        {
            XhtStruct s => s.Properties,
            XhtClass c => c.Properties,
            _ => null,
        };

        if (props is not null)
        {
            foreach (XhtProperty p in props)
            {
                // Skip container properties: TArray<T> / TMap<K,V> /
                // TSet<T> store their elements indirectly. The
                // container's own footprint doesn't include T's bytes.
                if (p.IsContainer)
                {
                    continue;
                }

                // Skip pointer / reference properties: "Foo*", "Foo&",
                // "const Foo&", "Foo const&". The indirection means
                // the property's storage is a pointer-sized slot, not
                // T's bytes.
                if (IsIndirectTypeIdentifier(p.TypeIdentifier))
                {
                    continue;
                }

                if (!ctx.ResolvedPropertyTypes.TryGetValue(p, out XhtTypeBase? referent))
                {
                    // Primitive or unresolved -- no cycle edge.
                    continue;
                }

                // Only struct / class referents can participate in a
                // value-typed cycle.
                if (referent is not XhtStruct && referent is not XhtClass)
                {
                    continue;
                }

                if (Walk(ctx, referent, chain, chainSet, visited, diagnosedCycle))
                {
                    chain.RemoveAt(chain.Count - 1);
                    chainSet.Remove(current);
                    return true;
                }
            }
        }

        // Subtree clean: mark visited so future starts prune. Then
        // unwind.
        visited.Add(current);
        chain.RemoveAt(chain.Count - 1);
        chainSet.Remove(current);
        return false;
    }

    /// <summary>
    /// Returns true when the supplied type identifier looks like a
    /// pointer or reference (so the field stores indirection, not
    /// value bytes). Conservative: any trailing <c>*</c> or <c>&amp;</c>
    /// in the raw <see cref="XhtProperty.TypeIdentifier"/> qualifies.
    /// </summary>
    private static bool IsIndirectTypeIdentifier(string? typeIdentifier)
    {
        if (string.IsNullOrEmpty(typeIdentifier))
        {
            return false;
        }
        string trimmed = typeIdentifier.TrimEnd();
        if (trimmed.Length == 0)
        {
            return false;
        }
        char last = trimmed[^1];
        return last == '*' || last == '&';
    }
}
