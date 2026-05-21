// Copyright Simgenics. All Rights Reserved.

using System;
using Simgenics.XPact.XHT.Tables;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Tables;

/// <summary>
/// Tests for <see cref="CppKeywordTable"/>: exact case-sensitive lookup
/// + XHT marker recognition per Contract Section 1.1 + XHT.html Rev 7
/// Section 3.4.
/// </summary>
public class CppKeywordTableTests
{
    [Theory]
    [InlineData("class", CppKeywordKind.Type)]
    [InlineData("struct", CppKeywordKind.Type)]
    [InlineData("enum", CppKeywordKind.Type)]
    [InlineData("const", CppKeywordKind.Qualifier)]
    [InlineData("static", CppKeywordKind.StorageClass)]
    [InlineData("virtual", CppKeywordKind.Other)]
    [InlineData("override", CppKeywordKind.Other)]
    [InlineData("nullptr", CppKeywordKind.Other)]
    [InlineData("public", CppKeywordKind.AccessModifier)]
    [InlineData("true", CppKeywordKind.Boolean)]
    [InlineData("static_cast", CppKeywordKind.Cast)]
    [InlineData("sizeof", CppKeywordKind.OperatorWord)]
    public void Lookup_ReturnsKindForKnownKeyword(string spelling, CppKeywordKind expectedKind)
    {
        CppKeyword? kw = CppKeywordTable.Lookup(spelling);
        Assert.NotNull(kw);
        Assert.Equal(expectedKind, kw!.Kind);
        Assert.Equal(spelling, kw.Spelling);
    }

    [Fact]
    public void Lookup_IsCaseSensitive()
    {
        // C++ keywords are case-sensitive. Uppercase 'CLASS' is NOT a
        // keyword.
        Assert.Null(CppKeywordTable.Lookup("CLASS"));
        Assert.Null(CppKeywordTable.Lookup("Const"));
        Assert.Null(CppKeywordTable.Lookup("VIRTUAL"));
    }

    [Fact]
    public void Lookup_UnknownIdentifier_ReturnsNull()
    {
        Assert.Null(CppKeywordTable.Lookup("XValve"));
        Assert.Null(CppKeywordTable.Lookup("not_a_keyword"));
        Assert.Null(CppKeywordTable.Lookup(""));
    }

    [Fact]
    public void Lookup_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CppKeywordTable.Lookup(null!));
    }

    [Theory]
    [InlineData("XCLASS")]
    [InlineData("XSTRUCT")]
    [InlineData("XENUM")]
    [InlineData("XINTERFACE")]
    [InlineData("XFUNCTION")]
    [InlineData("XPROPERTY")]
    [InlineData("XDELEGATE")]
    [InlineData("XPARAM")]
    [InlineData("XMETA")]
    [InlineData("XGENERATED_BODY")]
    public void IsXhtMarker_TrueForLockedMarkerVocabulary_PerContractSection11(string marker)
    {
        Assert.True(CppKeywordTable.IsXhtMarker(marker),
            $"'{marker}' must be a recognised XHT marker per Contract Section 1.1.");

        // Cross-check: the lookup also returns the XhtMarker kind.
        CppKeyword? kw = CppKeywordTable.Lookup(marker);
        Assert.NotNull(kw);
        Assert.Equal(CppKeywordKind.XhtMarker, kw!.Kind);
    }

    [Theory]
    [InlineData("class")]    // regular C++ keyword
    [InlineData("const")]    // regular C++ keyword
    [InlineData("virtual")]  // regular C++ keyword
    [InlineData("XValve")]  // arbitrary identifier
    [InlineData("xclass")]   // lowercase form (case-sensitive)
    [InlineData("XCLAS")]    // typo
    [InlineData("")]         // empty string
    public void IsXhtMarker_FalseForNonMarkers(string spelling)
    {
        Assert.False(CppKeywordTable.IsXhtMarker(spelling));
    }

    [Fact]
    public void IsXhtMarker_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CppKeywordTable.IsXhtMarker(null!));
    }

    [Theory]
    [InlineData("XCLASS", true)]
    [InlineData("XGENERATED_BODY", true)]
    [InlineData("class", false)]
    [InlineData("XValve", false)]
    [InlineData("xclass", false)]
    public void IsXhtMarkerSpan_BehavesAsStringOverload(string spelling, bool expected)
    {
        ReadOnlySpan<char> span = spelling.AsSpan();
        Assert.Equal(expected, CppKeywordTable.IsXhtMarkerSpan(span));
    }

    [Fact]
    public void All_ContainsTheTenLockedMarkers()
    {
        int markerCount = 0;
        foreach (CppKeyword kw in CppKeywordTable.All)
        {
            if (kw.Kind == CppKeywordKind.XhtMarker)
            {
                markerCount++;
            }
        }
        Assert.Equal(10, markerCount);
    }
}
