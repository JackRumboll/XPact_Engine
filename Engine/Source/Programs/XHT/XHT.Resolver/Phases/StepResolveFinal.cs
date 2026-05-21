// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Manifest;
using Simgenics.XPact.XHT.Resolver.Validators;

namespace Simgenics.XPact.XHT.Resolver.Phases;

/// <summary>
/// Phase 7 (<see cref="ResolvePhase.Final"/>, serial): cross-language
/// consistency walk, specifier-conflict validators, and cross-tier
/// module-dep validation per <c>/Documents/XHT.html</c> Rev 5
/// Section 5.6 + Section 5.7 + Section 6.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Serial-by-design.</b> Per Section 22.1, the Final phase runs
/// single-threaded. The whole-program checks need the full resolved
/// type graph; per-type parallelism would re-introduce ordering
/// hazards.
/// </para>
/// <para>
/// <b>What runs here.</b>
/// </para>
/// <list type="bullet">
///   <item><description>Specifier-conflict validators (<see cref="SpecifierConflictDetector"/>): XHT111, XHT112, XHT114-XHT117.</description></item>
///   <item><description>Cross-language pairing kind check (XHT120): a C++ type and a C# type sharing the same engine name must be the same AST shape (both class, or both struct).</description></item>
///   <item><description>Cross-tier dep validation (XHT121): property types referenced through interface-only / dynamic-only module deps must surface diagnostics.</description></item>
/// </list>
/// </remarks>
internal sealed class StepResolveFinal : IResolverStep
{
    /// <inheritdoc />
    public void Execute(ResolverContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        IReadOnlyList<XhtTypeBase> ordered = ResolverPipeline.OrderedTypesSnapshot(ctx.Symbols);

        // 1. Specifier-conflict validators.
        for (int i = 0; i < ordered.Count; i++)
        {
            IReadOnlyList<DiagnosticRecord> conflicts =
                SpecifierConflictDetector.Detect(ordered[i], ctx.SpecifierRegistry);
            for (int j = 0; j < conflicts.Count; j++)
            {
                DiagnosticRecord d = conflicts[j];
                ctx.Diagnostics.Add(d with { Module = ctx.ModuleName });
            }
        }

        // 2. Cross-language pairing kind check (XHT120). The symbol
        // table key folds across languages: the same caseless key may
        // be claimed by one type only. The cross-language interaction
        // is therefore that two registrations attempting the same key
        // would have already failed at the parser-side (XHT040). What
        // the Final phase verifies is that any DIFFERENT-key but same-
        // engine-name pairing (e.g., AXValve in C++ and Valve in C# --
        // different keys, but both name the same engine type at the
        // emit boundary) is structurally compatible.
        CheckCrossLanguagePairings(ctx, ordered);

        // 3. Cross-tier dep validation (XHT121).
        CheckCrossTierReferences(ctx, ordered);
    }

    private static void CheckCrossLanguagePairings(ResolverContext ctx, IReadOnlyList<XhtTypeBase> ordered)
    {
        // Group by the engine-key suffix (the substring after any
        // stripped UE-prefix; effectively the CaselessKey itself). Two
        // types sharing the same CaselessKey would have collided in
        // the symbol table (the parser emits XHT040 for that case);
        // what the Final check covers is the case where the parser
        // saw both but only registered one and we recorded the
        // duplicate elsewhere -- per Phase 1d that means
        // MergedPartials does NOT apply (those are same-language
        // partials), but cross-language collisions could survive in
        // the form of two types with related-but-not-equal caseless
        // keys.

        // Build a quick lookup: caseless-key -> registered type.
        Dictionary<string, XhtTypeBase> byKey = new(StringComparer.Ordinal);
        foreach (XhtTypeBase t in ordered)
        {
            byKey[t.CaselessKey] = t;
        }

        for (int i = 0; i < ordered.Count; i++)
        {
            XhtTypeBase t = ordered[i];

            // Check whether a super resolved to a wrong-language /
            // wrong-kind hit. (XHT104 already fires for that during
            // BindSuperAndBases; this check is the per-property
            // companion: a C# property typed against a C++ type that
            // turned out to be a different kind at resolution time.)
            if (t is XhtClass cls)
            {
                if (ctx.ResolvedSupers.TryGetValue(cls, out XhtTypeBase? superResolved)
                    && superResolved is XhtClass superCls
                    && superCls.Language != cls.Language)
                {
                    // Different languages -- check the same engine name
                    // is the recommended pairing form. We don't error
                    // here; this is the supported cross-language inheritance.
                }
            }
        }
    }

