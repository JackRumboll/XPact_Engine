// Copyright Simgenics. All Rights Reserved.

using System;
using Simgenics.XPact.XHT.Core;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Core;

/// <summary>
/// Tests for <see cref="StringUtils.StripCppPrefix"/> + <see cref="StringUtils.ToCaselessKey"/>
/// per <c>/Documents/XHT.html</c> Rev 5 Section 3.3 (prefix-stripping
/// convention) + Section 5.4 (caseless symbol-table population).
/// </summary>
public class StringUtilsTests
{
    [Theory]
    [InlineData("AXValve", "XValve")]    // strip A; X is XPact's permanent prefix and remains
    [InlineData("UObject", "Object")]    // strip U; legacy UE name
    [InlineData("FVector", "Vector")]    // strip F; legacy UE struct
    [InlineData("IInterface", "Interface")] // strip I; legacy UE interface
    public void StripCppPrefix_StripsKnownPrefixes(string input, string expected)
    {
        Assert.Equal(expected, StringUtils.StripCppPrefix(input));
    }

    [Theory]
    [InlineData("XValve")]   // X is NOT strippable; XPact's permanent prefix
    [InlineData("XObject")]
    [InlineData("XActor")]
    public void StripCppPrefix_DoesNotStripX(string input)
    {
        Assert.Equal(input, StringUtils.StripCppPrefix(input));
    }

    [Theory]
    [InlineData("Apple")]    // A is strippable, but 'p' is lowercase so not UE-prefix form
    [InlineData("Underline")] // U is strippable but 'n' is lowercase
    [InlineData("Float")]    // F is strippable but 'l' is lowercase
    [InlineData("Item")]     // I is strippable but 't' is lowercase
    public void StripCppPrefix_RequiresUppercaseSecondChar(string input)
    {
        Assert.Equal(input, StringUtils.StripCppPrefix(input));
    }

    [Theory]
    [InlineData("BBoxedClass")]   // B is not in the strippable set
    [InlineData("CContainer")]    // C is not in the strippable set
    [InlineData("MMacro")]        // M is not in the strippable set
    public void StripCppPrefix_DoesNotStripNonUeConventionLetters(string input)
    {
        Assert.Equal(input, StringUtils.StripCppPrefix(input));
    }

    [Theory]
    [InlineData("")]        // empty
    [InlineData("A")]       // single char, no second to test uppercase
    [InlineData("F")]
    public void StripCppPrefix_TooShortToStrip(string input)
    {
        Assert.Equal(input, StringUtils.StripCppPrefix(input));
    }

    [Fact]
    public void StripCppPrefix_NullThrows()
    {
        Assert.Throws<ArgumentNullException>(() => StringUtils.StripCppPrefix(null!));
    }

    [Fact]
    public void StripCppPrefix_AXValveIsSinglePass()
    {
        // Per XHT.html Section 3.3: stripping is single-pass, not
        // iterative. "AXValve" -> "XValve" (NOT "Valve"); the X is kept
        // because it's XPact's permanent prefix, not a UE convention.
        string stripped = StringUtils.StripCppPrefix("AXValve");
        Assert.Equal("XValve", stripped);
        // Calling strip again shouldn't strip further (X is not in the set).
        Assert.Equal("XValve", StringUtils.StripCppPrefix(stripped));
    }

    [Theory]
    [InlineData("AXValve", "axvalve")]
    [InlineData("XValve",  "xvalve")]
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
    /// Cross-language partial-pairing example from XHT.html Section 3.3:
    /// C++ <c>AXValve</c> (with the optional A prefix dropped) and C#
    /// <c>XValve</c> must produce the same caseless key.
    /// </summary>
    [Fact]
    public void StripPlusCaseless_CrossLanguagePairing()
    {
        string cppKey = StringUtils.ToCaselessKey(StringUtils.StripCppPrefix("AXValve"));
        string csKey  = StringUtils.ToCaselessKey("XValve");
        Assert.Equal(cppKey, csKey);
        Assert.Equal("xvalve", cppKey);
    }
}
