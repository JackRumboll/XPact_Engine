// Copyright Simgenics. All Rights Reserved.

using System;
using Simgenics.XPact.XHT.Core;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Core;

/// <summary>
/// Tests for <see cref="StringUtils.StripCppPrefix"/> +
/// <see cref="StringUtils.ToCaselessKey"/> per
/// <c>/Documents/XHT.html</c> Rev 5 Section 3.3 (engine-name convention)
/// + Section 5.4 (caseless symbol-table population).
/// </summary>
/// <remarks>
/// Round-2 (2026-05-21) removed the legacy A / U / I / F prefix-strip
/// rule. <see cref="StringUtils.StripCppPrefix"/> is now a no-op
/// pass-through. The engine-name is the lowercase-invariant form of the
/// source identifier; XPact's permanent <c>X</c> prefix is preserved.
/// </remarks>
public class StringUtilsTests
{
    [Theory]
    [InlineData("XValve")]      // X is XPact's permanent prefix, preserved
    [InlineData("XObject")]
    [InlineData("XActor")]
    [InlineData("AXValve")]     // legacy A is NOT stripped under Round-2
    [InlineData("UObject")]     // legacy U is NOT stripped
    [InlineData("FVector")]     // legacy F is NOT stripped
    [InlineData("IInterface")]  // legacy I is NOT stripped
    public void StripCppPrefix_IsNoOp(string input)
    {
        // Round-2: the strip is removed. The function returns its input
        // unchanged for any non-null string.
        Assert.Equal(input, StringUtils.StripCppPrefix(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("F")]
    public void StripCppPrefix_HandlesShortStrings(string input)
    {
        Assert.Equal(input, StringUtils.StripCppPrefix(input));
    }

    [Fact]
    public void StripCppPrefix_NullThrows()
    {
        Assert.Throws<ArgumentNullException>(() => StringUtils.StripCppPrefix(null!));
    }

    [Theory]
    [InlineData("XValve",  "xvalve")]
    [InlineData("AXValve", "axvalve")]  // legacy form lowercases verbatim
    [InlineData("Valve",   "valve")]
    [InlineData("VALVE",   "valve")]
    public void ToCaselessKey_LowercaseInvariant(string input, string expected)
    {
        Assert.Equal(expected, StringUtils.ToCaselessKey(input));
    }

    [Fact]
    public void ToCaselessKey_PreservesUnderscoresAndDigits()
    {
        Assert.Equal("x_valve_42", StringUtils.ToCaselessKey("X_Valve_42"));
    }

    [Fact]
    public void ToCaselessKey_NullThrows()
    {
        Assert.Throws<ArgumentNullException>(() => StringUtils.ToCaselessKey(null!));
    }

    /// <summary>
    /// Cross-language pairing example for Round-2: both the C++ side and
    /// the C# side use the canonical <c>XValve</c> name; they fold to
    /// the same engine name <c>"xvalve"</c>. A bare <c>Valve</c> folds
    /// to <c>"valve"</c> -- a different engine name, no implicit pairing.
    /// </summary>
    [Fact]
    public void StripPlusCaseless_CrossLanguagePairing_XPrefixRound2()
    {
        string cppKey = StringUtils.ToCaselessKey(StringUtils.StripCppPrefix("XValve"));
        string csKey  = StringUtils.ToCaselessKey(StringUtils.StripCppPrefix("XValve"));
        Assert.Equal(cppKey, csKey);
        Assert.Equal("xvalve", cppKey);

        // The bare-name forms differ from the X-prefixed forms.
        Assert.NotEqual(
            StringUtils.ToCaselessKey(StringUtils.StripCppPrefix("XValve")),
            StringUtils.ToCaselessKey(StringUtils.StripCppPrefix("Valve")));
    }
}
