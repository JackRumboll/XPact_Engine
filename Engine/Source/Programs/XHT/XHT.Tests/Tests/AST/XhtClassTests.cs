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
            Name: "AXValve",
            FullyQualifiedName: "Industrial::AXValve",
            OuterName: null,
            ModuleName: "XScoring",
            Language: Language.Cpp,
            Span: TestSpan(),
            Specifiers: Array.Empty<Specifier>(),
            SuperIdentifier: "AXActor",
            Super: null,
            Functions: Array.Empty<XhtFunction>(),
            Properties: Array.Empty<XhtProperty>(),
            InterfaceIdentifiers: Array.Empty<string>(),
            Interfaces: Array.Empty<XhtInterface>(),
            WithinIdentifier: null,
            WithinClass: null,
            RequiredAPIMacroName: "XSCORING_API",
            HasGeneratedBody: true);

        Assert.Equal("AXValve", c.Name);
        Assert.Equal("Industrial::AXValve", c.FullyQualifiedName);
        Assert.Null(c.OuterName);
        Assert.Equal("XScoring", c.ModuleName);
        Assert.Equal(Language.Cpp, c.Language);
        Assert.Equal("AXActor", c.SuperIdentifier);
        Assert.Null(c.Super);
        Assert.Empty(c.Interfaces);
        Assert.Equal("XSCORING_API", c.RequiredAPIMacroName);
        Assert.True(c.HasGeneratedBody);
    }

    [Fact]
    public void CaselessKey_CppPrefixedName_StripsLeadingPrefix_AndLowercases()
    {
        // Section 3.3 worked example: AXValve -> strip A -> xvalve.
        XhtClass c = MakeMinimal("AXValve");
        Assert.Equal("xvalve", c.CaselessKey);
    }

    [Fact]
    public void CaselessKey_CSharpName_LowercasesWithoutPrefixStrip()
    {
        // Section 3.3 C# example: Valve -> no UE prefix to strip -> valve.
        XhtClass c = MakeMinimal("Valve");
        Assert.Equal("valve", c.CaselessKey);
    }

    [Fact]
    public void CaselessKey_XPrefixedCppName_DoesNotStripX()
    {
        // Section 3.3 X-is-permanent rule: XValve -> xvalve (X retained).
        XhtClass c = MakeMinimal("XValve");
        Assert.Equal("xvalve", c.CaselessKey);
    }

    [Fact]
    public void Record_WithSuperPattern_ProducesCopyWithMutatedField()
    {
        // The resolver mutates pointer fields via record `with` syntax.
        XhtClass parent = MakeMinimal("AXActor");
        XhtClass child = MakeMinimal("AXValve");
        XhtClass resolved = child with { Super = parent };

        Assert.Null(child.Super);                  // original immutable
        Assert.Same(parent, resolved.Super);       // resolved carries pointer
        Assert.Equal(child.Name, resolved.Name);   // other fields preserved
    }

    [Fact]
    public void Equality_TwoIdenticalRecords_AreValueEqual()
    {
        XhtClass a = MakeMinimal("AXValve");
        XhtClass b = MakeMinimal("AXValve");
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
