// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XHT.AST;

namespace Simgenics.XPact.XHT.Resolver.Phases;

/// <summary>
/// Phase 2 (<see cref="ResolvePhase.InvalidCheck"/>): narrow sanity
/// checks per <c>/Documents/XHT.html</c> Rev 5 Section 5.1 + UHT
/// precedent. Specifier-conflict checks do NOT run here; they run in
/// <see cref="StepResolveFinal"/> per the Rev 2 + UHT
/// <c>StepResolveValidate</c> placement.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per UHT precedent.</b> InvalidCheck handles "things that prevent
/// the resolver from proceeding": orphaned native interfaces (caught
/// upstream in Pairings; consolidated here is redundant), reflected
/// members that lack a valid container, etc.
/// </para>
/// <para>
/// <b>Top-level XFUNCTION ban.</b> In XPact's model every reflected
/// function lives inside a class / struct / interface; a free-floating
/// XFUNCTION makes no sense at runtime because there is no <c>this</c>
/// to dispatch through. The check walks
/// <see cref="ResolverContext.Symbols"/> looking for orphan
/// <see cref="XhtFunction"/> entries (which would only appear if a
/// parser bug let one through); the parser is the load-bearing
/// preventer.
/// </para>
/// </remarks>
internal sealed class StepResolveInvalidCheck : IResolverStep
{
    /// <inheritdoc />
    public void Execute(ResolverContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // The symbol table only carries XhtTypeBase entries (class /
        // struct / enum / interface / delegate). Functions and
        // properties are owned by their container. The "orphan
        // function" check is therefore implicit: if the parser
        // produced one not attached to a container, it would never
        // appear in AllTypes. Phase 1d covers the structural-coherence
        // check by walking every container and asserting its function
        // / property lists are self-consistent. Top-level XPROPERTY is
        // similarly disallowed; only members of class / struct /
        // interface qualify.

        // Re-check the Pairings result: if a C++ XhtInterface still
        // has no entry in InterfacePairings after Pairings ran, that
        // is the canonical "orphaned interface" signal. Pairings
        // already emitted XHT100 for this case, so InvalidCheck does
        // not re-emit; we keep the phase deliberate-but-minimal so
        // future checks (e.g., the body-macro presence sanity check)
        // can layer in without churn.

        IReadOnlyList<XhtTypeBase> ordered = ResolverPipeline.OrderedTypesSnapshot(ctx.Symbols);

        for (int i = 0; i < ordered.Count; i++)
        {
            XhtTypeBase t = ordered[i];
            switch (t)
            {
                case XhtClass cls:
                    CheckClassMembers(ctx, cls);
                    break;
                case XhtStruct st:
                    CheckStructMembers(ctx, st);
                    break;
                case XhtInterface iface:
                    CheckInterfaceMembers(ctx, iface);
                    break;
                default:
                    break;
            }
        }
    }

    private static void CheckClassMembers(ResolverContext ctx, XhtClass cls)
    {
        // Functions on a class are always valid here -- the class is
        // their container. Properties on a class are also always
        // valid. Phase 1d's check is structural: assert lists are non-
        // null (sanity guard against parser regressions).
        if (cls.Functions is null)
        {
            PhaseHelpers.Error(
                ctx,
                DiagnosticCodes.TopLevelFunctionForbidden,
                $"Class '{cls.FullyQualifiedName}' has null Functions list (parser invariant violation).",
                cls.Span);
        }
        if (cls.Properties is null)
        {
            PhaseHelpers.Error(
                ctx,
                DiagnosticCodes.OrphanProperty,
                $"Class '{cls.FullyQualifiedName}' has null Properties list (parser invariant violation).",
                cls.Span);
        }
    }

    private static void CheckStructMembers(ResolverContext ctx, XhtStruct st)
    {
        if (st.Properties is null)
        {
            PhaseHelpers.Error(
                ctx,
                DiagnosticCodes.OrphanProperty,
                $"Struct '{st.FullyQualifiedName}' has null Properties list (parser invariant violation).",
                st.Span);
        }
    }

    private static void CheckInterfaceMembers(ResolverContext ctx, XhtInterface iface)
    {
        if (iface.Functions is null)
        {
            PhaseHelpers.Error(
                ctx,
                DiagnosticCodes.TopLevelFunctionForbidden,
                $"Interface '{iface.FullyQualifiedName}' has null Functions list (parser invariant violation).",
                iface.Span);
        }
    }
}
