// Copyright Simgenics. All Rights Reserved.

using System;
using System.Globalization;

namespace Simgenics.XPact.XHT.Core;

/// <summary>
/// String utilities for XHT's symbol-table model. Per
/// <c>/Documents/XHT.html</c> Rev 7 Section 3.3 (engine-name convention)
/// + Section 5.4 (caseless symbol-table population). No culture-specific
/// case folding: identifiers are UTF-8 source per the engine-wide UTF-8
/// commitment (Contract Section 6), and the caseless symbol-table keys
/// are computed with <see cref="string.ToLowerInvariant"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Round-2 simplification (user directive 2026-05-21).</b> The
/// previous prefix-strip rule (drop one of <c>A / U / I / F</c> when
/// followed by uppercase) is gone. XPact uses one and only one
/// project-permanent prefix: <c>X</c>. Engine source no longer carries
/// the UE-convention single-letter prefixes; the engine-name of a C++
/// <c>XValve</c> and a C# <c>XValve</c> is simply <c>"xvalve"</c>
/// without any leading-letter strip. The
/// <see cref="StripCppPrefix(string)"/> entry point is retained for ABI
/// stability but is a no-op pass-through that returns the input
/// unchanged.
/// </para>
/// </remarks>
public static class StringUtils
{
    /// <summary>
    /// No-op identifier pass-through retained as a stable API surface
    /// for callers that still spell the legacy <c>StripCppPrefix</c>
    /// transform. Per Round-2 the engine-name is the source identifier
    /// verbatim (lowercased by <see cref="ToCaselessKey(string)"/>); no
    /// letter is stripped.
    /// </summary>
    /// <param name="identifier">The source C++ / C# identifier. Must not be null.</param>
    /// <returns>The identifier unchanged.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="identifier"/> is null.</exception>
    public static string StripCppPrefix(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return identifier;
    }

    /// <summary>
    /// Compute the caseless symbol-table key for an identifier per
    /// <c>/Documents/XHT.html</c> Rev 7 Section 5.4. UTF-8 source is
    /// assumed; no culture-specific case folding (
    /// <see cref="CultureInfo.InvariantCulture"/> would be redundant on
    /// the lowercase invariant path).
    /// </summary>
    /// <param name="identifier">The identifier to normalise. Must not be null.</param>
    /// <returns>The lowercase-invariant form of <paramref name="identifier"/>.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="identifier"/> is null.</exception>
    public static string ToCaselessKey(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return identifier.ToLowerInvariant();
    }
}
