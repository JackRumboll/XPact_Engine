// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;

namespace Simgenics.XPact.XHT.AST;

/// <summary>
/// One reflected class (<c>XCLASS</c> / <c>[XClass]</c>). Per
/// <c>/Documents/XHT.html</c> Rev 7 Section 4.1 + Section 7.4.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolver-mutated pointer fields.</b> <see cref="Super"/>,
/// <see cref="Interfaces"/>, and <see cref="WithinClass"/> are set null /
/// empty at parse time and populated by the resolver across its three
/// inheritance-related phases per Section 5.1:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>StepBindSuperAndBases</c> populates <see cref="Super"/> from
///     <see cref="SuperIdentifier"/> via the caseless symbol table.
///   </description></item>
///   <item><description>
///     <c>StepResolveBases</c> populates <see cref="Interfaces"/> from
///     <see cref="InterfaceIdentifiers"/> and <see cref="WithinClass"/>
///     from <see cref="WithinIdentifier"/>.
///   </description></item>
///   <item><description>
///     <c>StepResolveFinal</c> runs the <c>XHT119
///     ClassWithinIncompatibleWithSuper</c> check per Section 6.2.
///   </description></item>
/// </list>
/// <para>
/// Records are immutable; the resolver mutates by record <c>with</c>
/// reassignment (<c>var resolved = c with { Super = found };</c>). Phase 1b
/// holds the post-parse shape -- nulls and empty lists everywhere except
/// the identifier strings.
/// </para>
/// </remarks>
/// <param name="Name">Class identifier as authored (e.g. <c>"XValve"</c>, <c>"Valve"</c>).</param>
/// <param name="FullyQualifiedName">Identifier prefixed with the namespace path.</param>
/// <param name="OuterName">Containing type name, or null for top-level classes.</param>
/// <param name="ModuleName">Manifest module this class belongs to.</param>
/// <param name="Language">Source language (Cpp / CSharp).</param>
/// <param name="Span">Source location of the class declaration.</param>
/// <param name="Specifiers">Raw parsed class-side specifiers.</param>
/// <param name="SuperIdentifier">Super-class identifier as authored; null when the class has no super.</param>
/// <param name="Super">Resolved super-class pointer; null until <c>StepBindSuperAndBases</c> populates it.</param>
/// <param name="Functions">Reflected member functions.</param>
/// <param name="Properties">Reflected member properties.</param>
/// <param name="InterfaceIdentifiers">Implemented-interface identifiers as authored; resolved in <c>StepResolveBases</c>.</param>
/// <param name="Interfaces">Resolved interface pointer list; empty until <c>StepResolveBases</c> populates it.</param>
/// <param name="WithinIdentifier">Optional <c>Within=&lt;TypeName&gt;</c> specifier value; null when not declared.</param>
/// <param name="WithinClass">Resolved outer-type pointer for <c>Within=</c>; null until <c>StepResolveBases</c> populates it.</param>
/// <param name="RequiredAPIMacroName">Optional <c>&lt;MODULE&gt;_API</c> export macro name; derived per Section 7.4 Round-2 fix.</param>
/// <param name="HasGeneratedBody">True when <c>XGENERATED_BODY()</c> was found in the class body (C++ only; C# always emits the body inline).</param>
/// <param name="IsPartial">True when the C# <c>partial</c> modifier appears on this declaration. C++ classes are always false. The resolver merges partials with the same FullyQualifiedName per Section 3.3.</param>
/// <param name="PartialSourcePaths">Set of source paths that contributed to a merged partial class. Singleton list <c>[Span.SourceFilePath]</c> on a freshly-parsed declaration; populated to the union by <c>StepResolvePairings</c> when partials are merged.</param>
public sealed record XhtClass(
    string Name,
    string FullyQualifiedName,
    string? OuterName,
    string ModuleName,
    Language Language,
    SourceSpan Span,
    IReadOnlyList<Specifier> Specifiers,
    string? SuperIdentifier,
    XhtClass? Super,
    IReadOnlyList<XhtFunction> Functions,
    IReadOnlyList<XhtProperty> Properties,
    IReadOnlyList<string> InterfaceIdentifiers,
    IReadOnlyList<XhtInterface> Interfaces,
    string? WithinIdentifier,
    XhtClass? WithinClass,
    string? RequiredAPIMacroName,
    bool HasGeneratedBody,
    bool IsPartial = false,
    IReadOnlyList<string>? PartialSourcePaths = null)
    : XhtTypeBase(Name, FullyQualifiedName, OuterName, ModuleName, Language, Span, Specifiers);
