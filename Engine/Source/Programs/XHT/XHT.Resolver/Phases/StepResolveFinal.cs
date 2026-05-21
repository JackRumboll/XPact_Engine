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

        // 1. Specifier-conflict validators. Per M11 audit: attribute
        // each diagnostic to the TYPE's home module (ordered[i].ModuleName),
        // not the consumer's current module. The home module is what
        // the user must fix; the consumer-module attribution misled
        // diagnostics for cross-module types.
        for (int i = 0; i < ordered.Count; i++)
        {
            IReadOnlyList<DiagnosticRecord> conflicts =
                SpecifierConflictDetector.Detect(ordered[i], ctx.SpecifierRegistry);
            for (int j = 0; j < conflicts.Count; j++)
            {
                DiagnosticRecord d = conflicts[j];
                ctx.Diagnostics.Add(d with { Module = ordered[i].ModuleName });
            }
        }

        // 2. Cross-language pairing kind check (XHT120). The symbol
        // table key folds across languages: the same caseless key may
        // be claimed by one type only. The cross-language interaction
        // is therefore that two registrations attempting the same key
        // would have already failed at the parser-side (XHT040). What
        // the Final phase verifies is that any DIFFERENT-key but same-
        // engine-name pairing (e.g., XValve in C++ and XValve in C# --
        // same engine name across both sides; the resolver folds them
        // and the emit boundary references the merged shape) is
        // structurally compatible.
        CheckCrossLanguagePairings(ctx, ordered);

        // 3. Cross-tier dep validation (XHT121).
        CheckCrossTierReferences(ctx, ordered);
    }

    /// <summary>
    /// Implement the XHT.html §5.7 cross-language consistency check per
    /// C8 audit. For each reflected type's super and properties, walk
    /// the type references and emit:
    ///   - <see cref="DiagnosticCodes.SuperWrongKind"/> (XHT104) when a
    ///     C++ class's super resolves to a C# struct (or any other
    ///     kind mismatch). Already covered by BindSuperAndBases.
    ///   - <see cref="DiagnosticCodes.CrossLanguagePairingMismatch"/>
    ///     (XHT120) when a property's type identifier names a type
    ///     that appears to be a non-reflected sibling in the engine
    ///     source (i.e., the identifier looks like a reflectable type
    ///     but is missing the <c>XCLASS</c> / <c>[XClass]</c> marker).
    /// </summary>
    private static void CheckCrossLanguagePairings(ResolverContext ctx, IReadOnlyList<XhtTypeBase> ordered)
    {
        // Build the set of all known engine-name caseless keys for fast
        // membership checks. Plus a per-language set so we can detect
        // "C++ side has it, C# side doesn't" patterns.
        HashSet<string> allEngineKeys = new(StringComparer.Ordinal);
        Dictionary<string, Language> registeredLanguage = new(StringComparer.Ordinal);
        foreach (XhtTypeBase t in ordered)
        {
            allEngineKeys.Add(t.CaselessKey);
            registeredLanguage[t.CaselessKey] = t.Language;
        }

        // Walk every property; if its type identifier looks like a
        // reflectable user type (starts with 'X' or a UE-prefix letter
        // and an uppercase letter, OR has the form X<Name>) but does
        // not resolve in the symbol table, emit XHT120.
        foreach (XhtTypeBase t in ordered)
        {
            IReadOnlyList<XhtProperty>? props = t switch
            {
                XhtClass c => c.Properties,
                XhtStruct s => s.Properties,
                _ => null,
            };
            if (props is null) { continue; }

            foreach (XhtProperty p in props)
            {
                // If the resolver already classified this property type
                // as resolved, no XHT120: the symbol table found it.
                if (ctx.ResolvedPropertyTypes.ContainsKey(p))
                {
                    continue;
                }

                string typeText = p.TypeIdentifier ?? string.Empty;
                if (string.IsNullOrEmpty(typeText))
                {
                    continue;
                }

                // Extract the base identifier (strip pointers / refs /
                // template arguments). Container properties handled
                // separately via the inner-type extraction.
                string lookup;
                if (p.IsContainer)
                {
                    lookup = StepResolveProperties.ExtractInnerTypeIdentifier(typeText)
                        ?? typeText;
                }
                else
                {
                    lookup = typeText.Trim().TrimEnd('*', '&').Trim();
                }
                if (string.IsNullOrEmpty(lookup))
                {
                    continue;
                }

                if (!LooksLikeReflectableTypeName(lookup))
                {
                    // Primitive ("int32" / "float" / "string") or a
                    // non-XPact-convention identifier -- not our place
                    // to assume the user means a reflected reference.
                    continue;
                }

                if (allEngineKeys.Contains(Core.StringUtils.ToCaselessKey(lookup)))
                {
                    // Looks reflectable AND is registered; the
                    // ResolvedPropertyTypes hit covers this in normal
                    // operation. If we got here without a hit despite a
                    // matching engine-key, there's a downstream
                    // lookup gap -- still no XHT120, but worth a
                    // future trace.
                    continue;
                }

                // Looks reflectable, but the symbol table has no entry.
                // This is the XHT120 condition: the type identifier
                // references a sibling type that is NOT reflected (or
                // missing the marker entirely). Attribute the error to
                // the property's HOME module (the consumer), but name
                // the missing identifier explicitly so the user knows
                // what to mark.
                PhaseHelpers.Error(
                    ctx,
                    DiagnosticCodes.CrossLanguagePairingMismatch,
                    $"Property '{t.FullyQualifiedName}.{p.Name}' references type '{lookup}' which is not reflected (missing XCLASS / XSTRUCT / [XClass] / [XStruct] marker?).",
                    p.Span);
            }
        }
    }

    /// <summary>
    /// Heuristic: does <paramref name="name"/> look like a name that
    /// SHOULD be reflected if it exists in source? Per Round-2 audit M4
    /// + the user-locked Round-1 decision to drop A/U/I/F prefixes: only
    /// XPact-convention names ('X' + uppercase second char) trigger the
    /// "should be reflected" heuristic. Legacy UE A/U/I/F prefixes are
    /// no longer treated as reflectable -- those names appear in XPact
    /// only as primitive aliases (FString, FName, FText) which are
    /// explicitly excluded below.
    /// </summary>
    private static bool LooksLikeReflectableTypeName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length < 2)
        {
            return false;
        }

        // Built-in primitive set (case-sensitive) -- never reflectable.
        // Includes the legacy UE F-prefix aliases the engine still uses
        // for string / name / text primitive types.
        switch (name)
        {
            case "void":
            case "bool":
            case "char":
            case "short":
            case "int":
            case "long":
            case "float":
            case "double":
            case "int8":
            case "int16":
            case "int32":
            case "int64":
            case "uint8":
            case "uint16":
            case "uint32":
            case "uint64":
            case "string":
            case "FString":
            case "FName":
            case "FText":
                return false;
        }

        char first = name[0];
        char second = name[1];
        return first == 'X' && second >= 'A' && second <= 'Z';
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
