// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.Configuration;

/// <summary>
/// One tier-validation violation. Returned by
/// <see cref="TierValidator.ValidateModuleDeps"/>; the catching site
/// emits a diagnostic with exit code 21 (<c>TierViolation</c> per
/// Toolchain Contract Rev 13 Section 13).
/// </summary>
/// <param name="ConsumerName">The name of the consuming module (edge tail).</param>
/// <param name="ConsumerTier">The tier of the consuming module.</param>
/// <param name="ProducerName">The name of the producing module (edge head).</param>
/// <param name="ProducerTier">The tier of the producing module.</param>
/// <param name="Kind">
/// The dependency kind. Per the matrix, only
/// <see cref="DependencyKind.Link"/> can produce violations; dynamic
/// edges are always allowed.
/// </param>
public sealed record TierViolation(
    string ConsumerName,
    ModuleTier ConsumerTier,
    string ProducerName,
    ModuleTier ProducerTier,
    DependencyKind Kind)
{
    /// <summary>
    /// Human-readable diagnostic message matching the format in
    /// <c>/Documents/XBT.html</c> Section 13.3.
    /// </summary>
    public string FormatMessage()
    {
        string fromTier = ConsumerTier.ToString();
        string toTier = ProducerTier.ToString();
        string edgeWord = Kind == DependencyKind.Link ? "link" : "dynamic-load";
        return
            $"Tier violation in module dependency graph ({edgeWord} dep). " +
            $"Module: {ConsumerName} (Tier = {fromTier}) declares a {edgeWord} dependency on " +
            $"{ProducerName} which has Tier = {toTier}. " +
            $"{fromTier}-tier modules may not {edgeWord}-depend on {toTier}-tier modules. " +
            "Link deps point downward only (Engine <- Studio <- Project); " +
            "dynamic-load deps (DynamicallyLoadedModuleNames) are allowed in any direction.";
    }
}

/// <summary>
/// Walks a <see cref="ModuleRules"/>'s three dependency lists and
/// returns every edge that violates the cross-tier matrix
/// (<see cref="TierMatrix"/>). Implements <c>/Documents/XBT.html</c>
/// Section 4.4 + 13 and Toolchain Contract Rev 13 Section 9.3.
/// </summary>
/// <remarks>
/// <para>
/// The validator is a pure function: it produces a list of violations
/// and never throws on a tier issue itself. The caller decides whether
/// to log warnings + continue or to throw a
/// <see cref="ModuleRulesValidationException"/> with exit 21. This
/// separation lets the catching site batch multiple violations into
/// a single diagnostic pass.
/// </para>
/// <para>
/// <b>Resolver function.</b> Module dependencies are by name; the
/// validator does not know the dependency's tier until the catching
/// site supplies it through the resolver. The resolver returns
/// <c>null</c> for an unknown name (e.g. a missing plugin); the
/// validator skips those edges silently because tier validation is
/// scoped to the present module graph, and the missing-plugin
/// diagnostic comes from a separate code path (Contract Section 9.3:
/// "tier validation runs as if all declared plugins exist; a
/// diagnostic fires if a build is configured with a missing Studio
/// plugin enabled-by-default").
/// </para>
/// </remarks>
public static class TierValidator
{
    /// <summary>
    /// Validate the dependency edges of a single module against the
    /// cross-tier matrix.
    /// </summary>
    /// <param name="module">The consuming module (edge tail).</param>
    /// <param name="moduleTier">
    /// The tier of <paramref name="module"/>. Passed explicitly because
    /// <see cref="ModuleRules.Tier"/> is init-only and we already have
    /// the value at the call site -- no need to re-read.
    /// </param>
    /// <param name="resolveTier">
    /// Callback that maps a dependency name to its declared tier, or
    /// returns null for an unknown name. Provided by the catching site.
    /// </param>
    /// <returns>
    /// Empty list on success. One <see cref="TierViolation"/> entry per
    /// disallowed edge otherwise.
    /// </returns>
    public static IReadOnlyList<TierViolation> ValidateModuleDeps(
        ModuleRules module,
        ModuleTier moduleTier,
        Func<string, ModuleTier?> resolveTier)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(resolveTier);

        List<TierViolation> violations = new();

        // Public + Private dep lists are both Link-kind edges; the
        // public/private distinction does not affect tier validation
        // (only include-path propagation).
        CheckList(
            consumerName: module.Name,
            consumerTier: moduleTier,
            deps: module.PublicDependencyModuleNames,
            kind: DependencyKind.Link,
            resolveTier: resolveTier,
            violations: violations);

        CheckList(
            consumerName: module.Name,
            consumerTier: moduleTier,
            deps: module.PrivateDependencyModuleNames,
            kind: DependencyKind.Link,
            resolveTier: resolveTier,
            violations: violations);

        // Dynamic deps run through the matrix too even though every
        // edge currently passes (the matrix says Dynamic is always
        // allowed). We still call IsEdgeAllowed so a future contract
        // revision that adds a Dynamic restriction is caught here
        // without re-plumbing the validator.
        CheckList(
            consumerName: module.Name,
            consumerTier: moduleTier,
            deps: module.DynamicallyLoadedModuleNames,
            kind: DependencyKind.Dynamic,
            resolveTier: resolveTier,
            violations: violations);

        return violations;
    }

    private static void CheckList(
        string consumerName,
        ModuleTier consumerTier,
        List<ModuleDep> deps,
        DependencyKind kind,
        Func<string, ModuleTier?> resolveTier,
        List<TierViolation> violations)
    {
        foreach (ModuleDep dep in deps)
        {
            ModuleTier? producerTier = resolveTier(dep.Name);
            if (producerTier is null)
            {
                // Unknown -- skip, per the Contract Section 9.3 note.
                continue;
            }

            if (!TierMatrix.IsEdgeAllowed(consumerTier, producerTier.Value, kind))
            {
                violations.Add(new TierViolation(
                    ConsumerName: consumerName,
                    ConsumerTier: consumerTier,
                    ProducerName: dep.Name,
                    ProducerTier: producerTier.Value,
                    Kind: kind));
            }
        }
    }
}
