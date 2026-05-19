// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Simgenics.XPact.XBT.Manifest;

/// <summary>
/// Enumeration of the canonical Toolchain Contract surface elements per
/// Contract Rev 13 Section 10.2 ("ContractVersion auto-derivation"). This
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
///   <item><see cref="ManglingRuleExample"/> -- Contract Section 1.4 Itanium-style length-prefixed mangling example.</item>
///   <item><see cref="FileIdScheme"/> -- Contract Section 1.5 per-file ID scheme.</item>
///   <item><see cref="ExitCodes"/> -- Contract Section 13 exit-code surface.</item>
///   <item><see cref="ActionTypes"/> -- the action-type enum the action graph consumes.</item>
/// </list>
/// </remarks>
public static class ContractSurface
{
    /// <summary>
    /// Hand-bumped semantic version tag. Tracks the current Toolchain
    /// Contract revision (Rev 13 -> "13.0"). Bumped on every contract
    /// revision so a textually-large but structurally-small revision can
    /// still produce a new <see cref="ContractVersion.Current"/> string.
    /// </summary>
    /// <remarks>
    /// The full <see cref="ContractVersion.Current"/> string is
    /// <c>$"{SemanticVersionTag}+{StructureHash[0..16]}"</c>; the
    /// semantic tag flows before the <c>+</c>, the hash suffix after.
    /// </remarks>
    public const string SemanticVersionTag = "13.0";

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
    /// File-ID scheme per Contract Section 1.5. Format string defines
    /// how XHT and XIL2CPP encode per-file unique symbol prefixes:
    /// <c>XID_{PluginName}__{LogicalPath}_h_{LineNumber}_{Suffix}</c>.
    /// </summary>
    public const string FileIdScheme = "XID_{PluginName}__{LogicalPath}_h_{LineNumber}_{Suffix}";

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
    /// Codes 90-99 are reserved (not enumerated here) for Phase 2 Live
    /// Coding domain failures. They join the surface when XLiveCoding
    /// ships.
    /// </remarks>
    public static readonly IReadOnlyList<(int Code, string Mnemonic)> ExitCodes = new[]
    {
        (0,   "Success"),
        (1,   "GenericFailure"),
        (10,  "CliArgumentError"),
        (20,  "ConfigurationError"),
        (21,  "TierViolation"),
        (22,  "CycleDetected"),
        (23,  "EngineOrToolchainVersionMismatch"),
        (24,  "PluginVersionMismatch"),
        (30,  "RulesCompileFailed"),
        (40,  "CopyrightHeaderMissing"),
        (41,  "BannedApiOnSimPathTU"),
        (50,  "ManifestMalformed"),
        (60,  "PluginNotFound"),
        (61,  "DependencyResolutionFailed"),
        (62,  "DynamicLoadProtocolMismatch"),
        (63,  "InterfaceModuleSymbolLeak"),
        (70,  "CompileFailed"),
        (71,  "LinkFailed"),
        (80,  "ActionGraphCycle"),
        (81,  "BuildHookOutputMismatch"),
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
