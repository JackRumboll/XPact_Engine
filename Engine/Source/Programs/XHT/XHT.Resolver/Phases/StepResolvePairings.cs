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
/// <b>Partial-class merge (C3 audit).</b> C# allows the same type to be
/// split across multiple <c>partial class</c> declarations. The parser
/// registers the first declaration to land into the symbol table; any
/// duplicate partials land in
/// <see cref="ResolverContext.ExtraPartials"/>. This phase walks
/// <c>ExtraPartials</c>, groups them by canonical entry (the
/// first-registered XhtClass), and synthesizes a merged XhtClass via
/// record <c>with</c> -- union of Functions, Properties, Specifiers,
/// and InterfaceIdentifiers across every partial. The merged shape is
/// stored in <see cref="ResolverContext.MergedSymbolView"/> keyed by
/// the canonical entry; the original
/// <see cref="ResolverContext.MergedPartials"/> map carries the
/// duplicate-to-canonical pointer for back-reference.
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

        // Pass 2: detect + merge C# partial-class duplicates from the
        // parser's ExtraPartials stash (set per C3 audit).
        MergePartials(ctx);
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

    /// <summary>
    /// Merge every duplicate C# partial-class declaration in
    /// <see cref="ResolverContext.ExtraPartials"/> with its canonical
    /// (first-registered) entry in the symbol table. The merged
    /// XhtClass replaces the canonical via
    /// <see cref="ResolverContext.MergedSymbolView"/>; emitters and
    /// downstream phases consult <see cref="ResolverContext.GetEffectiveShape"/>
    /// (helper extension) to read the merged view in place of the raw
    /// table entry.
    /// </summary>
    private static void MergePartials(ResolverContext ctx)
    {
        if (ctx.ExtraPartials.Count == 0)
        {
            return;
        }

        // Group duplicate partials by (FullyQualifiedName) so we can
        // perform a single union per canonical entry.
        Dictionary<string, List<XhtClass>> byFqn = new(StringComparer.Ordinal);
        foreach (XhtClass dup in ctx.ExtraPartials)
        {
            if (!byFqn.TryGetValue(dup.FullyQualifiedName, out List<XhtClass>? list))
            {
                list = new List<XhtClass>();
                byFqn[dup.FullyQualifiedName] = list;
            }
            list.Add(dup);
        }

        foreach (KeyValuePair<string, List<XhtClass>> kv in byFqn)
        {
            string fqn = kv.Key;
            List<XhtClass> dups = kv.Value;
            if (dups.Count == 0)
            {
                continue;
            }

            // Find the canonical entry in the symbol table by FQN +
            // language. The first partial registered for this FQN is
            // the canonical entry (it occupied the caseless slot).
            XhtClass? canonical = FindCanonical(ctx, fqn);
            if (canonical is null)
            {
                // No canonical found (shouldn't happen if the parser
                // pushed the first partial to the table). Defensive:
                // skip the merge for this FQN.
                continue;
            }

            // Union members from canonical + every duplicate.
            List<XhtFunction> functions = new(canonical.Functions);
            List<XhtProperty> properties = new(canonical.Properties);
            List<Specifier> specifiers = new(canonical.Specifiers);
            List<string> interfaces = new(canonical.InterfaceIdentifiers);
            HashSet<string> sourcePaths = new(canonical.PartialSourcePaths ?? new[] { canonical.Span.SourceFilePath }, StringComparer.Ordinal);

            string? superId = canonical.SuperIdentifier;
            string? withinId = canonical.WithinIdentifier;

            foreach (XhtClass dup in dups)
            {
                functions.AddRange(dup.Functions);
                properties.AddRange(dup.Properties);
                specifiers.AddRange(dup.Specifiers);
                interfaces.AddRange(dup.InterfaceIdentifiers);
                foreach (string s in dup.PartialSourcePaths ?? new[] { dup.Span.SourceFilePath })
                {
                    sourcePaths.Add(s);
                }
                // Super / Within: if the canonical didn't have one but
                // a duplicate does, adopt the duplicate's value. If
                // both have one and they differ, log a diagnostic and
                // keep the canonical (deterministic).
                if (superId is null && dup.SuperIdentifier is not null)
                {
                    superId = dup.SuperIdentifier;
                }
                else if (superId is not null && dup.SuperIdentifier is not null
                    && !string.Equals(superId, dup.SuperIdentifier, StringComparison.Ordinal))
                {
                    PhaseHelpers.Error(
                        ctx,
                        DiagnosticCodes.SuperWrongKind,
                        $"Partial-class '{fqn}' declares conflicting bases ('{superId}' vs '{dup.SuperIdentifier}'); canonical wins.",
                        dup.Span);
                }
                if (withinId is null && dup.WithinIdentifier is not null)
                {
                    withinId = dup.WithinIdentifier;
                }

                // Record the duplicate -> canonical pointer.
                ctx.MergedPartials[dup] = canonical;
            }

            // Synthesize the merged XhtClass via the record `with`
            // pattern. The merged value is stored in MergedSymbolView
            // keyed by the canonical entry AND installed into the
            // symbol table in place of the canonical so downstream
            // resolver phases (BindSuperAndBases, ResolveBases,
            // Properties, Final) see the merged shape when they walk
            // ctx.Symbols.AllTypes. The replacement preserves the
            // caseless key (the canonical's name doesn't change) so
            // existing super / interface lookups still hit.
            List<string> sortedSourcePaths = new(sourcePaths);
            sortedSourcePaths.Sort(StringComparer.Ordinal);
            XhtClass merged = canonical with
            {
                Functions = functions,
                Properties = properties,
                Specifiers = specifiers,
                InterfaceIdentifiers = interfaces,
                SuperIdentifier = superId,
                WithinIdentifier = withinId,
                PartialSourcePaths = sortedSourcePaths,
            };
            ctx.MergedSymbolView[canonical] = merged;
            ctx.Symbols.Replace(canonical, merged);

            // Emit one informational diagnostic per merged duplicate.
            foreach (XhtClass dup in dups)
            {
                PhaseHelpers.Info(
                    ctx,
                    DiagnosticCodes.PartialClassMerged,
                    $"C# partial-class duplicate '{dup.FullyQualifiedName}' merged into canonical declaration at '{canonical.Span.SourceFilePath}({canonical.Span.Line})'.",
                    dup.Span);
            }
        }
    }

    private static XhtClass? FindCanonical(ResolverContext ctx, string fqn)
    {
        foreach (XhtTypeBase t in ctx.Symbols.AllTypes)
        {
            if (t is XhtClass cls
                && cls.Language == Language.CSharp
                && string.Equals(cls.FullyQualifiedName, fqn, StringComparison.Ordinal))
            {
                return cls;
            }
        }
        return null;
    }
}
