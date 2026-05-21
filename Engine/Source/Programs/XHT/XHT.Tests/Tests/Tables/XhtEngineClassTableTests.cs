// Copyright Simgenics. All Rights Reserved.

using System;
using Simgenics.XPact.XHT.Tables;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Tables;

/// <summary>
/// Tests for <see cref="XhtEngineClassTable"/>: exact case-sensitive
/// lookup of engine-anchor types per <c>/Documents/XHT.html</c> Rev 7
/// Section 2 (Rev 2 addition mirroring UHT's UhtEngineClassTable).
/// </summary>
public class XhtEngineClassTableTests
{
    [Theory]
    [InlineData("XObject", EngineClassRole.XObject)]
    [InlineData("XClass", EngineClassRole.XClass)]
    [InlineData("XStruct", EngineClassRole.XStruct)]
    [InlineData("XInterface", EngineClassRole.XInterface)]
    [InlineData("XEnum", EngineClassRole.XEnum)]
    [InlineData("XFunction", EngineClassRole.XFunction)]
    [InlineData("XProperty", EngineClassRole.XProperty)]
    public void Lookup_HitsExactNameForEachAnchor(string name, EngineClassRole expected)
    {
        EngineClassRole? role = XhtEngineClassTable.Lookup(name);
        Assert.True(role.HasValue, $"Anchor '{name}' must be registered.");
        Assert.Equal(expected, role!.Value);
    }

    [Fact]
    public void Lookup_CaseMismatch_ReturnsNull()
    {
        // Anchor lookups are case-sensitive per Contract Section 1 (reserved
        // identifiers). The Section 7.2 case-insensitivity rule applies to
        // specifier-name lookup, NOT to engine-anchor lookup.
        Assert.Null(XhtEngineClassTable.Lookup("xobject"));
        Assert.Null(XhtEngineClassTable.Lookup("XOBJECT"));
        Assert.Null(XhtEngineClassTable.Lookup("xClass"));
        Assert.Null(XhtEngineClassTable.Lookup("xstruct"));
    }

    [Fact]
    public void Lookup_ArbitraryTypeName_ReturnsNull()
    {
        Assert.Null(XhtEngineClassTable.Lookup("XValve"));
        Assert.Null(XhtEngineClassTable.Lookup("Valve"));
        Assert.Null(XhtEngineClassTable.Lookup("UObject"));   // UE convention name; not adopted
        Assert.Null(XhtEngineClassTable.Lookup(""));
    }

    [Fact]
    public void Lookup_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => XhtEngineClassTable.Lookup(null!));
    }

    [Theory]
    [InlineData("XObject", true)]
    [InlineData("XClass", true)]
    [InlineData("XProperty", true)]
    [InlineData("XValve", false)]
    [InlineData("xobject", false)]
    [InlineData("UObject", false)]
    [InlineData("", false)]
    public void IsEngineClass_MatchesLookupAffirmative(string name, bool expected)
    {
        Assert.Equal(expected, XhtEngineClassTable.IsEngineClass(name));
    }

    [Fact]
    public void IsEngineClass_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => XhtEngineClassTable.IsEngineClass(null!));
    }

    [Fact]
    public void All_ContainsExactlySevenAnchors()
    {
        Assert.Equal(7, XhtEngineClassTable.All.Count);
    }
}
