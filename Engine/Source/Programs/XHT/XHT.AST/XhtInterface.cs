// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;

namespace Simgenics.XPact.XHT.AST;

/// <summary>
/// One reflected interface (<c>XINTERFACE</c> / <c>[XInterface]</c>). Per
/// <c>/Documents/XHT.html</c> Rev 8 Section 4.5.
/// </summary>
/// <remarks>
/// <para>
/// On the C++ side, <c>XINTERFACE</c> produces two paired AST nodes (the
/// <c>U</c>-prefix companion class and the <c>I</c>-prefix native
/// interface) which the resolver's <c>StepResolvePairings</c> phase
/// links per Section 4.5 + Section 5.1. Phase 1b carries the
/// <see cref="XhtInterface"/> shell only; the pairing logic ships with
/// the parser in Phase 1c.
/// </para>
/// <para>
/// On the C# side, only the <c>I</c>-prefix node is produced (C#
/// interfaces are first-class and don't need the C++ vtable-anchor
/// companion type per Section 4.5).
/// </para>
/// </remarks>
/// <param name="Name">Interface identifier as authored.</param>
/// <param name="FullyQualifiedName">Identifier prefixed with the namespace path.</param>
/// <param name="OuterName">Containing type name, or null for top-level interfaces.</param>
/// <param name="ModuleName">Manifest module this interface belongs to.</param>
/// <param name="Language">Source language (Cpp / CSharp).</param>
/// <param name="Span">Source location of the interface declaration.</param>
/// <param name="Specifiers">Raw parsed interface-side specifiers.</param>
/// <param name="SuperIdentifier">Optional super-interface identifier; interfaces can extend other interfaces.</param>
/// <param name="Super">Resolved super-interface pointer; null until <c>StepBindSuperAndBases</c> populates it.</param>
/// <param name="Functions">Reflected interface functions (the contract the implementer must honour).</param>
public sealed record XhtInterface(
    string Name,
    string FullyQualifiedName,
    string? OuterName,
    string ModuleName,
    Language Language,
    SourceSpan Span,
    IReadOnlyList<Specifier> Specifiers,
    string? SuperIdentifier,
    XhtInterface? Super,
    IReadOnlyList<XhtFunction> Functions)
    : XhtTypeBase(Name, FullyQualifiedName, OuterName, ModuleName, Language, Span, Specifiers);
