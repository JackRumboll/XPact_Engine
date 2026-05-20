// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Simgenics.XPact.XBT.Manifest;

/// <summary>
/// Enumeration of the canonical Toolchain Contract surface elements per
/// Contract Rev 13.1 Section 10.2 ("ContractVersion auto-derivation"). This
/// type is the <strong>C# source of truth</strong> for what the contract
/// locks; if the C# disagrees with <c>/Documents/XToolchainContract.html</c>
/// the C# wins (the HTML is documentation; this code is the implementation
/// that XHT, XIL2CPP, and the action-graph cache key actually consume).
/// </summary>
/// <remarks>
/// <para>
/// The surface is the input to <see cref="ContractVersion.StructureHash"/>:
/// any change to any item below changes the hash, which changes
/// <see cref="ContractVersion.Current"/>, which invalidates ActionHistory
/// and forces every downstream cache layer to recompute. The mechanism is
/// the "no silent contract bump" promise that the Rev 11 deep-audit added
/// to preempt the Rev 10 "forgot to bump ContractVersion" footgun.
/// </para>
/// <para>
/// The eight surface buckets, with their normative Contract sections:
/// </para>
/// <list type="bullet">
///   <item><see cref="SemanticVersionTag"/> -- manually bumped per Contract revision.</item>
///   <item><see cref="Enums"/> -- enum names and ordinals from <see cref="Enums"/>.cs, reflected once at static-init.</item>
///   <item><see cref="MarkerMacros"/> -- Contract Section 1.3 marker macros (XCLASS / XSTRUCT / ...).</item>
///   <item><see cref="BodyMacroSuffixes"/> -- Contract Section 1.3 body-macro suffix table.</item>
///   <item><see cref="ManglingRuleExample"/> -- Contract Section 1.3 Itanium-style length-prefixed mangling example.</item>
///   <item><see cref="FileIdScheme"/> -- Contract Section 1.4 per-file ID scheme (length-prefixed grammar).</item>
///   <item><see cref="ExitCodes"/> -- Contract Section 13 exit-code surface.</item>
///   <item><see cref="ActionTypes"/> -- the action-type enum the action graph consumes.</item>
/// </list>
/// </remarks>
public static class ContractSurface
{
    /// <summary>
    /// Hand-bumped semantic version tag. Tracks the current Toolchain
    /// Contract revision (Rev 13 -> "13.0"; Rev 13.1 = audit-fixes
    /// round 1 -> "13.1"). Bumped on every contract revision so a
    /// textually-large but structurally-small revision can still produce
    /// a new <see cref="ContractVersion.Current"/> string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The full <see cref="ContractVersion.Current"/> string is
    /// <c>$"{SemanticVersionTag}+{StructureHash[0..16]}"</c>; the
    /// semantic tag flows before the <c>+</c>, the hash suffix after.
    /// </para>
    /// <para>
    /// Rev 13.1 (audit fixes round 1): corrected Section 13 exit-code
    /// mnemonics (24=PluginNotFound; 60-63=XHT/XIL2CPP subprocess +
    /// internal failures), replaced the underscore-collapsing
    /// <see cref="FileIdScheme"/> with the Itanium-ABI-style
    /// length-prefixed grammar from Contract Section 1.4, and aligned
    /// <c>OptimizeCodeMode</c> enum ordinals with XBT.html Section 4.1.
    /// All three changes mutate the structure hash so every downstream
    /// cache layer (ActionHistory, XIL2CPP symbol mangling, XHT
    /// reflection metadata, manifest verification) treats the surface
    /// as incompatible and recomputes.
    /// </para>
    /// </remarks>
    public const string SemanticVersionTag = "13.1";

    /// <summary>
    /// Itanium-ABI-style length-prefixed mangling rule example per
    /// Contract Section 1.4. Locked example: <c>"XN1A1BE"</c> mangles
    /// the qualified name <c>A::B</c> (the <c>N...E</c> brackets are the
    /// Itanium nested-name convention; lengths are decimal). Any change
    /// to the mangling grammar must bump this example so the
    /// <see cref="ContractVersion.StructureHash"/> picks the change up.
    /// </summary>
    public const string ManglingRuleExample = "XN1A1BE";

