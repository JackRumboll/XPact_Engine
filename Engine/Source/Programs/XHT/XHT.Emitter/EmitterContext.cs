// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Manifest;
using Simgenics.XPact.XHT.Resolver;

namespace Simgenics.XPact.XHT.Emitter;

/// <summary>
/// Shared state passed through the per-header and per-module emitters per
/// <c>/Documents/XHT.html</c> Rev 5 Section 8 + Section 11.3 (parallel
/// emit). Carries the resolver context (post-resolve type metadata), the
/// XBT manifest (for path-stripping + module-dep lookups), the target
/// module entry, the output directory, and a shared diagnostics
/// accumulator.
/// </summary>
/// <remarks>
/// <para>
/// <b>Plugin name resolution.</b> The Phase 1 implementation does NOT
/// thread a plugin descriptor through the manifest, so the
/// <see cref="PluginName"/> is currently derived by convention: engine-
/// tier modules get the literal <c>"Engine"</c>; studio / project modules
/// reuse the module name (the conservative non-collision form). When the
/// plugin descriptor surface lands in a future XBT addendum the
/// <see cref="PluginName"/> field can flip to the descriptor value
/// without changing the symbol grammar.
/// </para>
/// <para>
/// <b>Diagnostics list.</b> The shared list is append-only. The emitters
/// add per-file XHT070-band records (sentinel-emitted, etc.) into it;
/// callers may also seed the list with diagnostics from the resolver
/// pass so the per-module <c>.gen.manifest</c>'s <c>[Diagnostics]</c>
/// section receives the merged set.
/// </para>
/// </remarks>
/// <param name="ResolverContext">Read-only access to the resolved symbol graph.</param>
/// <param name="XbtManifest">The XBT manifest carrying module envelope + per-target info.</param>
/// <param name="Module">The module being emitted.</param>
/// <param name="OutputDirectory">Absolute path of the per-module output directory (typically <c>Intermediate/.../Generated/&lt;Module&gt;/</c>).</param>
/// <param name="Diagnostics">Shared diagnostics accumulator.</param>
public sealed record EmitterContext(
    ResolverContext ResolverContext,
    XbtManifest XbtManifest,
    XbtModule Module,
    string OutputDirectory,
    List<DiagnosticRecord> Diagnostics)
{
    /// <summary>
    /// Plugin name to feed the FileId encoder. Phase 1 default: the
    /// literal <c>"Engine"</c> for engine-tier modules, the module name
    /// for studio / project modules. Tests may override by constructing
    /// the record with the desired value via <c>with</c>.
    /// </summary>
    public string PluginName { get; init; } = Module.Tier == ModuleTier.Engine ? "Engine" : Module.Name;

    /// <summary>
    /// Shortcut for <see cref="XbtTargetInfo.ManglingScheme"/>. Surfaced
    /// on the context so emitter call-sites don't need to drill through
    /// the manifest record. Per Round-2 audit C1, the emit pipeline
    /// validates this against <see cref="SymbolNaming.Phase1ManglingScheme"/>
    /// before producing symbols; mismatches surface as diagnostic XHT124.
    /// </summary>
    public string ManglingScheme => XbtManifest.Target.ManglingScheme;

    /// <summary>
    /// Shortcut for <see cref="XbtTargetInfo.GCRootABI"/>. Emitted as a
    /// <c>static_assert</c> pin at <c>.gen.cpp</c> scope so a runtime
    /// rebuilt against a different ABI fails the compile loudly per
    /// Round-2 audit C1.
    /// </summary>
    public string GCRootABI => XbtManifest.Target.GCRootABI;

    /// <summary>
    /// Shortcut for <see cref="XbtTargetInfo.ExceptionABI"/>. Emitted as
    /// a <c>static_assert</c> pin at <c>.gen.cpp</c> scope per
    /// Round-2 audit C1.
    /// </summary>
    public string ExceptionABI => XbtManifest.Target.ExceptionABI;
}
