// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.Configuration;

/// <summary>
/// In-memory representation of a single build target's descriptor.
/// Field-for-field canonical per <c>/Documents/XBT.html</c> Rev 4
/// Section 4.3.
/// </summary>
/// <remarks>
/// <para>
/// Target descriptors are written in TOML at
/// <c>{Project}/{TargetName}.Target.toml</c> for game / editor / server
/// targets. The parser produces an instance of this type which becomes
/// the source for the read-only target view (<see cref="ReadOnlyTargetRules"/>)
/// surfaced to descriptor evaluation and the toolchain abstraction.
/// </para>
/// <para>
/// <b>SimdLevelDefault</b> is the production default of
/// <see cref="SimdLevel.SSE42"/>. Modules declaring
/// <see cref="SimdLevel.Default"/> resolve to this value at
/// flag-derivation time. Per Contract Rev 12 Section 4.2 N7.
/// </para>
/// </remarks>
public sealed class TargetRules
{
    /// <summary>
    /// Target name. Matches the <c>.Target.toml</c> filename's stem.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Editor / Game / Server discriminator. Per master plan Section 2
    /// Build targets row.
    /// </summary>
    public BuildTargetType TargetType { get; init; }

    /// <summary>
    /// Build configuration: Debug / DebugGame / Development / Test /
    /// Shipping. Affects optimization, exception lowering (Tier 1
    /// shim vs. <c>Result&lt;T,E&gt;</c> per Contract Section 5),
    /// asset stripping, etc.
    /// </summary>
    public BuildConfiguration Configuration { get; init; }

    /// <summary>
    /// Target platform: Win64 / Linux / Android.
    /// </summary>
    public Platform Platform { get; init; }

    /// <summary>
    /// CPU architecture string (e.g. <c>"x86_64"</c>, <c>"aarch64"</c>).
    /// Defaults to the host-default for the platform when unset.
    /// </summary>
    public string Architecture { get; init; } = "x86_64";

    /// <summary>
    /// Android NDK API level used to compose the per-API-level Clang
    /// target triple (e.g. <c>aarch64-linux-android24</c>). Per audit
    /// fix R8-C1: the NDK ships a generic <c>bin/clang</c> driver that
    /// defaults to the host triple unless <c>--target=&lt;arch&gt;-linux-android&lt;API&gt;</c>
    /// is passed explicitly. Without that flag, codegen produces
    /// host-architecture object files (x86-64 on Win64 / Linux build
    /// hosts) which the Android linker then refuses to combine into an
    /// <c>.so</c>. Phase 1 default 21 (Android 5.0 Lollipop, the
    /// minimum NDK r26 supports for 64-bit targets); per-target
    /// override via <c>.Target.toml</c> when a project requires a
    /// higher minimum.
    /// </summary>
    /// <remarks>
    /// Only consulted on <see cref="Platform"/> == <see cref="Platform.Android"/>;
    /// ignored on Win64 / Linux. <see cref="Architecture"/> drives the
    /// triple's architecture prefix (e.g. <c>aarch64</c>,
    /// <c>armv7a</c>, <c>x86_64</c>, <c>i686</c> per NDK r26
    /// conventions). XPact's primary Android target is
    /// <c>aarch64</c> per master plan §2.
    /// </remarks>
    public int AndroidApiLevel { get; init; } = 21;

    /// <summary>
    /// Station role for Game targets per <c>/Documents/XBT.html</c>
    /// Section 4.10. <see cref="StationRole.None"/> for Editor / Server
    /// targets. Game targets receive the
    /// <c>-DXPACT_STATION_ROLE=&lt;value&gt;</c> compile define.
    /// </summary>
    public StationRole StationRole { get; init; } = StationRole.None;

    /// <summary>
    /// Default SIMD level used when a module's
    /// <see cref="ModuleRules.SimdLevel"/> is
    /// <see cref="SimdLevel.Default"/>. Production value is
    /// <see cref="SimdLevel.SSE42"/> per Contract Rev 12 Section 4.2 N7.
    /// </summary>
    public SimdLevel SimdLevelDefault { get; init; } = SimdLevel.SSE42;

    /// <summary>
    /// FIPS mode. When true, crypto subsystems substitute FIPS-validated
    /// implementations. Per master plan Section 2 Network security row
    /// Rev 8.
    /// </summary>
    public bool FipsMode { get; init; } = false;

    /// <summary>
    /// Allow modules declaring
    /// <see cref="ModuleRules.SimPathConservativeRootsAllowed"/> to
    /// use the conservative-roots GC mode in this target. Default
    /// false. Per Contract Rev 12 Section 3.2.
    /// </summary>
    public bool SimPathConservativeRootsAllowed { get; init; } = false;

