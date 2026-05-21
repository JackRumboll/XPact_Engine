// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Manifest;
using Simgenics.XPact.XHT.Tables;

namespace Simgenics.XPact.XHT.Resolver;

/// <summary>
/// State carried across the seven-active-phase resolve pipeline per
/// <c>/Documents/XHT.html</c> Rev 8 Section 5. Stores resolver-discovered
/// type pointers in <see cref="System.Collections.Generic.Dictionary{TKey, TValue}"/>
/// maps rather than mutating the immutable AST records.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why map-based storage.</b> <see cref="XhtTypeBase"/> records are
/// declared immutable for parser-output safety (AST records are frozen
/// once registered with <see cref="SymbolTable"/>). The resolver
/// pipeline could rebuild every node via the C#-record <c>with</c>
/// expression on each phase, but that would re-key the symbol table
/// and break referential equality. Instead, the resolver appends its
/// discovered links into the maps below, keyed by the AST node's
/// reference identity (<see cref="object.ReferenceEquals(object?, object?)"/>
/// semantics via the default <c>Dictionary</c> comparer for reference
/// types). Downstream consumers (Phase 1e emitter) read both the AST
/// node and the matching map entry.
/// </para>
/// <para>
/// <b>Diagnostics list.</b> The shared <see cref="Diagnostics"/> list is
/// append-only across phases. The first cut (Phase 1d) walks types
/// sequentially per phase; once parallelism lands at Phase 1d+, the
/// caller is expected to provide a thread-safe diagnostics sink (the
/// resolver does not internally lock the list).
/// </para>
/// </remarks>
public sealed class ResolverContext
{
    /// <summary>
    /// Construct a fresh context wrapping the supplied symbol table,
    /// specifier registry, manifest, and module-under-resolve name. All
    /// resolver-discovered maps start empty; the
    /// <paramref name="diagnostics"/> list is shared across phases.
    /// </summary>
    /// <param name="symbols">Populated symbol table from the parser pass; must not be null.</param>
    /// <param name="specifierRegistry">Specifier registry providing definition lookup; must not be null.</param>
    /// <param name="manifest">XBT manifest carrying module-dependency edges; must not be null.</param>
    /// <param name="moduleName">The module the pipeline is resolving (the consumer module for cross-tier checks); must not be null.</param>
    /// <param name="diagnostics">Shared diagnostics accumulator; must not be null.</param>
    public ResolverContext(
        SymbolTable symbols,
        ISpecifierRegistry specifierRegistry,
        XbtManifest manifest,
        string moduleName,
        List<DiagnosticRecord> diagnostics)
    {
        System.ArgumentNullException.ThrowIfNull(symbols);
        System.ArgumentNullException.ThrowIfNull(specifierRegistry);
        System.ArgumentNullException.ThrowIfNull(manifest);
        System.ArgumentNullException.ThrowIfNull(moduleName);
        System.ArgumentNullException.ThrowIfNull(diagnostics);

        Symbols = symbols;
        SpecifierRegistry = specifierRegistry;
        Manifest = manifest;
        ModuleName = moduleName;
        Diagnostics = diagnostics;

        InterfacePairings = new Dictionary<XhtInterface, XhtClass>();
        ResolvedSupers = new Dictionary<XhtTypeBase, XhtTypeBase>();
        ResolvedInterfaces = new Dictionary<XhtClass, List<XhtInterface>>();
        ResolvedWithin = new Dictionary<XhtClass, XhtClass>();
        ResolvedRepNotifyMethods = new Dictionary<XhtProperty, XhtFunction>();
        MergedPartials = new Dictionary<XhtClass, XhtClass>();
        PropertyContainers = new Dictionary<XhtProperty, XhtTypeBase>();
        ResolvedPropertyTypes = new Dictionary<XhtProperty, XhtTypeBase>();
        ExtraPartials = new List<XhtClass>();
        MergedSymbolView = new Dictionary<XhtTypeBase, XhtTypeBase>();
        CurrentPhase = ResolvePhase.None;
    }

    /// <summary>Populated symbol table the resolver walks.</summary>
    public SymbolTable Symbols { get; }

    /// <summary>Specifier registry used by validators for definition lookup.</summary>
    public ISpecifierRegistry SpecifierRegistry { get; }

    /// <summary>XBT manifest carrying the module-dependency closure for cross-tier validation.</summary>
    public XbtManifest Manifest { get; }

    /// <summary>The module the pipeline is currently resolving (the consumer module).</summary>
    public string ModuleName { get; }

    /// <summary>Shared diagnostics accumulator; append-only across phases.</summary>
    public List<DiagnosticRecord> Diagnostics { get; }

    /// <summary>The most-recently-completed phase. <see cref="ResolvePhase.None"/> before any phase runs.</summary>
    public ResolvePhase CurrentPhase { get; internal set; }

