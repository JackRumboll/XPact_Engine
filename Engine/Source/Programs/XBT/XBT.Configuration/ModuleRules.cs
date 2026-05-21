// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.Configuration;

/// <summary>
/// In-memory representation of a single module's descriptor after a
/// <c>.Build.toml</c> + optional <c>.Build.expr</c> parse + merge, or
/// (for the Phase 1 Roslyn escape hatch per <c>/Documents/XBT.html</c>
/// Section 3.6) after a <c>.Build.cs</c> evaluation. Field-for-field
/// canonical per Toolchain Contract Rev 13 Section 9.1 and
/// <c>/Documents/XBT.html</c> Rev 4 Section 4.2.
/// </summary>
/// <remarks>
/// <para>
/// This type is the surface <c>XBT.ActionGraph</c> and
/// <c>XBT.Toolchain</c> consume. It is not what developers write;
/// developers write TOML. The parser
/// (<see cref="BuildTomlParser"/>) constructs instances of this type.
/// </para>
/// <para>
/// <b>Derivability.</b> The class is non-sealed so the Phase 1 Roslyn
/// escape hatch (<c>.Build.cs</c>, see <see cref="BuildCsCompiler"/>)
/// can have the user write
/// <c>public sealed class MyModuleBuild : ModuleRules</c> with a
/// constructor receiving <see cref="TargetRules"/>. Contract Section 9.1
/// declares the base shape as <c>abstract</c>; in this implementation
/// it is concrete (not abstract) so the TOML parser can instantiate
/// <see cref="ModuleRules"/> directly without spinning a one-off
/// derived type. Treat the type as if it were abstract from the
/// descriptor-author's perspective: every C# descriptor derives from it.
/// </para>
/// <para>
/// <b>Mutability.</b> Init-only scalar properties and pre-allocated
/// collection properties (<see cref="System.Collections.Generic.List{T}"/>).
/// Init-only properties may only be set by the parser at construction
/// time; the collections may have items appended (e.g. by a Roslyn
/// <c>.Build.cs</c>) but the property references themselves are not
/// re-assignable. Once handed to the action graph, the instance is
/// treated as immutable.
/// </para>
/// <para>
/// <b>Determinism.</b> All collection properties preserve insertion
/// order. The TOML parser sorts inputs alphabetically before
/// populating to guarantee identical output for two builds of the
/// same source.
/// </para>
/// </remarks>
public class ModuleRules
{
    // -----------------------------------------------------------------
    // Identity (Contract Section 9.1; XBT.html Section 4.2)
    // -----------------------------------------------------------------

    /// <summary>
    /// Module name. Matches the producing module's directory name and
    /// the <c>Name</c> field used by every dependent's <see cref="ModuleDep"/>.
    /// Required.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Three-tier reusability classification per master plan Section 2.
    /// The check on cross-tier dependency edges (<see cref="TierMatrix"/>)
    /// operates on this declared value, NOT on the source-tree path.
    /// </summary>
    public ModuleTier Tier { get; init; }

    /// <summary>
    /// Unreal-convention subfolder discriminator inside the tier's
    /// <c>Source/</c> directory. Per Contract Rev 13 Section 10.2 the
    /// FBS ordinals are append-only: Runtime=0, Editor=1, Developer=2,
    /// ThirdParty=3, Programs=4.
    /// </summary>
    public ModuleType ModuleType { get; init; }

    /// <summary>
    /// Language surface emitted by this module. C++ only, C# only, or
    /// both within one DLL per master plan Section 2 Module composition
    /// row. Defaults to <see cref="Languages.Cpp"/>.
    /// </summary>
    public Languages Languages { get; init; } = Languages.Cpp;

    /// <summary>
    /// Windows MAX_PATH escape hatch per <c>/Documents/XBT.html</c>
    /// Section 18.3. When non-null, replaces <see cref="Name"/> in path
    /// construction for intermediate / binary outputs. Optional.
    /// </summary>
    public string? ShortName { get; init; }

