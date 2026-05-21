// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XHT.Tables;

/// <summary>
/// One entry in the specifier registry per <c>/Documents/XHT.html</c> Rev 5
/// Section 7 + Section 18.1. Mirrors UHT's <c>UhtSpecifier</c> at
/// <c>EpicGames.UHT/Tables/UhtSpecifierTable.cs</c> but with the XHT
/// vocabulary reductions documented on <see cref="SpecifierValueKind"/> and
/// the <c>PropertyMember</c> / <c>PropertyArgument</c> split documented on
/// <see cref="SpecifierContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// Definitions are bulk-registered into a fresh <see cref="SpecifierRegistry"/>
/// from <see cref="BuiltInSpecifiers"/> at process start. Plugins extend the
/// registry through the <see cref="XhtSpecifierAttribute"/> attribute and
/// reflection-based plugin loading per Section 18.2; the plugin's loader
/// instantiates a <c>SpecifierDefinition</c> from the attribute data and
/// calls <see cref="ISpecifierRegistry.Register"/>.
/// </para>
/// <para>
/// The record is immutable. Equality is value-based per C#-record semantics,
/// which lets the registry detect <em>identical</em> re-registrations
/// (benign -- warned via <see cref="Simgenics.XPact.XHT.Core.Logger"/>)
/// distinctly from <em>conflicting</em> re-registrations (fatal --
/// throws per <c>XHT140</c> Section 18.1).
/// </para>
/// </remarks>
/// <param name="Name">
/// The specifier name as authored in source (e.g. <c>"BlueprintReadWrite"</c>).
/// Case is preserved for diagnostics but lookups are case-insensitive per
/// Section 7.2; the registry keys by <c>name.ToLowerInvariant()</c>.
/// </param>
/// <param name="ApplicableTo">
/// Bitwise OR of every <see cref="SpecifierContext"/> in which this
/// specifier is legal. The parser intersects this mask against the
/// currently-active syntactic context; a zero intersection emits
/// <c>XHT110</c>.
/// </param>
/// <param name="ValueKind">
/// The expected value shape per Section 7.2. Drives the parser's token-
/// consumption strategy after it sees the specifier name.
/// </param>
/// <param name="AllowMultiple">
/// True when the same specifier may appear more than once on the same
/// declaration site. Rare; <c>Category</c> and the meta-bag specifiers are
/// typical examples. Most specifiers are <c>false</c>; a second appearance
/// emits <c>XHT112</c>.
/// </param>
/// <param name="Documentation">
/// Optional one-line description used by <c>dump-ast</c> / IDE help. Null
/// when the specifier is self-describing (most flag specifiers).
/// </param>
/// <param name="AllowOverride">
/// Plugin-extensibility flag per M5 audit + Section 18.1. When TRUE on
/// BOTH the existing and incoming definitions, the incoming
/// registration wins (the second registration takes the slot). When
/// FALSE on either side, a registration conflict throws
/// <c>XHT140</c>. Built-in specifiers all set this <c>false</c>;
/// plugin specifiers may set it <c>true</c> to take precedence over a
/// previously-loaded plugin's definition.
/// </param>
public sealed record SpecifierDefinition(
    string Name,
    SpecifierContext ApplicableTo,
    SpecifierValueKind ValueKind,
    bool AllowMultiple,
    string? Documentation,
    bool AllowOverride = false);
