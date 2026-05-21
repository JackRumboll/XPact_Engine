// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;

namespace Simgenics.XPact.XHT.AST;

/// <summary>
/// One reflected struct (<c>XSTRUCT</c> / <c>[XStruct]</c>). Per
/// <c>/Documents/XHT.html</c> Rev 5 Section 4.1.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsFastArraySerializer"/> tracks whether the struct derives
/// from the <c>FFastArraySerializer</c> base type per Section 19.5
/// rationale and Phase 2 detection plan -- the determination happens in
/// the resolver during <c>StepResolveBases</c> (after the inheritance
/// chain is walked). Phase 1b defaults the flag to false.
/// </para>
/// <para>
/// The resolver mutates <see cref="Super"/> by record <c>with</c>
/// reassignment during <c>StepBindSuperAndBases</c>; Phase 1b holds the
/// post-parse shape with <see cref="Super"/> = null.
/// </para>
/// </remarks>
/// <param name="Name">Struct identifier as authored (e.g. <c>"FVector"</c>).</param>
/// <param name="FullyQualifiedName">Identifier prefixed with the namespace path.</param>
/// <param name="OuterName">Containing type name, or null for top-level structs.</param>
/// <param name="ModuleName">Manifest module this struct belongs to.</param>
/// <param name="Language">Source language (Cpp / CSharp).</param>
/// <param name="Span">Source location of the struct declaration.</param>
/// <param name="Specifiers">Raw parsed struct-side specifiers.</param>
/// <param name="SuperIdentifier">Super-struct identifier as authored; null when the struct has no super.</param>
/// <param name="Super">Resolved super-struct pointer; null until <c>StepBindSuperAndBases</c> populates it.</param>
/// <param name="Properties">Reflected member properties.</param>
/// <param name="IsFastArraySerializer">True iff the struct derives from <c>FFastArraySerializer</c>; Phase 2 detection (false in Phase 1b).</param>
public sealed record XhtStruct(
    string Name,
    string FullyQualifiedName,
    string? OuterName,
    string ModuleName,
    Language Language,
    SourceSpan Span,
    IReadOnlyList<Specifier> Specifiers,
    string? SuperIdentifier,
    XhtStruct? Super,
    IReadOnlyList<XhtProperty> Properties,
    bool IsFastArraySerializer)
    : XhtTypeBase(Name, FullyQualifiedName, OuterName, ModuleName, Language, Span, Specifiers);
