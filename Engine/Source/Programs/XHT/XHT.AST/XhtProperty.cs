// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;

namespace Simgenics.XPact.XHT.AST;

/// <summary>
/// One reflected property (<c>XPROPERTY</c> / <c>[XProperty]</c>) on a
/// class, struct, or function parameter list. Per
/// <c>/Documents/XHT.html</c> Rev 8 Section 4.7.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TypeIdentifier"/> is the type as authored (e.g.
/// <c>"int32"</c>, <c>"TArray&lt;FString&gt;"</c>, <c>"XValve*"</c>);
/// the resolver's <c>StepResolveProperties</c> phase (Section 5.1)
/// resolves it to a concrete <c>XhtProperty</c> subclass handle. Phase 1b
/// only carries the string form; the concrete <c>XhtIntProperty</c> /
/// <c>XhtObjectProperty</c> / etc. subclasses ship in Phase 1c+ alongside
/// the parser that detects them.
/// </para>
/// <para>
/// <see cref="RepNotifyFunctionName"/> is the validated callback name
/// from a <c>ReplicatedUsing=&lt;funcName&gt;</c> specifier; per the
/// Rev 4 phase model (Section 5.1) the symbol lookup happens during the
/// <c>StepResolveFinal</c> pass so the callback's signature can be
/// validated against the resolved property type (XHT113). Phase 1b
/// carries null.
/// </para>
/// </remarks>
/// <param name="Name">Property name.</param>
/// <param name="TypeIdentifier">Type as authored in source; pre-resolution.</param>
/// <param name="Specifiers">Raw parsed property-side specifiers.</param>
/// <param name="IsContainer">True for container properties (<c>TArray</c>, <c>TMap</c>, etc.); resolver detects inner type in Phase 2.</param>
/// <param name="RepNotifyFunctionName">Optional resolved <c>ReplicatedUsing</c> callback name; null in Phase 1b.</param>
/// <param name="Category">Optional <c>Category=</c> specifier value (UI grouping).</param>
/// <param name="Span">Source location of the property declaration.</param>
public sealed record XhtProperty(
    string Name,
    string TypeIdentifier,
    IReadOnlyList<Specifier> Specifiers,
    bool IsContainer,
    string? RepNotifyFunctionName,
    string? Category,
    SourceSpan Span);
