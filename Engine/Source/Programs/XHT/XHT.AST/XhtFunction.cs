// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;

namespace Simgenics.XPact.XHT.AST;

/// <summary>
/// One reflected function (<c>XFUNCTION</c> / <c>[XFunction]</c>) on a
/// class or interface. Per <c>/Documents/XHT.html</c> Rev 7 Section 4.6.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ReturnType"/> and parameter <c>TypeIdentifier</c> values
/// are the type strings as authored; the resolver's
/// <c>StepResolveProperties</c> phase (Section 5.1) resolves them to
/// concrete property handles via the symbol table (Section 5.3).
/// </para>
/// <para>
/// The C++-specific modifiers <see cref="IsStatic"/> /
/// <see cref="IsVirtual"/> / <see cref="IsConst"/> are captured at parse
/// time so the emitter (Section 8) can branch when emitting the function
/// thunk. C#-side functions never set <see cref="IsVirtual"/> /
/// <see cref="IsConst"/>; the C# parser leaves them false.
/// </para>
/// </remarks>
/// <param name="Name">Function name.</param>
/// <param name="ReturnType">Return type as authored; pre-resolution.</param>
/// <param name="Parameters">Parameter list in declaration order.</param>
/// <param name="Specifiers">Raw parsed function-side specifiers.</param>
/// <param name="IsStatic">True for C++ <c>static</c> / C# <c>static</c> functions.</param>
/// <param name="IsVirtual">True for C++ <c>virtual</c> functions (C# parser leaves false).</param>
/// <param name="IsConst">True for C++ trailing-<c>const</c> member functions (C# parser leaves false).</param>
/// <param name="Span">Source location of the function declaration.</param>
public sealed record XhtFunction(
    string Name,
    string ReturnType,
    IReadOnlyList<XhtParam> Parameters,
    IReadOnlyList<Specifier> Specifiers,
    bool IsStatic,
    bool IsVirtual,
    bool IsConst,
    SourceSpan Span);
