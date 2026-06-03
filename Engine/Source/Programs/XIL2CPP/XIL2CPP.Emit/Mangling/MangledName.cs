// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Simgenics.XPact.XIL2CPP.Core;

namespace Simgenics.XPact.XIL2CPP.Emit.Mangling;

/// <summary>
/// The product of mangling one symbol per
/// <c>/Documents/XToolchainContract.html</c> Section 2.2 / 2.3: the
/// conceptual (human-readable, <c>::</c>-qualified, parenthesized)
/// <see cref="CanonicalForm"/> and the linker-visible
/// <see cref="LinkerSymbol"/> (the deterministic translation of the
/// canonical form -- <c>::</c> becomes <c>__</c>, parentheses are dropped,
/// commas / spaces become <c>_</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure-function result.</b> <see cref="Diagnostics"/> carries any
/// forward-commitment warnings the mangle raised (e.g. <c>XIL2CPP179</c> for
/// the <c>ref readonly</c> private-extension discriminator, or
/// <c>XIL2CPP123</c> for a generic nesting that exceeded the one-level Phase
/// 6.e bound). The <see cref="Mangling.Mangler"/> stays a pure function: it
/// never touches the process-global logger; the caller (Pass 5 driver / the
/// emit pass) decides whether to forward the diagnostics to the logger.
/// </para>
/// </remarks>
/// <param name="CanonicalForm">
/// The conceptual mangled name per Contract Section 2.2 (e.g.
/// <c>_v1ab12cd34__Simgenics::XPact::GameFramework::Valve::SetOpenFraction_P_(R Simgenics::XPact::GameFramework::Valve, V float)</c>).
/// </param>
/// <param name="LinkerSymbol">
/// The linker-visible symbol per Contract Section 2.3 (the canonical form
/// with <c>::</c> to <c>__</c>, parentheses dropped, comma / space to
/// <c>_</c>).
/// </param>
/// <param name="Diagnostics">
/// Forward-commitment warnings raised during the mangle (possibly empty,
/// never null).
/// </param>
public readonly record struct MangledName(
    string CanonicalForm,
    string LinkerSymbol,
    IReadOnlyList<DiagnosticRecord> Diagnostics);
