// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XHT.Resolver;

/// <summary>
/// XHT diagnostic codes the resolver emits per
/// <c>/Documents/XHT.html</c> Rev 5 Section 6.2 (validator catalog) +
/// Section 12.3 (band allocations). All codes are decimal in the form
/// <c>XHT&lt;NNN&gt;</c>; bands are 20-slot ranges per Section 12.3
/// (Rev 4 X-Round2-M2 decimal-only convention).
/// </summary>
/// <remarks>
/// <para>
/// The resolver shares code-space with the parser: codes XHT100-XHT119
/// are validator-band codes where the parser detects syntactic issues
/// at marker-parse time and the resolver detects semantic issues at
/// post-resolution. For example, <c>XHT113</c> covers both the parser's
/// "ReplicatedUsing has no value" parse error and the resolver's
/// "ReplicatedUsing callback signature mismatch" coherence check; both
/// are surface-level "RepNotify is wrong" findings to the user.
/// </para>
/// </remarks>
public static class DiagnosticCodes
{
    /// <summary>XHT100 -- Interface declared without a paired class (orphan native interface).</summary>
    public const string InterfaceWithoutPairedClass = "XHT100";

    /// <summary>XHT101 -- Function declared outside a class / struct / interface (top-level XFUNCTION forbidden).</summary>
    public const string TopLevelFunctionForbidden = "XHT101";

    /// <summary>XHT102 -- Property declared outside a class / struct / interface (orphan reflected property).</summary>
    public const string OrphanProperty = "XHT102";

    /// <summary>XHT103 -- Super / base / interface / outer-type identifier could not be resolved via the symbol table.</summary>
    public const string SymbolNotFound = "XHT103";

    /// <summary>XHT104 -- Resolved super / base / outer entity is the wrong kind (e.g., a class extending a struct).</summary>
    public const string SuperWrongKind = "XHT104";

    /// <summary>XHT105 -- <c>TopologicalStructVisit</c> detected a cycle through the inheritance chain.</summary>
    public const string RecursiveStructCycle = "XHT105";

    /// <summary>XHT111 -- Mutually-exclusive function-side specifier combination (e.g., Server+Client+NetMulticast).</summary>
    public const string FunctionSpecifierConflict = "XHT111";

    /// <summary>XHT112 -- Mutually-exclusive property-side specifier combination (e.g., EditAnywhere+Transient).</summary>
    public const string PropertySpecifierConflict = "XHT112";

    /// <summary>XHT113 -- <c>ReplicatedUsing</c> callback signature mismatch or unresolved callback name.</summary>
    public const string RepNotifyInvalidSignature = "XHT113";

    /// <summary>XHT114 -- Class-level <c>NoExport + Config=</c> conflict per Section 7.4.</summary>
    public const string ConfigConflictsWithNoExport = "XHT114";

    /// <summary>XHT115 -- Class-level <c>Intrinsic + XGENERATED_BODY()</c> conflict per Section 7.4.</summary>
    public const string IntrinsicWithGeneratedBody = "XHT115";

    /// <summary>XHT116 -- Class-level <c>MinimalAPI + RequiredAPI</c> conflict per Section 7.4.</summary>
    public const string MinimalApiWithRequiredApi = "XHT116";

    /// <summary>XHT117 -- Class-level <c>NoExport + Blueprintable</c> conflict per Section 7.4.</summary>
    public const string NoExportWithBlueprintable = "XHT117";

    /// <summary>XHT119 -- Class <c>Within=</c> incompatible with super's <c>Within</c>; mirrors UHT <c>SetAndValidateWithinClass</c>.</summary>
    public const string ClassWithinIncompatibleWithSuper = "XHT119";

    /// <summary>XHT120 -- Cross-language pairing kind mismatch (e.g., C++ struct paired with C# class on the same engine name).</summary>
    public const string CrossLanguagePairingMismatch = "XHT120";

    /// <summary>XHT121 -- Type reference resolves only through a dynamic-only / interface-only module dependency; not link-visible.</summary>
    public const string DynamicOnlyModuleReference = "XHT121";

    /// <summary>XHT143 -- Partial-class duplicate merged into the canonical entry (informational; no exit).</summary>
    public const string PartialClassMerged = "XHT143";
}
