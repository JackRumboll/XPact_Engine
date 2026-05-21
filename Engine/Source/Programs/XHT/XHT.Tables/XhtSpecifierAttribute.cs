// Copyright Simgenics. All Rights Reserved.

using System;

namespace Simgenics.XPact.XHT.Tables;

/// <summary>
/// Plugin-side declaration of an XHT specifier per
/// <c>/Documents/XHT.html</c> Rev 5 Section 18 (plugin model). Authors of
/// Phase-2 plugins decorate a marker handler class with this attribute, and
/// the plugin loader (Section 18.2) scans the assembly via reflection and
/// registers the specifier into a <see cref="SpecifierRegistry"/> at plugin-
/// load time.
/// </summary>
/// <remarks>
/// <para>
/// The attribute does not <em>contain</em> the handler code -- it
/// declares the metadata the registry needs (name, applicable contexts,
/// value kind). The handler implementation lives on the decorated class
/// itself; the plugin loader resolves the handler delegate via the standard
/// plugin protocol (Phase 2 work; Phase 1 surfaces only the registration
/// path). Mirrors UHT's <c>UhtSpecifierAttribute</c> at
/// <c>EpicGames.UHT/Tables/UhtSpecifierTable.cs</c>.
/// </para>
/// <para>
/// <b>Attribute placement.</b> <see cref="AttributeUsageAttribute"/> pins
/// this attribute to classes only (<see cref="AttributeTargets.Class"/>),
/// with <c>AllowMultiple = false</c>. A handler class declares exactly one
/// specifier; the symmetric inverse (one specifier handled by multiple
/// classes) is forbidden -- the conflict-detection rule in
/// <see cref="SpecifierRegistry.Register(SpecifierDefinition)"/> catches it.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class XhtSpecifierAttribute : Attribute
{
    /// <summary>
    /// The specifier name as authored in source (e.g. <c>"BlueprintReadWrite"</c>).
    /// Case is preserved for diagnostics but lookups are case-insensitive
    /// per Section 7.2.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Bitwise OR of every <see cref="SpecifierContext"/> in which this
    /// specifier is legal. The parser intersects this mask against the
    /// active context; a zero intersection emits <c>XHT110</c>.
    /// </summary>
    public SpecifierContext ApplicableTo { get; }

    /// <summary>
    /// Expected value shape per Section 7.2.
    /// </summary>
    public SpecifierValueKind ValueKind { get; }

    /// <summary>
    /// True when the specifier may appear more than once on the same
    /// declaration site. Defaults to false; <c>Category</c> and a few
    /// meta-bag specifiers override.
    /// </summary>
    public bool AllowMultiple { get; init; } = false;

    /// <summary>
    /// Optional one-line description used by <c>dump-ast</c> and IDE help.
    /// Defaults to null when the specifier is self-describing.
    /// </summary>
    public string? Documentation { get; init; }

    /// <summary>
    /// Declare an XHT specifier.
    /// </summary>
    /// <param name="name">
    /// Specifier name as authored. The plugin loader passes this verbatim
    /// to <see cref="SpecifierDefinition"/>.
    /// </param>
    /// <param name="applicableTo">
    /// Bitwise OR of the legal syntactic contexts.
    /// </param>
    /// <param name="valueKind">
    /// The value shape the parser should expect after the specifier name.
    /// </param>
    public XhtSpecifierAttribute(string name, SpecifierContext applicableTo, SpecifierValueKind valueKind)
    {
        Name = name;
        ApplicableTo = applicableTo;
        ValueKind = valueKind;
    }
}
