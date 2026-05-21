// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;

namespace Simgenics.XPact.XHT.AST;

/// <summary>
/// One function parameter on an <see cref="XhtFunction"/> or
/// <see cref="XhtDelegate"/>. Per <c>/Documents/XHT.html</c> Rev 5
/// Section 4.6 + the <c>XPARAM</c> marker (Contract Section 1.1).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TypeIdentifier"/> is the type as authored (e.g.
/// <c>"int32"</c>, <c>"TArray&lt;FString&gt;"</c>, <c>"AXValve*"</c>); the
/// resolver's <c>StepResolveProperties</c> phase (Section 5.1) resolves
/// it to a concrete <c>XhtProperty</c> handle. Phase 1b only carries the
/// string form.
/// </para>
/// </remarks>
/// <param name="Name">Parameter name.</param>
/// <param name="TypeIdentifier">Type as authored in source; pre-resolution.</param>
/// <param name="Specifiers">Raw parsed parameter-side specifiers.</param>
/// <param name="IsOut">True for C++ <c>out</c> / C# <c>out</c>-decorated parameters.</param>
/// <param name="IsRef">True for C++ <c>&amp;</c> / C# <c>ref</c>-decorated parameters.</param>
/// <param name="Span">Source location of the parameter.</param>
public sealed record XhtParam(
    string Name,
    string TypeIdentifier,
    IReadOnlyList<Specifier> Specifiers,
    bool IsOut,
    bool IsRef,
    SourceSpan Span);
