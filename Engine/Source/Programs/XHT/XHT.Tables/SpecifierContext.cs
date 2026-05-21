// Copyright Simgenics. All Rights Reserved.

using System;

namespace Simgenics.XPact.XHT.Tables;

/// <summary>
/// Identifies the syntactic site at which a specifier may legally appear,
/// per <c>/Documents/XHT.html</c> Rev 7 Section 7 (markers + specifiers) and
/// Section 18.1 (table inheritance + the PropertyMember vs PropertyArgument
/// split).
/// </summary>
/// <remarks>
/// <para>
/// The enum is a <c>[Flags]</c> bitfield because a single specifier
/// definition may legally apply at more than one site (e.g. <c>Category</c>
/// is legal on every <c>XPROPERTY(...)</c> regardless of whether it is a
/// member or a parameter, and on every <c>XFUNCTION(...)</c>). Specifier
/// resolution intersects the registered <c>ApplicableTo</c> mask against the
/// parser's currently-active context; a non-zero intersection signals a
/// legal use.
/// </para>
/// <para>
/// <b>PropertyMember vs PropertyArgument split (XHT.html Rev 7 Section 18.1).
/// </b> UHT distinguishes specifiers valid on function parameters
/// (call-site argument descriptor -- <c>ConstParm</c>, <c>OutParm</c>,
/// <c>ReferenceParm</c>) from specifiers valid on class fields (persisted
/// member descriptor -- <c>EditAnywhere</c>, <c>Replicated</c>,
/// <c>SaveGame</c>). XHT mirrors the split. A specifier whose
/// <c>ApplicableTo</c> includes <see cref="Property"/> is legal in
/// <em>both</em> contexts (the base property table); a specifier whose
/// <c>ApplicableTo</c> includes only <see cref="PropertyMember"/> or only
/// <see cref="PropertyArgument"/> is legal in just one. The validator pass
/// (Section 6.2) catches context mismatches and emits <c>XHT111</c>.
/// </para>
/// </remarks>
[Flags]
public enum SpecifierContext
{
    /// <summary>The empty mask; no contexts. Sentinel for "uninitialised".</summary>
    None = 0,

    /// <summary>Legal inside <c>XCLASS(...)</c> on a class declaration.</summary>
    Class = 1 << 0,

    /// <summary>Legal inside <c>XSTRUCT(...)</c> on a struct declaration.</summary>
    Struct = 1 << 1,

    /// <summary>Legal inside <c>XENUM(...)</c> on an enum declaration.</summary>
    Enum = 1 << 2,

    /// <summary>Legal inside <c>XINTERFACE(...)</c> on an interface declaration.</summary>
    Interface = 1 << 3,

    /// <summary>Legal inside <c>XFUNCTION(...)</c> on a method declaration.</summary>
    Function = 1 << 4,

    /// <summary>
    /// Legal inside <c>XPROPERTY(...)</c> on either a class member or a
    /// function parameter (the base property table per Section 18.1).
    /// </summary>
    Property = 1 << 5,

    /// <summary>
    /// Legal on a class field only; not on a function parameter. Specifiers
    /// like <c>EditAnywhere</c>, <c>BlueprintReadOnly</c>, <c>Replicated</c>,
    /// <c>SaveGame</c>, <c>Config</c>.
    /// </summary>
    PropertyMember = 1 << 6,

    /// <summary>
    /// Legal on a function parameter only; not on a class field. Specifiers
    /// like <c>ConstParm</c>, <c>OutParm</c>, <c>ReferenceParm</c>.
    /// </summary>
    PropertyArgument = 1 << 7,

    /// <summary>Legal inside <c>XDELEGATE(...)</c> on a delegate signature.</summary>
    Delegate = 1 << 8,

    /// <summary>Legal inside <c>XMETA(...)</c> on an enum value.</summary>
    EnumValue = 1 << 9,

    /// <summary>Legal inside <c>XPARAM(...)</c> on a function parameter.</summary>
    Param = 1 << 10,

    /// <summary>
    /// Union of every concrete context. A specifier with
    /// <see cref="All"/> as its <c>ApplicableTo</c> mask is legal in every
    /// supported syntactic site (rare; reserved for parser-driven cases such
    /// as the universal <c>Meta</c> handler).
    /// </summary>
    All = Class | Struct | Enum | Interface | Function | Property | PropertyMember | PropertyArgument | Delegate | EnumValue | Param,
}
