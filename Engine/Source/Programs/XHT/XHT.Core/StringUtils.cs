// Copyright Simgenics. All Rights Reserved.

using System;
using System.Globalization;

namespace Simgenics.XPact.XHT.Core;

/// <summary>
/// String utilities for XHT's symbol-table model. Per
/// <c>/Documents/XHT.html</c> Rev 5 Section 3.3 (prefix-stripping
/// convention) + Section 5.4 (caseless symbol-table population). No
/// culture-specific case folding: identifiers are UTF-8 source per the
/// engine-wide UTF-8 commitment (Contract Section 6), and the caseless
/// symbol-table keys are computed with
/// <see cref="string.ToLowerInvariant"/>.
/// </summary>
public static class StringUtils
{
    /// <summary>
    /// Strippable UE-convention prefix set per
    /// <c>/Documents/XHT.html</c> Rev 5 Section 3.3 (REVISED Rev 3 per
    /// Xm3 + Rev 2 addition with prefix-stripping rule corrected).
    /// <strong><c>X</c> is NOT in this set</strong> -- it is XPact's
    /// permanent project-wide prefix, not a UE-convention prefix.
    /// </summary>
    private static readonly char[] s_strippablePrefixes = new[] { 'A', 'U', 'I', 'F' };

    /// <summary>
    /// Strip a single leading UE-convention prefix letter from a C++
    /// identifier so it pairs with the corresponding C# identifier in
    /// XHT's symbol table per <c>/Documents/XHT.html</c> Rev 5
    /// Section 3.3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stripping rule (Rev 3 correction).</b> The leading uppercase
    /// letter is stripped only when it is one of <c>{A, U, I, F}</c>
    /// AND the next character is also uppercase (the UE convention is
    /// always <c>&lt;Prefix&gt;&lt;UppercaseName&gt;</c>; a single bare
    /// letter is not a UE prefix). Examples:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>"AXValve"</c> -&gt; <c>"XValve"</c> (strip <c>A</c>; <c>X</c> remains as the permanent XPact prefix).</description></item>
    ///   <item><description><c>"UObject"</c> -&gt; <c>"Object"</c> (strip <c>U</c>; legacy UE-named class).</description></item>
    ///   <item><description><c>"FVector"</c> -&gt; <c>"Vector"</c> (strip <c>F</c>; legacy UE-named struct).</description></item>
    ///   <item><description><c>"XValve"</c> -&gt; <c>"XValve"</c> (no strip; <c>X</c> is not in the strippable set).</description></item>
    ///   <item><description><c>"Apple"</c> -&gt; <c>"Apple"</c> (no strip; <c>A</c> is strippable but <c>p</c> is lowercase, so this is not a UE-prefix form).</description></item>
    ///   <item><description><c>"A"</c> -&gt; <c>"A"</c> (no strip; single-character identifier has no second character to check).</description></item>
    /// </list>
    /// </remarks>
    /// <param name="identifier">The source C++ identifier. Must not be null.</param>
    /// <returns>The identifier with the UE-convention prefix stripped, or the identifier unchanged if none applies.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="identifier"/> is null.</exception>
    public static string StripCppPrefix(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        if (identifier.Length < 2)
        {
            return identifier;
        }

        char first = identifier[0];
        char second = identifier[1];

        // The second character must be uppercase for the leading letter
        // to be interpreted as a UE-convention prefix.
        if (!char.IsUpper(second))
        {
            return identifier;
        }

        bool isStrippable = false;
        for (int i = 0; i < s_strippablePrefixes.Length; i++)
        {
            if (s_strippablePrefixes[i] == first)
            {
                isStrippable = true;
                break;
            }
        }

        if (!isStrippable)
        {
            return identifier;
        }

        return identifier.Substring(1);
    }

    /// <summary>
    /// Compute the caseless symbol-table key for an identifier per
    /// <c>/Documents/XHT.html</c> Rev 5 Section 5.4. UTF-8 source is
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
