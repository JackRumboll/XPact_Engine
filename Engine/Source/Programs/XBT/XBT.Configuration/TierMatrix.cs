// Copyright Simgenics. All Rights Reserved.

using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.Configuration;

/// <summary>
/// Discriminator for the kind of dependency edge being checked by
/// <see cref="TierMatrix.IsEdgeAllowed"/>.
/// </summary>
public enum DependencyKind
{
    /// <summary>
    /// Link-time dependency from
    /// <see cref="ModuleRules.PublicDependencyModuleNames"/> or
    /// <see cref="ModuleRules.PrivateDependencyModuleNames"/>. Both
    /// behave identically for tier-validation purposes; the public/private
    /// distinction only affects include-path propagation.
    /// </summary>
    Link = 0,

    /// <summary>
    /// Runtime-load dependency from
    /// <see cref="ModuleRules.DynamicallyLoadedModuleNames"/>. Upward
    /// edges (e.g. Engine -> Studio) are permitted here to support
    /// hot-reload and plugin-load patterns; that is the rule the
    /// matrix encodes.
    /// </summary>
    Dynamic = 1,
}

/// <summary>
/// Cross-tier dependency-edge matrix per Toolchain Contract Rev 13
/// Section 9.3 and <c>/Documents/XBT.html</c> Rev 4 Section 13.1.
/// Implements the authoritative rule: link deps point downward only
/// (Engine &larr; Studio &larr; Project); dynamic-load deps may point
/// in any direction including upward (the audit fix that preserved
/// the hot-reload model).
/// </summary>
/// <remarks>
/// <para>
/// <b>The matrix</b> (Contract Section 9.3 + XBT.html Section 13.1):
/// </para>
/// <code>
/// From       | To        | Link      | Dynamic
/// -----------|-----------|-----------|--------
/// Engine     | Engine    | ALLOWED   | ALLOWED
/// Engine     | Studio    | BANNED    | ALLOWED  (the audit-finding fix)
/// Engine     | Project   | BANNED    | ALLOWED
/// Studio     | Engine    | ALLOWED   | ALLOWED
/// Studio     | Studio    | ALLOWED   | ALLOWED
/// Studio     | Project   | BANNED    | ALLOWED
/// Project    | Engine    | ALLOWED   | ALLOWED
/// Project    | Studio    | ALLOWED   | ALLOWED
/// Project    | Project   | ALLOWED   | ALLOWED
/// </code>
/// <para>
/// <b>Why upward dynamic loads are allowed.</b> The Rev 10 rule "tier
/// dependencies must point downward only" was over-broad: it broke the
/// hot-reload model entirely, because Engine modules consume Studio
/// plugins at runtime via XLiveCoding / plugin load. The Rev 11 audit
/// (Contract Section 9.3) replaced the blanket rule with this matrix.
/// </para>
/// <para>
/// <b>Same-tier link edges</b> are always allowed (Engine -> Engine,
/// Studio -> Studio, Project -> Project). The directional rule applies
/// only to <em>strict</em> upward link edges.
/// </para>
/// </remarks>
public static class TierMatrix
{
    /// <summary>
    /// Test whether a dependency edge from a module in tier
    /// <paramref name="fromTier"/> to a module in tier
    /// <paramref name="toTier"/> of kind <paramref name="kind"/> is
    /// permitted by the matrix.
    /// </summary>
    /// <param name="fromTier">The tier of the consuming module (edge tail).</param>
    /// <param name="toTier">The tier of the producing module (edge head).</param>
    /// <param name="kind">
    /// <see cref="DependencyKind.Link"/> for entries in
    /// <see cref="ModuleRules.PublicDependencyModuleNames"/> or
    /// <see cref="ModuleRules.PrivateDependencyModuleNames"/>;
    /// <see cref="DependencyKind.Dynamic"/> for entries in
    /// <see cref="ModuleRules.DynamicallyLoadedModuleNames"/>.
    /// </param>
    /// <returns>
    /// True iff the edge is allowed. False values must produce a
    /// build failure with Toolchain Contract Rev 13 Section 13 exit
    /// code 21 (<c>TierViolation</c>); see <see cref="TierValidator"/>
    /// for the wrapper that produces those diagnostics.
    /// </returns>
    public static bool IsEdgeAllowed(ModuleTier fromTier, ModuleTier toTier, DependencyKind kind)
    {
        // Dynamic edges are always allowed in any direction, per the
        // audit-finding fix that preserves the hot-reload model.
        // Engine -> Studio and Engine -> Project dynamic loads are how
        // the engine consumes plugins at runtime.
        if (kind == DependencyKind.Dynamic)
        {
            return true;
        }

        // Link edges (Public / Private dep lists): downward or
        // same-tier only. Use the numeric ordinal of ModuleTier where
        // Engine=0, Studio=1, Project=2 -- a Project (2) module can
        // depend on a Studio (1) module (2 -> 1 is allowed) but a
        // Studio module CANNOT depend on a Project module (1 -> 2 is
        // banned).
        int from = (int)fromTier;
        int to = (int)toTier;
        return to <= from;
    }
}
