// Copyright Simgenics. All Rights Reserved.

using System;
using Simgenics.XPact.XHT.AST;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.AST;

/// <summary>
/// Tests for <see cref="XhtStruct"/>. Per <c>/Documents/XHT.html</c>
/// Rev 7 Section 4.1 + Section 19.5 FastArraySerializer detection.
/// </summary>
public class XhtStructTests
{
    private static SourceSpan TestSpan() => new("Test.h", 1, 1, 5);

    [Fact]
    public void Construct_PopulatesFields()
    {
        XhtStruct s = new(
            Name: "FVector",
            FullyQualifiedName: "Industrial::FVector",
            OuterName: null,
            ModuleName: "XCore",
            Language: Language.Cpp,
            Span: TestSpan(),
            Specifiers: Array.Empty<Specifier>(),
            SuperIdentifier: null,
            Super: null,
            Properties: Array.Empty<XhtProperty>(),
            IsFastArraySerializer: false);

        Assert.Equal("FVector", s.Name);
        Assert.Equal(Language.Cpp, s.Language);
        Assert.False(s.IsFastArraySerializer);
    }

    [Fact]
    public void CaselessKey_LegacyFPrefix_IsNotStripped_LowercasedVerbatim()
    {
        // Round-2 (2026-05-21): UE-convention F prefix is no longer
        // stripped. FVector lowercases to "fvector".
        XhtStruct s = MakeMinimal("FVector");
        Assert.Equal("fvector", s.CaselessKey);
    }

    [Fact]
    public void CaselessKey_CSharpName_LowercasesVerbatim()
    {
        XhtStruct s = MakeMinimal("Vector");
        Assert.Equal("vector", s.CaselessKey);
    }

    [Fact]
    public void CaselessKey_XStruct_PreservesX_LowercasesVerbatim()
    {
        // Canonical XPact struct naming: XVector. The X prefix is the
        // permanent project prefix and is retained.
        XhtStruct s = MakeMinimal("XVector");
        Assert.Equal("xvector", s.CaselessKey);
    }

    [Fact]
    public void IsFastArraySerializer_DefaultPhase1b_False()
    {
        XhtStruct s = MakeMinimal("FVector");
        Assert.False(s.IsFastArraySerializer);
    }

    private static XhtStruct MakeMinimal(string name)
    {
        return new XhtStruct(
            Name: name,
            FullyQualifiedName: name,
            OuterName: null,
            ModuleName: "XCore",
            Language: Language.Cpp,
            Span: TestSpan(),
            Specifiers: Array.Empty<Specifier>(),
            SuperIdentifier: null,
            Super: null,
            Properties: Array.Empty<XhtProperty>(),
            IsFastArraySerializer: false);
    }
}