    /// <summary>
    /// Per-file ID grammar per Contract Section 1.4 (REVISED Rev 11 --
    /// collision-free Itanium-ABI-style length-prefixed mangling). The
    /// constant is a grammar-description string, not a sprintf-style
    /// template: each <c>N&lt;Len&gt;&lt;Token&gt;</c> production carries a
    /// decimal length prefix followed by the token bytes, so escaping
    /// is bijective and collisions like <c>Foo_Bar/X.h</c> vs.
    /// <c>Foo/Bar_X.h</c> are impossible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Components, in order:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>PluginName</c> -- the plugin's <c>.xplugin</c>
    ///   <c>Name</c> field (or the literal <c>"Engine"</c> for the
    ///   engine itself).</item>
    ///   <item><c>LogicalPath</c> segments -- the header's per-module
    ///   logical path (declared via <c>PublicIncludePaths</c> /
    ///   <c>PrivateIncludePaths</c>), split on forward slash. The
    ///   filesystem path is intentionally NOT part of the symbol so
    ///   moving the file on disk does not invalidate consumers.</item>
    ///   <item><c>LineNumber</c> -- the source line of the
    ///   <c>XGENERATED_BODY()</c> macro.</item>
    ///   <item><c>Suffix</c> -- one of the table in
    ///   <see cref="BodyMacroSuffixes"/>.</item>
    /// </list>
    /// <para>
    /// Worked example for <c>AXValve</c> at line 14 of plugin
    /// <c>Engine</c> with logical path
    /// <c>XGameFramework/Public/Valves/XValve</c>:
    /// <c>_XID_N6EngineN4N14XGameFrameworkN6PublicN6ValvesN6XValve_L14_GENERATED_BODY</c>.
    /// </para>
    /// </remarks>
    public const string FileIdScheme =
        "_XID_N<PluginNameLen><PluginName>N<LogicalPathSegCount>{N<SegLen><SegName>}_L<LineNumber>_<Suffix>";

    /// <summary>
    /// Marker-macro vocabulary per Contract Section 1.3. Locked
    /// alphabetic-then-anchor order: the eight specifier markers
    /// followed by <c>XGENERATED_BODY</c>. Order is preserved in the
    /// canonicalization so reordering changes the hash.
    /// </summary>
    public static readonly IReadOnlyList<string> MarkerMacros = new[]
    {
        "XCLASS",
        "XSTRUCT",
        "XENUM",
        "XFUNCTION",
        "XPROPERTY",
        "XINTERFACE",
        "XDELEGATE",
        "XPARAM",
        "XMETA",
        "XGENERATED_BODY",
    };

    /// <summary>
    /// Body-macro suffix table per Contract Section 1.3 ("composite
    /// expansion order"). The order matches the Contract's documented
    /// expansion order: <c>PROLOG</c> first (file-scope forward decls),
    /// then the in-class composite, then the in-class slices in their
    /// composition order (<c>INCLASS</c>, <c>RPC_WRAPPERS</c>,
    /// <c>ACCESSORS</c>, <c>FIELDNOTIFY</c>, <c>VINTERFACES</c>,
    /// <c>STANDARD_CONSTRUCTORS</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> BodyMacroSuffixes = new[]
    {
        "PROLOG",
        "GENERATED_BODY",
        "INCLASS",
        "RPC_WRAPPERS",
        "ACCESSORS",
        "FIELDNOTIFY",
        "VINTERFACES",
        "STANDARD_CONSTRUCTORS",
    };

