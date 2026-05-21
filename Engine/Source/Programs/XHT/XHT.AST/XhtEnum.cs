// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;

namespace Simgenics.XPact.XHT.AST;

/// <summary>
/// One reflected enum (<c>XENUM</c> / <c>[XEnum]</c>). Per
/// <c>/Documents/XHT.html</c> Rev 8 Section 4.1.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UnderlyingType"/> captures an explicit
/// <c>Underlying=&lt;type&gt;</c> specifier value; when the enum was
/// authored without one, the parser leaves it null and the emitter
/// defaults to <c>int32</c> per Contract Section 5.1.
/// </para>
/// <para>
/// <see cref="IsFlags"/> reflects either a C# <c>[Flags]</c> attribute
/// or a C++ <c>BlueprintType=Bitmask</c> specifier per UHT precedent.
/// </para>
/// </remarks>
/// <param name="Name">Enum identifier as authored.</param>
/// <param name="FullyQualifiedName">Identifier prefixed with the namespace path.</param>
/// <param name="OuterName">Containing type name, or null for top-level enums.</param>
/// <param name="ModuleName">Manifest module this enum belongs to.</param>
/// <param name="Language">Source language (Cpp / CSharp).</param>
/// <param name="Span">Source location of the enum declaration.</param>
/// <param name="Specifiers">Raw parsed enum-side specifiers.</param>
/// <param name="UnderlyingType">Optional <c>Underlying=</c> specifier value (e.g. <c>"uint8"</c>); null when not declared.</param>
/// <param name="IsFlags">True for bit-flag enums (<c>[Flags]</c> on C# / <c>BlueprintType=Bitmask</c> on C++).</param>
/// <param name="Values">Reflected enum values in declaration order.</param>
public sealed record XhtEnum(
    string Name,
    string FullyQualifiedName,
    string? OuterName,
    string ModuleName,
    Language Language,
    SourceSpan Span,
    IReadOnlyList<Specifier> Specifiers,
    string? UnderlyingType,
    bool IsFlags,
    IReadOnlyList<XhtEnumValue> Values)
    : XhtTypeBase(Name, FullyQualifiedName, OuterName, ModuleName, Language, Span, Specifiers);
