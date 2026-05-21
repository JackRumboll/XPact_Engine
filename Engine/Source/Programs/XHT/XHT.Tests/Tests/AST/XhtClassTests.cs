// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XHT.AST;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.AST;

/// <summary>
/// Tests for <see cref="XhtClass"/>. The record models a reflected class
/// (<c>XCLASS</c> / <c>[XClass]</c>) per <c>/Documents/XHT.html</c> Rev 5
/// Section 4.1 + Section 7.4.
/// </summary>
public class XhtClassTests
{
    private static SourceSpan TestSpan(int line = 1, int column = 1, int length = 5)
        => new("Test.h", line, column, length);

    [Fact]
    public void Construct_TopLevelClass_PopulatesAllFields()
    {
        XhtClass c = new(
            Name: "XValve",
            FullyQualifiedName: "Industrial::XValve",
            OuterName: null,
            ModuleName: "XScoring",
            Language: Language.Cpp,
            Span: TestSpan(),
            Specifiers: Array.Empty<Specifier>(),
            SuperIdentifier: "XActor",
            Super: null,
            Functions: Array.Empty<XhtFunction>(),
            Properties: Array.Empty<XhtProperty>(),
            InterfaceIdentifiers: Array.Empty<string>(),
            Interfaces: Array.Empty<XhtInterface>(),
            WithinIdentifier: null,
            WithinClass: null,
            RequiredAPIMacroName: "XSCORING_API",
            HasGeneratedBody: true);

        Assert.Equal("XValve", c.Name);
        Assert.Equal("Industrial::XValve", c.FullyQualifiedName);
        Assert.Null(c.OuterName);
        Assert.Equal("XScoring", c.ModuleName);
        Assert.Equal(Language.Cpp, c.Language);
        Assert.Equal("XActor", c.SuperIdentifier);
        Assert.Null(c.Super);
        Assert.Empty(c.Interfaces);
        Assert.Equal("XSCORING_API", c.RequiredAPIMacroName);
        Assert.True(c.HasGeneratedBody);
    }

    [Fact]
    public void CaselessKey_PreservesX_AndLowercases()
    {
        // Round-2 Section 3.3: the engine-name is the source identifier
        // lowercased verbatim. XValve -> xvalve (X retained as XPact's
        // permanent prefix; no UE-convention strip applied).
        XhtClass c = MakeMinimal("XValve");
        Assert.Equal("xvalve", c.CaselessKey);
    }

    [Fact]
    public void CaselessKey_CSharpName_LowercasesVerbatim()
    {
        XhtClass c = MakeMinimal("Valve");
        Assert.Equal("valve", c.CaselessKey);
    }

    [Fact]
    public void CaselessKey_LegacyUePrefixedName_IsLowercasedVerbatim()
    {
        // Round-2: legacy UE-style names (AXValve) are no longer stripped.
        // They lowercase verbatim and produce a different engine-name
        // from the canonical XValve. The expected XPact source form is
        // simply "XValve".
        XhtClass c = MakeMinimal("AXValve");
        Assert.Equal("axvalve", c.CaselessKey);
    }

    [Fact]
    public void Record_WithSuperPattern_ProducesCopyWithMutatedField()
    {
        // The resolver mutates pointer fields via record `with` syntax.
        XhtClass parent = MakeMinimal("XActor");
        XhtClass child = MakeMinimal("XValve");
        XhtClass resolved = child with { Super = parent };

        Assert.Null(child.Super);                  // original immutable
        Assert.Same(parent, resolved.Super);       // resolved carries pointer
        Assert.Equal(child.Name, resolved.Name);   // other fields preserved
    }

    [Fact]
    public void Equality_TwoIdenticalRecords_AreValueEqual()
    {
        XhtClass a = MakeMinimal("XValve");
        XhtClass b = MakeMinimal("XValve");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    private static XhtClass MakeMinimal(string name)
    {
        return new XhtClass(
            Name: name,
            FullyQualifiedName: name,
            OuterName: null,
            ModuleName: "XScoring",
            Language: Language.Cpp,
            Span: TestSpan(),
            Specifiers: Array.Empty<Specifier>(),
            SuperIdentifier: null,
            Super: null,
            Functions: Array.Empty<XhtFunction>(),
            Properties: Array.Empty<XhtProperty>(),
            InterfaceIdentifiers: Array.Empty<string>(),
            Interfaces: Array.Empty<XhtInterface>(),
            WithinIdentifier: null,
            WithinClass: null,
            RequiredAPIMacroName: null,
            HasGeneratedBody: false);
    }
}
