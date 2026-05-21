// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Tables;

namespace Simgenics.XPact.XHT.Resolver.Phases;

/// <summary>
/// Phase 3 (<see cref="ResolvePhase.BindSuperAndBases"/>): resolve every
/// <see cref="XhtClass.SuperIdentifier"/>, <see cref="XhtStruct.SuperIdentifier"/>,
/// and <see cref="XhtInterface.SuperIdentifier"/> via the caseless
/// symbol table. Populates <see cref="ResolverContext.ResolvedSupers"/>.
/// Mirrors UHT's <c>StepBindSuperAndBases</c> at
/// <c>UhtSession.cs:1460</c> per <c>/Documents/XHT.html</c> Rev 7
/// Section 5.1 + Section 5.3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Engine-anchor exemption.</b> A super named in the engine-anchor
/// table (<see cref="XhtEngineClassTable"/>) is allowed to resolve to
/// null without diagnostic; the anchor types are runtime-registered
/// rather than reflected, so they intentionally do not live in the
/// parsed symbol table. The check uses
/// <see cref="XhtEngineClassTable.IsEngineClass(string)"/> on the
/// pre-strip identifier.
/// </para>
/// <para>
/// <b>Wrong-kind diagnostic.</b> When the identifier resolves to a
/// reflected entity but the entity is the wrong AST shape (e.g., a
/// <see cref="XhtClass"/>'s <c>SuperIdentifier</c> resolves to a
/// <see cref="XhtStruct"/>), the resolver emits
/// <see cref="DiagnosticCodes.SuperWrongKind"/> (XHT104). The check
/// runs once per type; mismatches do not propagate downstream.
/// </para>
/// </remarks>
internal sealed class StepBindSuperAndBases : IResolverStep
{
    /// <inheritdoc />
    public void Execute(ResolverContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        IReadOnlyList<XhtTypeBase> ordered = ResolverPipeline.OrderedTypesSnapshot(ctx.Symbols);

        for (int i = 0; i < ordered.Count; i++)
        {
            XhtTypeBase t = ordered[i];
            switch (t)
            {
                case XhtClass cls:
                    BindClassSuper(ctx, cls);
                    break;
                case XhtStruct st:
                    BindStructSuper(ctx, st);
                    break;
                case XhtInterface iface:
                    BindInterfaceSuper(ctx, iface);
                    break;
                default:
                    break;
            }
        }
    }

    private static void BindClassSuper(ResolverContext ctx, XhtClass cls)
    {
        if (string.IsNullOrEmpty(cls.SuperIdentifier))
        {
            return;
        }

        if (XhtEngineClassTable.IsEngineClass(cls.SuperIdentifier))
        {
            // Engine anchor super (e.g., XObject). Not registered in
            // the parsed symbol table; this is allowed.
            return;
        }

        XhtTypeBase? resolved = ctx.Symbols.Lookup(cls.SuperIdentifier);
        if (resolved is null)
        {
            PhaseHelpers.Error(
                ctx,
                DiagnosticCodes.SymbolNotFound,
                $"Class '{cls.FullyQualifiedName}' extends unresolved type '{cls.SuperIdentifier}'.",
                cls.Span);
            return;
        }

        if (resolved is not XhtClass)
        {
            PhaseHelpers.Error(
                ctx,
                DiagnosticCodes.SuperWrongKind,
                $"Class '{cls.FullyQualifiedName}' super '{cls.SuperIdentifier}' resolved to {resolved.GetType().Name} but expected class.",
                cls.Span);
            return;
        }

        ctx.ResolvedSupers[cls] = resolved;
    }

    private static void BindStructSuper(ResolverContext ctx, XhtStruct st)
    {
        if (string.IsNullOrEmpty(st.SuperIdentifier))
        {
            return;
        }

        if (XhtEngineClassTable.IsEngineClass(st.SuperIdentifier))
        {
            return;
        }

        XhtTypeBase? resolved = ctx.Symbols.Lookup(st.SuperIdentifier);
        if (resolved is null)
        {
            PhaseHelpers.Error(
                ctx,
                DiagnosticCodes.SymbolNotFound,
                $"Struct '{st.FullyQualifiedName}' extends unresolved type '{st.SuperIdentifier}'.",
                st.Span);
            return;
        }

        if (resolved is not XhtStruct)
        {
            PhaseHelpers.Error(
                ctx,
                DiagnosticCodes.SuperWrongKind,
                $"Struct '{st.FullyQualifiedName}' super '{st.SuperIdentifier}' resolved to {resolved.GetType().Name} but expected struct.",
                st.Span);
            return;
        }

        ctx.ResolvedSupers[st] = resolved;
    }

    private static void BindInterfaceSuper(ResolverContext ctx, XhtInterface iface)
    {
        if (string.IsNullOrEmpty(iface.SuperIdentifier))
        {
            return;
        }

        if (XhtEngineClassTable.IsEngineClass(iface.SuperIdentifier))
        {
            return;
        }

        XhtTypeBase? resolved = ctx.Symbols.Lookup(iface.SuperIdentifier);
        if (resolved is null)
        {
            PhaseHelpers.Error(
                ctx,
                DiagnosticCodes.SymbolNotFound,
                $"Interface '{iface.FullyQualifiedName}' extends unresolved type '{iface.SuperIdentifier}'.",
                iface.Span);
            return;
        }

        if (resolved is not XhtInterface)
        {
            PhaseHelpers.Error(
                ctx,
                DiagnosticCodes.SuperWrongKind,
                $"Interface '{iface.FullyQualifiedName}' super '{iface.SuperIdentifier}' resolved to {resolved.GetType().Name} but expected interface.",
                iface.Span);
            return;
        }

        ctx.ResolvedSupers[iface] = resolved;
    }
}