    /// <summary>
    /// Exit-code surface per Contract Section 13. Adding a code is a
    /// contract revision; removing or repurposing a code is forbidden.
    /// Canonicalization sorts by integer code, so reordering this array
    /// does <em>not</em> change the hash, but adding/removing/renaming
    /// any entry does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mnemonics mirror the contract's Section 13.1 table verbatim.
    /// Audit fix (Rev 13.1): the Rev 13.0 mnemonics for codes 20 and
    /// 24 had drifted from the contract (e.g. <c>ConfigurationError</c>
    /// instead of <c>DiscoveryFailure</c> at code 20;
    /// <c>PluginVersionMismatch</c> instead of <c>PluginNotFound</c>
    /// at code 24), and codes 60-63 had incorrectly been assigned to
    /// the plugin / dynamic-load domain instead of the XHT / XIL2CPP
    /// subprocess + internal failure domain. The table now matches
    /// Contract Section 13.1 exactly.
    /// </para>
    /// <para>
    /// Codes 90-99 are reserved (not enumerated here) for Phase 2 Live
    /// Coding domain failures. They join the surface when XLiveCoding
    /// ships.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<(int Code, string Mnemonic)> ExitCodes = new[]
    {
        (0,   "Success"),
        (1,   "GenericFailure"),
        (10,  "CliArgumentError"),
        (20,  "DiscoveryFailure"),
        (21,  "TierViolation"),
        (22,  "CycleDetected"),
        (23,  "EngineOrToolchainVersionMismatch"),
        (24,  "PluginNotFound"),
        (30,  "RulesCompileFailed"),
        (40,  "CopyrightHeaderMissing"),
        (41,  "BannedApiOnSimPathTU"),
        (50,  "ManifestMalformed"),
        (60,  "XhtSubprocessFailure"),
        (61,  "Xil2CppSubprocessFailure"),
        (62,  "XhtInternalFailure"),
        (63,  "Xil2CppInternalFailure"),
        (70,  "CompileFailed"),
        (71,  "LinkFailed"),
        (80,  "ActionGraphCycle"),
        (81,  "BuildHookOutputMismatch"),
        // Codes 90-99 reserved for Phase 2 XLiveCoding domain (not
        // enumerated; they join the surface when XLiveCoding ships).
        (130, "Cancelled"),
    };

    /// <summary>
    /// Action-type slot list per the XActionType enum (Contract /
    /// XBT.html Section 8.1 cache-key input). Declared-array order is
    /// the slot ordinal; reordering changes the hash, which is exactly
    /// what we want (changing slot ordinals breaks every cached
    /// ActionHistory entry).
    /// </summary>
    /// <remarks>
    /// Slots 9-15 are reserved for forward-compatibility (XPactBuild
    /// Accelerator + XLiveCoding action types) and are deliberately
    /// excluded from the contract surface so a Phase 2 addendum that
    /// appends actions does not retroactively invalidate Phase 1
    /// ActionHistory.
    /// </remarks>
    public static readonly IReadOnlyList<string> ActionTypes = new[]
    {
        "ValidateCopyrightAction",
        "WriteManifestAction",
        "ParseHeadersAction",
        "EmitReflectionAction",
        "XIL2CPPAction",
        "PCHGenerationAction",
        "CompileCppAction",
        "StaticAnalysisAction",
        "LinkModuleAction",
    };

    /// <summary>
    /// Reflected enum surface. Lazily populated on first access from
    /// the enums declared in <c>XBT.Manifest/Enums.cs</c>: enum names
    /// sorted alphabetically, members within each enum sorted by
    /// ordinal. Reflecting (rather than hand-coding) the enum table
    /// means a new enum added to <c>Enums.cs</c> automatically lands
    /// in the contract surface.
    /// </summary>
    public static IReadOnlyList<(string EnumName, IReadOnlyList<(string Name, int Ordinal)> Members)> Enums => s_enums.Value;

    private static readonly Lazy<IReadOnlyList<(string, IReadOnlyList<(string, int)>)>> s_enums = new(ReflectEnums);

    private static IReadOnlyList<(string, IReadOnlyList<(string, int)>)> ReflectEnums()
    {
        // The contract surface enums are exactly the public enums declared
        // in this assembly's manifest namespace (Enums.cs is the single
        // source of truth). Reflecting over the assembly avoids drift
        // between a hand-coded table and the actual C# declarations.
        Type[] enumTypes = typeof(ContractSurface).Assembly
            .GetTypes()
            .Where(t => t.IsEnum && t.IsPublic && t.Namespace == typeof(ContractSurface).Namespace)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();

        List<(string, IReadOnlyList<(string, int)>)> result = new(enumTypes.Length);
        foreach (Type enumType in enumTypes)
        {
            string[] names = Enum.GetNames(enumType);
            (string Name, int Ordinal)[] members = new (string, int)[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                members[i] = (names[i], Convert.ToInt32(Enum.Parse(enumType, names[i])));
            }

            // Sort members by ordinal (stable; ties broken by name) so
            // canonicalization is deterministic regardless of source-order.
            Array.Sort(
                members,
                static (a, b) => a.Ordinal != b.Ordinal
                    ? a.Ordinal.CompareTo(b.Ordinal)
                    : string.CompareOrdinal(a.Name, b.Name));

            result.Add((enumType.Name, members));
        }

        return result;
    }
}
