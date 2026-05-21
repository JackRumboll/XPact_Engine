// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;

namespace Simgenics.XPact.XHT.AST;

/// <summary>
/// One reflected delegate signature (<c>XDELEGATE</c> /
/// <c>XMULTICASTDELEGATE</c> / <c>[XDelegate]</c>). Per
/// <c>/Documents/XHT.html</c> Rev 8 Section 4.6.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsMulticast"/> distinguishes the marker family per
/// Contract Section 1.1 -- multicast delegates produce a thunk +
/// add/remove/broadcast surface, single delegates only the thunk.
/// </para>
/// </remarks>
/// <param name="Name">Delegate identifier as authored.</param>
/// <param name="FullyQualifiedName">Identifier prefixed with the namespace path.</param>
/// <param name="OuterName">Containing type name, or null for top-level delegates.</param>
/// <param name="ModuleName">Manifest module this delegate belongs to.</param>
/// <param name="Language">Source language (Cpp / CSharp).</param>
/// <param name="Span">Source location of the delegate declaration.</param>
/// <param name="Specifiers">Raw parsed delegate-side specifiers.</param>
/// <param name="ReturnType">Return type as authored.</param>
/// <param name="Parameters">Delegate parameter list in declaration order.</param>
/// <param name="IsMulticast">True for <c>XMULTICASTDELEGATE</c>; false for single <c>XDELEGATE</c>.</param>
public sealed record XhtDelegate(
    string Name,
    string FullyQualifiedName,
    string? OuterName,
    string ModuleName,
    Language Language,
    SourceSpan Span,
    IReadOnlyList<Specifier> Specifiers,
    string ReturnType,
    IReadOnlyList<XhtParam> Parameters,
    bool IsMulticast)
    : XhtTypeBase(Name, FullyQualifiedName, OuterName, ModuleName, Language, Span, Specifiers);
