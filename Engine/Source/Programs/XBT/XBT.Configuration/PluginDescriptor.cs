// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.Configuration;

/// <summary>
/// In-memory representation of a <c>.xplugin</c> descriptor. Mirrors
/// Unreal's <c>.uplugin</c> in intent (JSON) with XPact-specific fields
/// per <c>/Documents/XBT.html</c> Rev 4 Section 17 + Toolchain Contract
/// Rev 13 Section 9.4.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tier</b> is derived from the descriptor's filesystem location
/// during discovery (<c>PluginEnumerator</c>): a descriptor under
/// <c>/Engine/Plugins/</c> resolves to <see cref="ModuleTier.Engine"/>,
/// <c>/Studio/Plugins/</c> to <see cref="ModuleTier.Studio"/>, etc.
/// Plugins do NOT declare their tier in the file -- the location is
/// authoritative (per Contract Section 9.4 shadow precedence rules,
/// the same plugin name may legitimately appear at multiple tiers
/// with the higher tier winning).
/// </para>
/// <para>
/// <b>EngineVersion vs. Min/Max.</b> Engine plugins ship tip-of-main
/// with the engine itself; their <see cref="Version"/> equals the
/// engine's version and <see cref="MinEngineVersion"/> /
/// <see cref="MaxEngineVersion"/> are typically null. Studio and
/// Project plugins declare a compatible engine-version range; the
/// build fails with exit 23 if the active engine falls outside the
/// range (per Contract Section 9.4 / XBT.html Section 17.3).
/// </para>
/// </remarks>
public sealed record PluginDescriptor
{
    /// <summary>
    /// Plugin name. Matches the parent directory name and the
    /// descriptor filename's stem. Required.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Plugin semver. Engine plugins equal the engine version; Studio
    /// and Project plugins have independent versions per Contract
    /// Section 9.4.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Minimum compatible engine version (inclusive). Null = no minimum
    /// (typical for Engine-tier plugins).
    /// </summary>
    public string? MinEngineVersion { get; init; }

    /// <summary>
    /// Maximum compatible engine version (inclusive). Null = no maximum
    /// (typical for Engine-tier plugins).
    /// </summary>
    public string? MaxEngineVersion { get; init; }

    /// <summary>Human-readable description. Optional.</summary>
    public string? Description { get; init; }

    /// <summary>Author name or organization. Optional.</summary>
    public string? Author { get; init; }

    /// <summary>
    /// Modules contained in this plugin. Each entry is a module name;
    /// XBT's <c>ModuleEnumerator</c> finds the corresponding
    /// <c>.Build.toml</c> under the plugin's <c>Source/</c> tree.
    /// </summary>
    public IReadOnlyList<string> Modules { get; init; } = new List<string>();

    /// <summary>
    /// Names of plugins this plugin depends on. Each dependency is
    /// resolved against the deduplicated <c>PluginCatalog</c>;
    /// unresolved entries fail with exit 60
    /// (<c>PluginNotFound</c>).
    /// </summary>
    public IReadOnlyList<string> Dependencies { get; init; } = new List<string>();

    /// <summary>
    /// True if this plugin is enabled by default. May be overridden by
    /// the project's <c>.xproject</c> <c>DisablePlugins</c> list.
    /// </summary>
    public bool EnabledByDefault { get; init; } = true;

    /// <summary>
    /// Supported platforms (e.g. <c>"Win64"</c>, <c>"Linux"</c>,
    /// <c>"Android"</c>). Empty means "all".
    /// </summary>
    public IReadOnlyList<string> Platforms { get; init; } = new List<string>();

    /// <summary>
    /// Supported target types (e.g. <c>"Editor"</c>, <c>"Game"</c>,
    /// <c>"Server"</c>). Empty means "all".
    /// </summary>
    public IReadOnlyList<string> Targets { get; init; } = new List<string>();
}