    /// <summary>
    /// XCLASS + XINTERFACE pairings discovered in
    /// <see cref="ResolvePhase.Pairings"/>. Key: the interface AST node;
    /// Value: the paired class (the <c>U</c>-prefix companion). A missing
    /// pairing emits <c>XHT100</c>.
    /// </summary>
    public Dictionary<XhtInterface, XhtClass> InterfacePairings { get; }

    /// <summary>
    /// Super-pointer resolutions discovered in
    /// <see cref="ResolvePhase.BindSuperAndBases"/>. Key: the derived
    /// type AST node; Value: the resolved super type. A missing super
    /// emits <c>XHT103</c>; a wrong-kind super emits <c>XHT104</c>.
    /// </summary>
    public Dictionary<XhtTypeBase, XhtTypeBase> ResolvedSupers { get; }

    /// <summary>
    /// Interface-base lists discovered in
    /// <see cref="ResolvePhase.ResolveBases"/>. Key: the implementing
    /// class; Value: the resolved interface list in declaration order.
    /// </summary>
    public Dictionary<XhtClass, List<XhtInterface>> ResolvedInterfaces { get; }

    /// <summary>
    /// <c>ClassWithin</c> outer-type resolutions discovered in
    /// <see cref="ResolvePhase.ResolveBases"/>. Key: the class carrying
    /// <c>Within=...</c>; Value: the resolved outer class. Mismatches
    /// against the super's <c>ClassWithin</c> emit <c>XHT119</c>.
    /// </summary>
    public Dictionary<XhtClass, XhtClass> ResolvedWithin { get; }

    /// <summary>
    /// <c>ReplicatedUsing=...</c> callback resolutions discovered in
    /// <see cref="ResolvePhase.Properties"/>. Key: the property carrying
    /// <c>ReplicatedUsing=&lt;funcName&gt;</c>; Value: the resolved
    /// callback function. Signature mismatches emit <c>XHT113</c>.
    /// </summary>
    public Dictionary<XhtProperty, XhtFunction> ResolvedRepNotifyMethods { get; }

    /// <summary>
    /// Partial-class duplicates discovered in
    /// <see cref="ResolvePhase.Pairings"/>. Key: the duplicate (later)
    /// partial-class entry; Value: the canonical (first-registered)
    /// entry. Each merge emits info-severity <c>XHT143</c>.
    /// </summary>
    public Dictionary<XhtClass, XhtClass> MergedPartials { get; }

    /// <summary>
    /// Owning-type lookup for properties, populated during
    /// <see cref="ResolvePhase.Properties"/> so cross-language /
    /// cross-tier checks at <see cref="ResolvePhase.Final"/> can find a
    /// property's container.
    /// </summary>
    public Dictionary<XhtProperty, XhtTypeBase> PropertyContainers { get; }

    /// <summary>
    /// Resolved property-type lookups discovered in
    /// <see cref="ResolvePhase.Properties"/>. Key: the property; Value:
    /// the resolved reflected type referent (for container properties
    /// this is the inner element type when discoverable). Primitive
    /// types resolve to a null entry (not present in the map).
    /// </summary>
    public Dictionary<XhtProperty, XhtTypeBase> ResolvedPropertyTypes { get; }

    /// <summary>
    /// Extra C# partial-class declarations the parser walker(s) saw but
    /// could not register because the canonical (first-registered)
    /// declaration already occupied the caseless engine-name key. The
    /// resolver's <c>StepResolvePairings</c> phase reads this list to
    /// drive the partial merge per C3 audit (XHT.html Section 3.3).
    /// The <c>EmitModuleMode</c> populates this list from each
    /// walker's <c>ExtraPartials</c> surface after the parser pass.
    /// </summary>
    public List<XhtClass> ExtraPartials { get; }

    /// <summary>
    /// Merged-shape lookup for types that were folded across multiple
    /// declarations (currently: C# partial-class merges). Key: the
    /// canonical (first-registered) AST node as held in
    /// <see cref="Symbols"/>; Value: the merged shape (union of
    /// Functions / Properties / Specifiers / Interfaces across every
    /// partial). The emitter consults this map and uses the merged
    /// value when emitting per-type blocks per C3 audit. Empty until
    /// the Pairings phase populates it.
    /// </summary>
    public Dictionary<XhtTypeBase, XhtTypeBase> MergedSymbolView { get; }

    /// <summary>
    /// Return the effective (post-merge) shape for a type. When the
    /// type is the canonical entry of a merged partial-class group,
    /// returns the merged XhtClass; otherwise returns
    /// <paramref name="raw"/> unchanged. Callers should use this
    /// accessor whenever they read a type's Functions / Properties /
    /// Specifiers in contexts where the merged view is the intent.
    /// </summary>
    /// <param name="raw">The type as returned from <see cref="Symbols"/>. Must not be null.</param>
    /// <returns>The merged shape, or <paramref name="raw"/> if no merge applies.</returns>
    public XhtTypeBase GetEffectiveShape(XhtTypeBase raw)
    {
        System.ArgumentNullException.ThrowIfNull(raw);
        return MergedSymbolView.TryGetValue(raw, out XhtTypeBase? merged) ? merged : raw;
    }
}