    /// <summary>
    /// Audit fix R8-M3: first 16 hex characters of the BLAKE3 content
    /// hash of the descriptor file this module was loaded from (the
    /// <c>.Build.toml</c> or <c>.Build.cs</c> on disk). Flows through
    /// every toolchain emit site's
    /// <see cref="ActionGraph.IExternalAction.CacheKeyComponents"/> so
    /// a descriptor edit -- even one that does not change the parsed
    /// <see cref="ModuleRules"/> values in any user-visible way (e.g.
    /// adding a comment, reordering a list, adding a conditional
    /// include path that resolves to nothing on this build but might
    /// on the next) -- correctly invalidates the cached compiles for
    /// the module.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the TOML parser path: set at construction time by
    /// <see cref="BuildTomlParser.ParseFile"/>. For the Roslyn
    /// (.Build.cs) escape-hatch path: set by
    /// <see cref="BuildCsCompiler.Compile"/> via
    /// <see cref="ApplyDescriptorContentHash"/> immediately after the
    /// user's constructor returns (the user's constructor cannot set
    /// it because the discovery layer is the one that holds the
    /// descriptor bytes' hash).
    /// </para>
    /// <para>
    /// The setter is <c>private set</c> so user-authored
    /// <c>.Build.cs</c> code cannot accidentally fake a hash; the
    /// in-assembly <see cref="ApplyDescriptorContentHash"/> method is
    /// the only legitimate write path.
    /// </para>
    /// <para>
    /// Test-only construction sites that synthesise a
    /// <c>ModuleRules</c> without a descriptor on disk leave this
    /// null; the toolchain's cache-key emit falls back to a sentinel
    /// in that case (the cache key remains stable across the test
    /// process so two test reads agree).
    /// </para>
    /// <para>
    /// The hash is truncated to 16 hex chars to match the cache-key
    /// component readability discipline used by
    /// <c>EnvelopeFlagsHash</c> / <c>XbtBinaryHash</c>.
    /// </para>
    /// </remarks>
    public string? DescriptorContentHash { get; private set; }

    /// <summary>
    /// Audit fix R8-M3: set the descriptor content hash on this
    /// instance. Intentionally not <c>init</c> so the Roslyn escape-
    /// hatch path (where the user's constructor populates every other
    /// property and the discovery layer then injects the hash) can
    /// reach it. The method is internal so user-authored
    /// <c>.Build.cs</c> code cannot fake the value -- only the
    /// in-assembly <see cref="BuildTomlParser"/> and
    /// <see cref="BuildCsCompiler"/> are legitimate writers.
    /// </summary>
    /// <param name="descriptorContentHash">
    /// First 16 hex chars of the descriptor's BLAKE3 hash. Pass null
    /// to clear (test-only construction).
    /// </param>
    internal void ApplyDescriptorContentHash(string? descriptorContentHash)
    {
        DescriptorContentHash = descriptorContentHash;
    }

    // -----------------------------------------------------------------
    // Sim-path declaration (Contract Section 4 + XBT.html Section 3.2)
    // -----------------------------------------------------------------

    /// <summary>
    /// True if this module participates in the lockstep simulation path.
    /// SimPath modules are subject to additional constraints
    /// (<see cref="PCHUsage"/> forced to <see cref="PCHUsageMode.NoSharedPCHs"/>,
    /// <see cref="FPSemantics"/> forced to <see cref="FPSemantics.Precise"/>,
    /// <see cref="SimdLevel"/> clamped to <c>&lt;= SSE42</c>); see
    /// <c>/Documents/XBT.html</c> Section 4.5.
    /// </summary>
    public bool SimPath { get; init; } = false;

    /// <summary>
    /// Allow the GC conservative-roots-only mode on this SimPath module.
    /// Per Contract Rev 12 Section 3.2; rarely set.
    /// </summary>
    public bool SimPathConservativeRootsAllowed { get; init; } = false;

    // -----------------------------------------------------------------
    // SIMD lever (Contract Rev 12 Section 4.2 N7; XBT.html Section 4.6)
    // -----------------------------------------------------------------

    /// <summary>
    /// Per-module SIMD baseline. <see cref="SimdLevel.Default"/> resolves
    /// at flag-derivation time by reading <see cref="TargetRules.SimdLevelDefault"/>.
    /// SimPath modules are additionally clamped to <c>&lt;= SSE42</c>; an
    /// AVX-or-higher value on a SimPath module fails the build with exit
    /// code 41.
    /// </summary>
    public SimdLevel SimdLevel { get; init; } = SimdLevel.Default;

    // -----------------------------------------------------------------
    // Dependencies (Contract Section 9.1; Rev 13 reconciliation)
    // -----------------------------------------------------------------

    /// <summary>
    /// Link-time public dependencies. The consumer's headers transitively
    /// see the producer's public include paths. Per-dep
    /// <see cref="ModuleDep.InterfaceModule"/> flag may be set to mark
    /// a header-only-no-link dependency (replaces UE's
    /// <c>PublicIncludePathModuleNames</c>; Rev 11 audit finding #9).
    /// </summary>
    public List<ModuleDep> PublicDependencyModuleNames { get; init; } = new();

    /// <summary>
    /// Link-time private dependencies. No include propagation to the
    /// consumer's downstream consumers.
    /// </summary>
    public List<ModuleDep> PrivateDependencyModuleNames { get; init; } = new();