    /// <summary>
    /// Allow hot-reload on this target. False on Shipping and Server
    /// per <c>/Documents/XBT.html</c> Section 16.4. Defaults to false
    /// at the target level; per-module opt-in via
    /// <see cref="ModuleRules.bAllowHotReload"/> remains required.
    /// </summary>
    public bool bAllowHotReload { get; init; } = false;

    /// <summary>
    /// True if unity-build clustering is enabled for this target.
    /// Defaults to true; per-module opt-out via
    /// <see cref="ModuleRules.bUseUnity"/> remains available.
    /// </summary>
    public bool bUseUnity { get; init; } = true;

    /// <summary>
    /// Editor target marker. Implied by
    /// <see cref="TargetType"/> == <see cref="BuildTargetType.Editor"/>;
    /// the explicit flag is preserved for descriptor-level overrides.
    /// </summary>
    public bool bWithEditor { get; init; } = false;

    /// <summary>
    /// Server target marker. Implied by
    /// <see cref="TargetType"/> == <see cref="BuildTargetType.Server"/>.
    /// </summary>
    public bool bWithServer { get; init; } = false;

    /// <summary>
    /// Modules forcibly enabled at this target (in addition to whatever
    /// the module dependency graph would pull in).
    /// </summary>
    public List<string> AdditionalModules { get; init; } = new();

    /// <summary>
    /// Modules forcibly disabled at this target. Disabled modules do
    /// not enter the dependency graph; their declared deps are silently
    /// dropped from consumers per Contract Section 9.3.
    /// </summary>
    public List<string> DisableModules { get; init; } = new();

    /// <summary>
    /// Plugin version pins. Map keys are plugin names; values are the
    /// pinned semver strings. Per master plan Section 2 Plugin
    /// versioning row Rev 8.
    /// </summary>
    public Dictionary<string, string> PluginPins { get; init; } = new();
}

/// <summary>
/// Read-only view over a <see cref="TargetRules"/> instance passed into
/// descriptor evaluation. The <c>target.*</c> bound names in the
/// expression sub-language (Contract Section 9.6) resolve through this
/// view; mutation of the underlying target by an expression is therefore
/// impossible at the API level.
/// </summary>
/// <remarks>
/// The wrapped <see cref="TargetRules"/> is itself effectively immutable
/// once the parser has finished populating it (init-only properties on
/// scalars; collections appended only at construction). The read-only
/// view is a defence-in-depth layer that matches the Contract's
/// no-mutation sandbox requirement.
/// </remarks>
public sealed class ReadOnlyTargetRules
{
    private readonly TargetRules _inner;

    /// <summary>
    /// Wrap a <see cref="TargetRules"/> instance.
    /// </summary>
    public ReadOnlyTargetRules(TargetRules inner)
    {
        _inner = inner;
    }

    /// <summary>Target name; see <see cref="TargetRules.Name"/>.</summary>
    public string Name => _inner.Name;

    /// <summary>Target type; see <see cref="TargetRules.TargetType"/>.</summary>
    public BuildTargetType TargetType => _inner.TargetType;

    /// <summary>Build configuration; see <see cref="TargetRules.Configuration"/>.</summary>
    public BuildConfiguration Configuration => _inner.Configuration;

    /// <summary>Target platform; see <see cref="TargetRules.Platform"/>.</summary>
    public Platform Platform => _inner.Platform;

    /// <summary>CPU architecture; see <see cref="TargetRules.Architecture"/>.</summary>
    public string Architecture => _inner.Architecture;

    /// <summary>Android NDK API level; see <see cref="TargetRules.AndroidApiLevel"/>.</summary>
    public int AndroidApiLevel => _inner.AndroidApiLevel;

    /// <summary>Station role; see <see cref="TargetRules.StationRole"/>.</summary>
    public StationRole StationRole => _inner.StationRole;

    /// <summary>Default SIMD level; see <see cref="TargetRules.SimdLevelDefault"/>.</summary>
    public SimdLevel SimdLevelDefault => _inner.SimdLevelDefault;

    /// <summary>FIPS mode flag; see <see cref="TargetRules.FipsMode"/>.</summary>
    public bool FipsMode => _inner.FipsMode;

    /// <summary>Hot-reload target-level allow flag.</summary>
    public bool bAllowHotReload => _inner.bAllowHotReload;

    /// <summary>Unity-build target-level enable flag.</summary>
    public bool bUseUnity => _inner.bUseUnity;
}