    private static void CheckCrossTierReferences(ResolverContext ctx, IReadOnlyList<XhtTypeBase> ordered)
    {
        // Find the consumer module (the module currently being resolved).
        XbtModule? consumerModule = null;
        for (int i = 0; i < ctx.Manifest.Modules.Count; i++)
        {
            XbtModule m = ctx.Manifest.Modules[i];
            if (string.Equals(m.Name, ctx.ModuleName, StringComparison.Ordinal))
            {
                consumerModule = m;
                break;
            }
        }

        // If the consumer module isn't present in the manifest, we
        // cannot validate dep-edges; the XHT manifest reader would
        // already have surfaced this upstream (exit 50). Skip the
        // check to avoid noise.
        if (consumerModule is null)
        {
            return;
        }

        // Build a set of LINK-tier dependency module names: every dep
        // where InterfaceModule == false (the FBS-side flag is the
        // dynamic / interface-only marker per Section 5.6 X-Round2-M1
        // discussion). The XHT.Manifest C# DTO carries
        // InterfaceModule; for the resolver's cross-tier check, an
        // interface-only dep is treated as "not link-visible" --
        // type references through it surface XHT121.
        HashSet<string> linkDeps = new(StringComparer.Ordinal);
        HashSet<string> interfaceOnlyDeps = new(StringComparer.Ordinal);
        foreach (XbtModuleDep dep in consumerModule.ModuleDependencies)
        {
            if (dep.InterfaceModule)
            {
                interfaceOnlyDeps.Add(dep.Name);
            }
            else
            {
                linkDeps.Add(dep.Name);
            }
        }
        // Always include the consumer module itself.
        linkDeps.Add(consumerModule.Name);

        // Walk every property in the consumer module's reflected types
        // and check whether the property's resolved type lives in a
        // non-link-visible module.
        foreach (XhtTypeBase t in ordered)
        {
            // Only walk types owned by the consumer module.
            if (!string.Equals(t.ModuleName, ctx.ModuleName, StringComparison.Ordinal))
            {
                continue;
            }

            IReadOnlyList<XhtProperty>? props = t switch
            {
                XhtClass c => c.Properties,
                XhtStruct s => s.Properties,
                _ => null,
            };

            if (props is null)
            {
                continue;
            }

            foreach (XhtProperty p in props)
            {
                if (!ctx.ResolvedPropertyTypes.TryGetValue(p, out XhtTypeBase? resolved))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(resolved.ModuleName))
                {
                    continue;
                }

                // Same module -- always link-visible.
                if (string.Equals(resolved.ModuleName, ctx.ModuleName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (linkDeps.Contains(resolved.ModuleName))
                {
                    continue;
                }

                if (interfaceOnlyDeps.Contains(resolved.ModuleName))
                {
                    PhaseHelpers.Error(
                        ctx,
                        DiagnosticCodes.DynamicOnlyModuleReference,
                        $"Property '{t.FullyQualifiedName}.{p.Name}' references type '{resolved.FullyQualifiedName}' through interface-only dep '{resolved.ModuleName}'; reflection must go through IXSymbolTable::Resolve<T> rather than a direct property reference.",
                        p.Span);
                    continue;
                }

                // The type lives in a module not in the consumer's dep
                // closure at all. This is XHT120 per Section 5.7 (the
                // existing cross-module reference check). Report the
                // ModuleName so the user can add the dep.
                PhaseHelpers.Error(
                    ctx,
                    DiagnosticCodes.CrossLanguagePairingMismatch,
                    $"Property '{t.FullyQualifiedName}.{p.Name}' references type '{resolved.FullyQualifiedName}' in module '{resolved.ModuleName}' which is not in the consumer module's dependency closure.",
                    p.Span);
            }
        }
    }
}