    /// <summary>
    /// Runtime-load dependencies (<c>LoadLibrary</c> / <c>dlopen</c>).
    /// Per Contract Rev 13 Section 9.1 these are typed
    /// <see cref="ModuleDep"/> like the other two lists; the per-dep
    /// <see cref="ModuleDep.InterfaceModule"/> flag is meaningless for
    /// dynamic deps and is always false. Per the cross-tier matrix
    /// (<see cref="TierMatrix"/>), upward edges are permitted here.
    /// </summary>
    public List<ModuleDep> DynamicallyLoadedModuleNames { get; init; } = new();

    // -----------------------------------------------------------------
    // Include paths (Contract Section 9.1)
    // -----------------------------------------------------------------

    /// <summary>
    /// Public include directories. Visible to consumers via transitive
    /// propagation through <see cref="PublicDependencyModuleNames"/>.
    /// </summary>
    public List<string> PublicIncludePaths { get; init; } = new();

    /// <summary>
    /// Private include directories. Visible only inside this module's
    /// own translation units.
    /// </summary>
    public List<string> PrivateIncludePaths { get; init; } = new();

    // -----------------------------------------------------------------
    // Preprocessor (Contract Section 9.1)
    // -----------------------------------------------------------------

    /// <summary>
    /// Public preprocessor definitions. Format: <c>"NAME"</c> or
    /// <c>"NAME=VALUE"</c>. Propagates to consumers.
    /// </summary>
    public List<string> PublicDefinitions { get; init; } = new();

    /// <summary>
    /// Private preprocessor definitions. Visible only inside this
    /// module's own translation units.
    /// </summary>
    public List<string> PrivateDefinitions { get; init; } = new();

    // -----------------------------------------------------------------
    // Compile environment (Contract Section 9.1)
    // -----------------------------------------------------------------

    /// <summary>
    /// Pre-compiled-header usage mode. SimPath modules must use
    /// <see cref="PCHUsageMode.NoSharedPCHs"/>; any other value on a
    /// SimPath module fails validation with exit 30.
    /// </summary>
    public PCHUsageMode PCHUsage { get; init; } = PCHUsageMode.Default;

    /// <summary>
    /// Optional private PCH header path (module-relative) consumed by
    /// every TU in the module when <see cref="PCHUsage"/> is in a
    /// PCH-emitting mode. Mirrors UE's <c>PrivatePCHHeaderFile</c>.
    /// Null = no per-module PCH (the module's TUs compile without
    /// <c>/Yu</c> / <c>-include-pch</c>).
    /// </summary>
    public string? PrivatePCHHeaderFile { get; init; } = null;

    /// <summary>
    /// Optional path (module-relative) to a header file this module wants
    /// to share as a PCH with other modules that name the same header.
    /// When two or more modules declare the same <see cref="SharedPCHHeaderFile"/>
    /// (resolved to the same absolute canonical path) and have compatible
    /// <see cref="PCHUsage"/> settings, XBT emits a single
    /// <c>PCHGenerationAction</c> for the group and binds all participants
    /// to its output. A single-participant declaration falls back to
    /// private-PCH semantics with a <see cref="Core.Logger.Info"/>
    /// diagnostic.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per Toolchain Contract Rev 13 Section 1.5 / <c>/Documents/XBT.html</c>
    /// Section 15.4:
    /// </para>
    /// <list type="bullet">
    ///   <item>SimPath modules MUST NOT participate in a shared PCH group;
    ///   <see cref="SharedPCHHeaderFile"/> must be null on SimPath modules.
    ///   Parser-side validation rejects with exit 30; toolchain-side
    ///   <c>GenerateSharedPCH</c> re-checks at emit time with exit 41.</item>
    ///   <item>The shared PCH header MUST NOT <c>#include</c> any
    ///   <c>.gen.h</c> file (XHT-generated content carries per-module macro
    ///   state that would leak across consumers).</item>
    ///   <item>Mutually exclusive with <see cref="PrivatePCHHeaderFile"/>;
    ///   a module that declares both is a parse failure (exit 30).</item>
    /// </list>
    /// </remarks>
    public string? SharedPCHHeaderFile { get; init; } = null;

    /// <summary>
    /// Floating-point semantics. Auto-promoted from
    /// <see cref="FPSemantics.Default"/> to <see cref="FPSemantics.Precise"/>
    /// when <see cref="SimPath"/> is true. Explicit
    /// <see cref="FPSemantics.Imprecise"/> on a SimPath module fails
    /// validation with exit 41 (banned-flag check).
    /// </summary>
    public FPSemantics FPSemantics { get; init; } = FPSemantics.Default;

    /// <summary>
    /// Per-module code-optimization mode. Translates to the toolchain's
    /// <c>/O</c> / <c>-O</c> flag at compile time.
    /// </summary>
    public OptimizeCodeMode OptimizeCode { get; init; } = OptimizeCodeMode.Default;

