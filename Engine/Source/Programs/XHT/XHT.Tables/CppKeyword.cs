// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XHT.Tables;

/// <summary>
/// Classification of the C++ keywords the XHT tokenizer needs to recognise
/// per <c>/Documents/XHT.html</c> Rev 7 Section 3.4 (the marker-driven
/// dispatcher). The kind drives parser branching: the C++ parser consults
/// the <c>Kind</c> of a recognised keyword to decide whether the token
/// starts a declaration (<see cref="Type"/>), modifies an existing
/// declaration (<see cref="Qualifier"/> / <see cref="StorageClass"/>),
/// introduces flow control (<see cref="Control"/>), or marks an XHT
/// reflection site (<see cref="XhtMarker"/>).
/// </summary>
/// <remarks>
/// <para>
/// XHT is <em>not</em> a full C++ compiler -- the tokenizer recognises
/// the subset of C++ relevant to marker-recognition and reflected-member
/// extraction (Section 3.1). The keyword table mirrors UHT's
/// <c>EpicGames.UHT/Tables/UhtKeywordTable.cs</c> but is narrower: XHT
/// keeps only the keywords the marker dispatcher actually inspects, plus
/// the small vocabulary of C++-side qualifiers that appear in reflected
/// type signatures.
/// </para>
/// </remarks>
public enum CppKeywordKind
{
    /// <summary>Declares a new type: <c>class</c>, <c>struct</c>, <c>enum</c>, <c>union</c>.</summary>
    Type,

    /// <summary>Storage class: <c>static</c>, <c>extern</c>, <c>mutable</c>, <c>thread_local</c>.</summary>
    StorageClass,

    /// <summary>Cv-qualifier: <c>const</c>, <c>volatile</c>, <c>restrict</c>.</summary>
    Qualifier,

    /// <summary>Access specifier: <c>public</c>, <c>private</c>, <c>protected</c>.</summary>
    AccessModifier,

    /// <summary>Control-flow keyword: <c>if</c>, <c>else</c>, <c>for</c>, <c>while</c>, <c>return</c>, <c>switch</c>, <c>case</c>, <c>do</c>, <c>break</c>, <c>continue</c>, <c>default</c>.</summary>
    Control,

    /// <summary>Boolean literal: <c>true</c>, <c>false</c>.</summary>
    Boolean,

    /// <summary>C++ named-cast operator: <c>static_cast</c>, <c>reinterpret_cast</c>, <c>const_cast</c>, <c>dynamic_cast</c>.</summary>
    Cast,

    /// <summary>Operator-as-keyword: <c>sizeof</c>, <c>alignof</c>, <c>typeid</c>, <c>noexcept</c>, <c>decltype</c>.</summary>
    OperatorWord,

    /// <summary>
    /// Other recognised C++ keywords: <c>nullptr</c>, <c>this</c>,
    /// <c>namespace</c>, <c>using</c>, <c>template</c>, <c>typedef</c>,
    /// <c>typename</c>, <c>friend</c>, <c>virtual</c>, <c>override</c>,
    /// <c>final</c>, <c>explicit</c>, <c>inline</c>, <c>constexpr</c>,
    /// <c>constinit</c>, <c>consteval</c>, <c>operator</c>, <c>new</c>,
    /// <c>delete</c>, <c>auto</c>, <c>throw</c>, <c>try</c>, <c>catch</c>.
    /// </summary>
    Other,

    /// <summary>
    /// One of the XHT reflection markers per Contract Section 1.1:
    /// <c>XCLASS</c>, <c>XSTRUCT</c>, <c>XENUM</c>, <c>XINTERFACE</c>,
    /// <c>XFUNCTION</c>, <c>XPROPERTY</c>, <c>XDELEGATE</c>, <c>XPARAM</c>,
    /// <c>XMETA</c>, <c>XGENERATED_BODY</c>. The marker-driven dispatcher
    /// (Section 3.4) routes these to the specifier parser; every other
    /// kind is a regular C++ keyword.
    /// </summary>
    XhtMarker,
}

/// <summary>
/// A single C++ keyword recognition entry -- the source spelling plus
/// its classification. Lookups are exact (case-sensitive) per the C++
/// language rule that keywords are case-sensitive. See
/// <see cref="CppKeywordTable"/>.
/// </summary>
/// <param name="Spelling">The keyword as it appears in source.</param>
/// <param name="Kind">The classification of the keyword.</param>
public sealed record CppKeyword(string Spelling, CppKeywordKind Kind);
