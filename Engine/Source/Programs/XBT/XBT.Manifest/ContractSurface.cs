// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Simgenics.XPact.XBT.Manifest;

/// <summary>
/// Enumeration of the canonical Toolchain Contract surface elements per
/// Contract Rev 13.2 Section 10.2 ("ContractVersion auto-derivation"). This
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
    /// round 1 -> "13.1"; Rev 13.2 = audit-fixes round 2+ -> "13.2";
    /// Rev 13.8 = XCore-4b Stage B addendum, freezing the reflection-
    /// type byte layouts via the <see cref="AbiLayoutTags"/> surface).
    /// Bumped on every contract revision so a textually-large but
    /// structurally-small revision can still produce a new
    /// <see cref="ContractVersion.Current"/> string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The full <see cref="ContractVersion.Current"/> string is
    /// <c>$"{SemanticVersionTag}+{StructureHash[0..16]}"</c>; the
    /// semantic tag flows before the <c>+</c>, the hash suffix after.
    /// </para>
    /// <para>
    /// Rev 13.2 narrative (audit fixes round 2 + round 4 verification):
    /// completed the ActionType slot table by adding
    /// <c>Tier2WholeProgramPass</c> (slot 13) and
    /// <c>BuildPluginManifestAction</c> (slot 16) -- both are emitted
    /// by XBT in Phase 1 with named slots so renaming them would
    /// otherwise be a silent contract drift; the audit added them to
    /// the canonical surface so the auto-derived hash now responds to
    /// either name change. Per the append-only contract ordinals, this
    /// mutation of the surface bumps the hash.
    /// </para>
    /// <para>
    /// Rev 13.8 narrative (XCore-4b Phase 4b.7 -- Stage B addendum):
    /// the reflection-type byte layouts freeze per XCore-4b Rev 4
    /// Section 11. The 15 <see cref="AbiLayoutTags"/> entries
    /// (XPACT_FNAME_LAYOUT_TAG, XPACT_FPROPERTY_LAYOUT_TAG v2,
    /// XPACT_FSTRUCT_LAYOUT_TAG v4, XPACT_FCLASS_LAYOUT_TAG v4,
    /// XPACT_FSCRIPTSTRUCT_LAYOUT_TAG v4, XPACT_FENUM_LAYOUT_TAG v4,
    /// XPACT_FINTERFACE_LAYOUT_TAG v4, XPACT_FCUSTOMVERSION_LAYOUT_TAG,
    /// XPACT_REPMETA_LAYOUT_TAG v2, XPACT_FFAKEVTABLE_LAYOUT_TAG v2,
    /// XPACT_FCPPSTRUCTOPSFAKEVTABLE_LAYOUT_TAG v1, plus four
    /// non-versioned byte-total tags for FField / FFieldClass /
    /// FFieldVariant / FRepRecord) join the canonical surface so
    /// every layout change rotates the structure hash. The semantic
    /// tag jumps from "13.2" straight to "13.8" because the interim
    /// 13.3 -- 13.7 were Rev-only documentation revisions that did
    /// not touch <see cref="ContractSurface"/>.
    /// </para>
    /// </remarks>
    public const string SemanticVersionTag = "13.8";

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
    /// <para>
    /// The list includes every action type that Phase 1 actually emits.
    /// The pre-reserved Phase 2 slots (9-12 + 14-15) are <em>not</em>
    /// listed because they are placeholders -- adding a Phase 2 slot
    /// would invalidate every Phase 1 ActionHistory the moment that
    /// addendum lands, which defeats the point of the
    /// append-only-ordinal contract.
    /// </para>
    /// <para>
    /// Audit fix R4-M1: <c>Tier2WholeProgramPass</c> (slot 13) and
    /// <c>BuildPluginManifestAction</c> (slot 16) are added to the
    /// surface. Both are declared in <see cref="ActionGraph.XActionType"/>
    /// with named, non-Reserved_ slots, both can be emitted by XBT in
    /// Phase 1 (per <c>XActionType.cs</c> Slot-16 declaration and the
    /// Tier 2 Pass 2 emit), and both rename-actions on either would
    /// otherwise be a silent contract drift the way the
    /// <c>StructureHash</c> mechanism is designed to prevent. Slot 17
    /// (formerly <c>RunPostBuildAction</c>) is RETIRED, not reused;
    /// it does NOT appear on this surface.
    /// </para>
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
        "Tier2WholeProgramPass",
        "BuildPluginManifestAction",
    };

    /// <summary>
    /// XCore-4b reflection-type ABI layout tag surface per the Stage B
    /// addendum (Toolchain Contract Rev 13.8 / XCore-4b Rev 4 Section 11.6).
    /// Each entry is a (TagMacro, TagContent) pair where:
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>TagMacro</c> is the <c>XPACT_*_LAYOUT_TAG</c> preprocessor
    /// identifier the runtime header defines (e.g.
    /// <c>XPACT_FPROPERTY_LAYOUT_TAG</c>); XHT-emitted <c>.gen.cpp</c>
    /// + <c>.gen.h</c> files reference this macro inside a
    /// <c>static_assert(XPactDetail::CompileTimeStrEq(...))</c> call so
    /// a downstream TU compiled against a different runtime ABI fails
    /// to compile cleanly per XCore-4b Section 9.4.
    /// </para>
    /// <para>
    /// <c>TagContent</c> is the canonical string value the runtime
    /// header MUST define <c>TagMacro</c> to expand to. The contract
    /// pin is content-addressed: any byte change to the runtime
    /// header's definition rotates the structure hash here AND fires
    /// the per-TU <c>static_assert</c> at compile time, providing two
    /// layers of defense against silent ABI drift.
    /// </para>
    /// <para>
    /// The list is in declaration order (which is also slot ordinal
    /// for the surface canonicalization). Adding, removing, or
    /// reordering any entry bumps
    /// <see cref="ContractVersion.StructureHash"/>; editing the
    /// <c>TagContent</c> string of an existing entry also bumps the
    /// hash (covering layout changes that retain the macro name but
    /// change the underlying byte arrangement). The 11 versioned tags
    /// match XCore-4b Rev 4 Section 11.6 exactly; the four
    /// non-versioned tags (FField, FFieldClass, FFieldVariant,
    /// FRepRecord) pin the byte totals of types whose layout is
    /// trivially derivable from a single int / pointer set but where
    /// silent drift would still corrupt the reflection runtime.
    /// </para>
    /// <para>
    /// Per XCore-4b Section 9.4 (per-DLL static_assert pins): every
    /// XHT-emitted <c>.gen.cpp</c> / <c>.gen.h</c> file carries the
    /// static_assert pins so a patch DLL compiled against a different
    /// ABI fails to link with a clean compile-time error before any
    /// runtime damage occurs.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<(string TagMacro, string TagContent)> AbiLayoutTags = new (string, string)[]
    {
        (
            "XPACT_FNAME_LAYOUT_TAG",
            "FName-v1: 4+4 / Index+SerialNumber / 8-byte total / 4-byte aligned"
        ),
        (
            "XPACT_FFIELD_LAYOUT_TAG",
            "FField-v1: 32 bytes; ClassPrivate@0, Owner@8, Next@16, NamePrivate@24"
        ),
        (
            "XPACT_FFIELDCLASS_LAYOUT_TAG",
            "FFieldClass-v1: 48 bytes; Name@0, Id@8, CastFlags@16, SuperClass@24, Construct@32, FakeVTable@40"
        ),
        (
            "XPACT_FFIELDVARIANT_LAYOUT_TAG",
            "FFieldVariant-v1: 8 bytes; Storage@0 (1-bit LSB tag, 0=FField, 1=FStruct, on 8-byte-aligned pointer)"
        ),
        (
            "XPACT_FPROPERTY_LAYOUT_TAG",
            "FProperty-v2: 96 base + 8 DispatchTable = 104 bytes; UE-equivalent rep-meta source; FakeVTable in .rodata; FFieldVariant LSB-tag (LSB=1 means FStruct, inverse of UE)"
        ),
        (
            "XPACT_FFAKEVTABLE_LAYOUT_TAG",
            "FFakeVTable-v2: 8-byte header (Capabilities uint32 + _reservedHeader uint32) + 15 function-pointer slots (8 bytes each) = 128 bytes per FProperty subclass in .rodata; ConvertFromType is slot index 14 (the 15th and last; ESlot enum 0-indexed) per Rev 3 FIX-R2-HIGH-1"
        ),
        (
            "XPACT_FSTRUCT_LAYOUT_TAG",
            "FStruct-v4: 112 bytes; ObjectRefProperties TArray @ offset 56 = 24 bytes; SchemaHash @ 80; SerializeStructFn @ 104"
        ),
        (
            "XPACT_FSCRIPTSTRUCT_LAYOUT_TAG",
            "FScriptStruct-v4: 112 FStruct base + 16 ICppStructOps FakeVTable pattern = 128 bytes; per-subtype FCppStructOpsFakeVTable in .rodata at 136 bytes (8-byte header + 16 handler slots)"
        ),
        (
            "XPACT_FCPPSTRUCTOPSFAKEVTABLE_LAYOUT_TAG",
            "FCppStructOpsFakeVTable-v1: 8-byte header (32-bit Capabilities + _reservedHeader) + 16 handler slots (8 bytes each) = 136 bytes per FScriptStruct subtype in .rodata"
        ),
        (
            "XPACT_FCLASS_LAYOUT_TAG",
            "FClass-v4: 112 FStruct base + 112 FClass-specific = 224 bytes; ClassReps is TArray<FRepRecord> (24 bytes); NetFields is TArray<FField*> (24 bytes); ObjectRefProperties is dense TArray on FStruct (24 bytes); reflects XCore-4a DefaultAllocator carrying 2-byte FMemTag for per-container memory attribution per Phase 4b.5 verification"
        ),
        (
            "XPACT_FREPRECORD_LAYOUT_TAG",
            "FRepRecord-v1: 16 bytes per ClassReps entry; {FProperty* Property; int32 Index} matches UE's Class.h:3984"
        ),
        (
            "XPACT_FENUM_LAYOUT_TAG",
            "FEnum-v4: 72 bytes; Values TArray @ offset 40 = 24 bytes; CppForm @ 64"
        ),
        (
            "XPACT_FINTERFACE_LAYOUT_TAG",
            "FInterface-v4: 64 bytes; InterfaceFunctions TArray @ offset 32 = 24 bytes; InterfaceFlags @ 56"
        ),
        (
            "XPACT_FCUSTOMVERSION_LAYOUT_TAG",
            "FCustomVersion-v1: Key(FGuid 16) + Version(int32) + FriendlyName(FName)"
        ),
        (
            "XPACT_REPMETA_LAYOUT_TAG",
            "RepMeta-v2: 15-condition ELifetimeCondition(1) + RepIndex(2) + RepNotifyFunc-as-FName(8); UE-equivalent pre-Iris set"
        ),
    };

    /// <summary>
    /// Per-type sizeof byte totals from the XCore-4b Rev 4 addendum
    /// Section 11.2 / 11.3 tables. Used by XBT's <c>validate-abi-tags</c>
    /// mode to cross-check the XHT-emitted <c>static_assert(sizeof(...))</c>
    /// pins against the contract-declared values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each entry is (TypeName, ExpectedBytes). The reflection-runtime
    /// header <c>XReflectionRuntime.h</c> MUST emit a
    /// <c>static_assert(sizeof(TypeName) == ExpectedBytes, ...)</c>
    /// pin matching every row; XHT's <c>.gen.h</c> emit replicates the
    /// per-TU pins so every consumer module catches ABI drift at its
    /// own compile time (per XCore-4b Section 9.4).
    /// </para>
    /// <para>
    /// The list contributes to the canonical surface bytes -- adding,
    /// removing, or changing any expected size rotates
    /// <see cref="ContractVersion.StructureHash"/>. This catches the
    /// "developer edits FProperty.h to add a member without bumping
    /// the contract" footgun that the XHT-emit per-TU static_assert
    /// also catches at compile time.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<(string TypeName, int ExpectedBytes)> AbiTypeSizes = new (string, int)[]
    {
        ("FName", 8),
        ("FField", 32),
        ("FFieldClass", 48),
        ("FFieldVariant", 8),
        ("FProperty", 104),
        ("FFakeVTable", 128),
        ("FStruct", 112),
        ("FScriptStruct", 128),
        ("FCppStructOpsFakeVTable", 136),
        ("FClass", 224),
        ("FRepRecord", 16),
        ("FEnum", 72),
        ("FInterface", 64),
        ("FCustomVersion", 32),
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