    /// <summary>
    /// True if C++ exceptions are enabled for translation units in this
    /// module. Defaults to true. The exception ABI itself is governed by
    /// Contract Section 5 (Tier 1 / Tier 2 calling-convention tiers).
    /// </summary>
    public bool bEnableExceptions { get; init; } = true;

    /// <summary>
    /// True if C++ RTTI is enabled. Defaults to false (RTTI is rarely
    /// useful in XPact and adds size + slowdown).
    /// </summary>
    public bool bUseRTTI { get; init; } = false;

    /// <summary>
    /// True if this module participates in unity-build clustering per
    /// <c>/Documents/XBT.html</c> Section 4.8. Defaults to true.
    /// </summary>
    public bool bUseUnity { get; init; } = true;

    /// <summary>
    /// True if compile warnings on this module's TUs are promoted to
    /// errors. Defaults to true (XPact is warning-clean engine-wide).
    /// </summary>
    public bool bWarningsAsErrors { get; init; } = true;

    /// <summary>
    /// True if this module's <c>.gen.h</c> is excluded from any
    /// SharedPCH per <c>/Documents/XBT.html</c> Section 15.4. Defaults
    /// to false; SimPath modules effectively set this through the
    /// <see cref="PCHUsage"/> constraint.
    /// </summary>
    public bool bExcludeFromSharedPCH { get; init; } = false;

    // -----------------------------------------------------------------
    // Hot-reload (master plan Section 2 hot-reload cascade row, Rev 10)
    // -----------------------------------------------------------------

    /// <summary>
    /// Per-module opt-in for Phase 2 XLiveCoding hot-reload. Effective
    /// only when the enforcement matrix in <c>/Documents/XBT.html</c>
    /// Section 16.4 permits it (e.g. SimPath modules can never
    /// hot-reload regardless of this flag).
    /// </summary>
    public bool bAllowHotReload { get; init; } = false;

    // -----------------------------------------------------------------
    // Phase 2 readiness fields (Contract Rev 11 audit finding #15)
    // -----------------------------------------------------------------

    /// <summary>
    /// True if this module participates only in test target builds.
    /// The action graph filters test modules out of Game / Server
    /// targets at link time so test-only code reaches zero in shipped
    /// binaries.
    /// </summary>
    public bool bIsTestModule { get; init; } = false;

    /// <summary>
    /// Optional human-readable deprecation message. When non-null, XBT
    /// emits a warning naming the module and the message on every build
    /// that references the module. Used during hoist transitions and
    /// plugin retirement.
    /// </summary>
    public string? DeprecationMessage { get; init; } = null;

    /// <summary>
    /// Optional minimum XBT toolchain version (semver) required by this
    /// module. When non-null, XBT compares this against its own version
    /// at module-resolution time and fails the build with exit
    /// <strong>23</strong> if its version is older. Per
    /// <c>/Documents/XBT.html</c> Section 4.2 Rev 3 exit-code fix
    /// (Rev 2's code 10 was incorrect).
    /// </summary>
    public string? MinimumToolchainVersion { get; init; } = null;

    /// <summary>
    /// Optional engine-version compatibility specifier. Default <c>"*"</c>
    /// (any-version-compatible). Per-module versioning so a single
    /// module can declare a tighter compatibility range than the plugin
    /// containing it. Surfaced into the manifest's
    /// <c>Module.EngineVersionCompat</c> field.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Audit fix M8: previously hardcoded to <c>"*"</c> at manifest
    /// emission time. The field is declared here so a future
    /// <c>.Build.toml</c> / <c>.Build.cs</c> parser revision can author
    /// it; XBT's BuildMode surfaces this value into the manifest when
    /// set, otherwise falls back to <c>"*"</c>.
    /// </para>
    /// </remarks>
    public string EngineVersionCompat { get; init; } = "*";

    // -----------------------------------------------------------------
    // Typed build hooks (Contract Rev 12 Section 9.5)
    // -----------------------------------------------------------------

    /// <summary>
    /// Pre-build hooks run before any of this module's compile actions
    /// schedule. Each hook contributes a first-class
    /// <see cref="IBuildHook"/>-emitted <c>IExternalAction</c> to the
    /// graph; the action is cached, incrementalizable, and parallelizable.
    /// </summary>
    public List<IBuildHook> PreBuildHooks { get; init; } = new();

    /// <summary>
    /// Post-build hooks run after this module's link action completes.
    /// Same action-graph integration as <see cref="PreBuildHooks"/>.
    /// </summary>
    public List<IBuildHook> PostBuildHooks { get; init; } = new();
}
