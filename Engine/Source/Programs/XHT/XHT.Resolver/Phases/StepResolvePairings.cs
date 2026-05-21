// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;

namespace Simgenics.XPact.XHT.Resolver.Phases;

/// <summary>
/// Phase 1 (<see cref="ResolvePhase.Pairings"/>): pair each XINTERFACE
/// with its companion XCLASS, and merge C# partial-class duplicates per
/// <c>/Documents/XHT.html</c> Rev 5 Section 4.5 (interface pairing) and
/// the partial-class merge rule per the brief.
/// </summary>
/// <remarks>
/// <para>
/// <b>Interface pairing.</b> The UHT precedent (<c>U</c>-prefix companion
/// + <c>I</c>-prefix native interface) is captured in XHT by treating
/// every <see cref="XhtInterface"/> as the "I"-side and looking for an
/// "U"-side <see cref="XhtClass"/> via the caseless symbol-table key.
/// Because <see cref="SymbolTable.Lookup"/> already strips the leading
/// UE-convention letter, the same engine-name yields both the
/// interface and the companion class. The pairing logic resolves the
/// companion via direct caseless lookup of the interface's name --
/// when a companion class exists, it shares the same caseless key. If
/// the only resolved entity is the interface itself, the pairing
/// fails and <see cref="DiagnosticCodes.InterfaceWithoutPairedClass"/>
/// (XHT100) is emitted.
/// </para>
/// <para>
/// <b>Partial-class merge.</b> C# allows the same type to be split
/// across multiple <c>partial class</c> declarations. The parser
/// registers the first declaration to land into the symbol table; any
/// duplicate would normally throw a collision. The Pairings phase
/// merges duplicates by populating
/// <see cref="ResolverContext.MergedPartials"/>; downstream phases
/// consult the map when they need the merged shape. This is a Phase
/// 1d "best-effort" merge: since AST records are immutable, we record
/// the pair and emit <see cref="DiagnosticCodes.PartialClassMerged"/>
/// (XHT143) informationally.
/// </para>
/// </remarks>
internal sealed class StepResolvePairings : IResolverStep
{
    /// <inheritdoc />
    public void Execute(ResolverContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        IReadOnlyList<XhtTypeBase> ordered = ResolverPipeline.OrderedTypesSnapshot(ctx.Symbols);

        // Pass 1: pair interfaces with companion classes.
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i] is XhtInterface iface)
            {
                TryPairInterface(ctx, iface);
            }
        }

        // Pass 2: detect partial-class duplicates (C#-only).
        // The symbol table only carries one entry per caseless key; the
        // duplicate sat in the originating walker's locally-rooted
        // type list (the C# walker emits XHT040 today for the second
        // entry). Phase 1d covers the in-table case where a caseless
        // key is shared between a fresh class and the same module's
        // partial class declared at a different file. For now, we walk
        // every XhtClass with Language.CSharp and look for siblings
        // sharing the FullyQualifiedName ordinal-equal.
        DetectPartials(ctx, ordered);
    }

    private static void TryPairInterface(ResolverContext ctx, XhtInterface iface)
    {
        // Per Section 4.5, XINTERFACE conceptually produces two type
        // nodes: the X-prefix "type-info anchor" (modelled by the
        // XhtInterface itself) and the I-prefix "native interface"
        // companion. In the current Phase 1d AST the XhtInterface
        // carries both halves logically; a SEPARATE companion XhtClass
        // is only present when the user explicitly authored one (or a
        // future parser pass starts emitting both).
        //
        // The pairing logic:
        //   1. Look up the interface's name caselessly.
        //   2. If the lookup returns a DISTINCT XhtClass, record the
        //      pairing (the parser registered the class first under
        //      the same caseless key; SymbolTable allows only one).
        //   3. If the lookup returns the interface itself, the
        //      "companion" is implicit on the interface node -- no
        //      pairing recorded, no diagnostic.
        //   4. For an explicit "paired class required" signal (a
        //      future parser flag), the parser is expected to emit a
        //      sentinel specifier or carry the failure upstream; the
        //      resolver's XHT100 emission path is reserved for that
        //      future signal.
        XhtTypeBase? resolved = ctx.Symbols.Lookup(iface.Name);

        if (resolved is XhtClass companion && !ReferenceEquals(companion, iface))
        {
            ctx.InterfacePairings[iface] = companion;
            return;
        }

        // Future-pairing sentinel: a parser may attach a
        // "PairedClass=<name>" specifier on an XhtInterface to declare
        // an explicit companion class. Lookup the named class; on miss,
        // emit XHT100. Phase 1d does not yet generate this sentinel
        // from the parser side, but the validator is in place so the
        // upstream change can land without resolver churn.
        Specifier? paired = PhaseHelpers.FindSpecifier(iface.Specifiers, "PairedClass");
        if (paired is not null
            && paired.Values is { Count: > 0 } values
            && !string.IsNullOrEmpty(values[0]))
        {
            string companionName = values[0];
            XhtTypeBase? hit = ctx.Symbols.Lookup(companionName);
            if (hit is XhtClass cls)
            {
                ctx.InterfacePairings[iface] = cls;
            }
            else
            {
                PhaseHelpers.Error(
                    ctx,
                    DiagnosticCodes.InterfaceWithoutPairedClass,
                    $"XINTERFACE '{iface.Name}' declares PairedClass='{companionName}' but the companion class could not be resolved.",
                    iface.Span);
            }
        }
    }

    private static void DetectPartials(ResolverContext ctx, IReadOnlyList<XhtTypeBase> ordered)
    {
        // Group C# classes by FullyQualifiedName (ordinal-equal).
        // Within each group, the symbol-table-registered entry is the
        // canonical form; any others are partial duplicates.
        Dictionary<string, XhtClass> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i] is not XhtClass cls)
            {
                continue;
            }
            if (cls.Language != Language.CSharp)
            {
                continue;
            }

            if (!seen.TryAdd(cls.FullyQualifiedName, cls))
            {
                XhtClass canonical = seen[cls.FullyQualifiedName];
                ctx.MergedPartials[cls] = canonical;
                PhaseHelpers.Info(
                    ctx,
                    DiagnosticCodes.PartialClassMerged,
                    $"C# partial-class duplicate '{cls.FullyQualifiedName}' merged into canonical declaration.",
                    cls.Span);
            }
        }
    }
}
