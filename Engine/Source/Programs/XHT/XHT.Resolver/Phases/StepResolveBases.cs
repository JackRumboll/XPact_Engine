// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Tables;

namespace Simgenics.XPact.XHT.Resolver.Phases;

/// <summary>
/// Phase 5 (<see cref="ResolvePhase.ResolveBases"/>): resolve interface
/// base lists for classes; resolve <c>Within=</c> outer-class pointers
/// per <c>/Documents/XHT.html</c> Rev 8 Section 5.1 + Section 7.4.
/// Validates <c>Within</c> compatibility against super's <c>Within</c>
/// per UHT's <c>SetAndValidateWithinClass</c> precedent (XHT119).
/// </summary>
internal sealed class StepResolveBases : IResolverStep
{
    /// <inheritdoc />
    public void Execute(ResolverContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        IReadOnlyList<XhtTypeBase> ordered = ResolverPipeline.OrderedTypesSnapshot(ctx.Symbols);

        // Pass 1: resolve interface lists + collect Within identifiers.
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i] is XhtClass cls)
            {
                ResolveInterfaceList(ctx, cls);
                ResolveWithin(ctx, cls);
            }
        }

        // Pass 2: validate Within compatibility against super's Within.
        // Requires every class's Within to be resolved first (pass 1).
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i] is XhtClass cls)
            {
                ValidateWithinAgainstSuper(ctx, cls);
            }
        }
    }

    private static void ResolveInterfaceList(ResolverContext ctx, XhtClass cls)
    {
        if (cls.InterfaceIdentifiers is null || cls.InterfaceIdentifiers.Count == 0)
        {
            return;
        }

        List<XhtInterface> resolved = new(cls.InterfaceIdentifiers.Count);
        for (int i = 0; i < cls.InterfaceIdentifiers.Count; i++)
        {
            string ident = cls.InterfaceIdentifiers[i];
            if (XhtEngineClassTable.IsEngineClass(ident))
            {
                // Engine anchor interface: not in the parsed symbol
                // table; skip resolution (consistent with super
                // binding policy).
                continue;
            }

            XhtTypeBase? hit = ctx.Symbols.Lookup(ident);
            if (hit is null)
            {
                PhaseHelpers.Error(
                    ctx,
                    DiagnosticCodes.SymbolNotFound,
                    $"Class '{cls.FullyQualifiedName}' implements unresolved interface '{ident}'.",
                    cls.Span);
                continue;
            }

            if (hit is not XhtInterface iface)
            {
                PhaseHelpers.Error(
                    ctx,
                    DiagnosticCodes.SuperWrongKind,
                    $"Class '{cls.FullyQualifiedName}' interface identifier '{ident}' resolved to {hit.GetType().Name} but expected interface.",
                    cls.Span);
                continue;
            }

            resolved.Add(iface);
        }

        if (resolved.Count > 0)
        {
            ctx.ResolvedInterfaces[cls] = resolved;
        }
    }

    private static void ResolveWithin(ResolverContext ctx, XhtClass cls)
    {
        if (string.IsNullOrEmpty(cls.WithinIdentifier))
        {
            return;
        }

        if (XhtEngineClassTable.IsEngineClass(cls.WithinIdentifier))
        {
            // Within=XObject etc. -- engine-anchor; not reflected.
            return;
        }

        XhtTypeBase? hit = ctx.Symbols.Lookup(cls.WithinIdentifier);
        if (hit is null)
        {
            PhaseHelpers.Error(
                ctx,
                DiagnosticCodes.SymbolNotFound,
                $"Class '{cls.FullyQualifiedName}' Within='{cls.WithinIdentifier}' could not be resolved.",
                cls.Span);
            return;
        }

        if (hit is not XhtClass outer)
        {
            PhaseHelpers.Error(
                ctx,
                DiagnosticCodes.SuperWrongKind,
                $"Class '{cls.FullyQualifiedName}' Within='{cls.WithinIdentifier}' resolved to {hit.GetType().Name} but expected class.",
                cls.Span);
            return;
        }

        ctx.ResolvedWithin[cls] = outer;
    }

    private static void ValidateWithinAgainstSuper(ResolverContext ctx, XhtClass cls)
    {
        // No Within on this class -> nothing to validate against the
        // super (it would inherit super's Within transitively at
        // emit-time; emit policy is Phase 1e).
        if (!ctx.ResolvedWithin.TryGetValue(cls, out XhtClass? thisWithin))
        {
            return;
        }

        // Find the super's effective Within. Walk the super chain via
        // ResolvedSupers and consult ResolvedWithin at each step.
        XhtClass? superWithin = FindEffectiveWithin(ctx, cls);
        if (superWithin is null)
        {
            // The super has no Within declared anywhere up the chain;
            // any Within on the child is acceptable.
            return;
        }

        // The child's Within must be the same as the super's effective
        // Within, OR a derived-from-or-equal-to the super's Within.
        // Walk the child's Within chain to see if it reaches the super's
        // effective Within.
        if (IsSameOrDerivedFrom(ctx, thisWithin, superWithin))
        {
            return;
        }

        PhaseHelpers.Error(
            ctx,
            DiagnosticCodes.ClassWithinIncompatibleWithSuper,
            $"Class '{cls.FullyQualifiedName}' declares Within='{thisWithin.FullyQualifiedName}' which is not derived-from-or-equal-to super's Within='{superWithin.FullyQualifiedName}'.",
            cls.Span);
    }

    private static XhtClass? FindEffectiveWithin(ResolverContext ctx, XhtClass cls)
    {
        foreach (XhtTypeBase ancestor in PhaseHelpers.WalkSuperChain(ctx, cls))
        {
            if (ancestor is XhtClass ancestorCls
                && ctx.ResolvedWithin.TryGetValue(ancestorCls, out XhtClass? within))
            {
                return within;
            }
        }
        return null;
    }

    private static bool IsSameOrDerivedFrom(ResolverContext ctx, XhtClass candidate, XhtClass target)
    {
        if (ReferenceEquals(candidate, target))
        {
            return true;
        }

        foreach (XhtTypeBase ancestor in PhaseHelpers.WalkSuperChain(ctx, candidate))
        {
            if (ReferenceEquals(ancestor, target))
            {
                return true;
            }
        }
        return false;
    }
}
