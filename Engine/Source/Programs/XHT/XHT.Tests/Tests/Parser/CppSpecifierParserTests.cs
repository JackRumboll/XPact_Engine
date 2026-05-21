// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Parser.Cpp;
using Simgenics.XPact.XHT.Tables;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Parser;

/// <summary>
/// Tests for <see cref="CppSpecifierParser"/>. Specifier grammar
/// recognition + context validation per
/// <c>/Documents/XHT.html</c> Rev 7 Section 7.2.
/// </summary>
public class CppSpecifierParserTests
{
    private const string Path = "Test.h";

    private static (IReadOnlyList<Specifier> specs, List<DiagnosticRecord> diags)
        ParseSpecifiers(string sourceAfterMarker, SpecifierContext ctx, SpecifierRegistry? registry = null)
    {
        // The harness passes "Foo, Bar=Baz)..."; we simulate the marker
        // consumer having already eaten the opening '('.
        CppTokenizer tok = new(Path, sourceAfterMarker);
        registry ??= new SpecifierRegistry(registerBuiltIns: true);
        CppSpecifierParser parser = new(registry);
        List<DiagnosticRecord> diags = new();
        IReadOnlyList<Specifier> specs = parser.ParseSpecifierList(tok, ctx, diags);
        return (specs, diags);
    }

    [Fact]
    public void EmptyList_ReturnsNoSpecifiers()
    {
        (IReadOnlyList<Specifier> specs, _) = ParseSpecifiers(")", SpecifierContext.Class);
        Assert.Empty(specs);
    }

    [Fact]
    public void SingleFlag_NoValueList_ParsesOK()
    {
        (IReadOnlyList<Specifier> specs, List<DiagnosticRecord> diags) = ParseSpecifiers(
            "EditAnywhere)",
            SpecifierContext.PropertyMember);
        Assert.Single(specs);
        Assert.Equal("EditAnywhere", specs[0].Key);
        Assert.Empty(specs[0].Values);
        Assert.Empty(diags);
    }

    [Fact]
    public void CommaSeparatedFlags_ParseAsTwoSpecifiers()
    {
        (IReadOnlyList<Specifier> specs, List<DiagnosticRecord> diags) = ParseSpecifiers(
            "EditAnywhere, BlueprintReadWrite)",
            SpecifierContext.PropertyMember);
        Assert.Equal(2, specs.Count);
        Assert.Equal("EditAnywhere", specs[0].Key);
        Assert.Equal("BlueprintReadWrite", specs[1].Key);
        Assert.Empty(diags);
    }

    [Fact]
    public void SingleValueQuotedString_StripsQuotes()
    {
        (IReadOnlyList<Specifier> specs, _) = ParseSpecifiers(
            "Category=\"Combat\")",
            SpecifierContext.PropertyMember);
        Assert.Single(specs);
        Assert.Equal("Category", specs[0].Key);
        Assert.Single(specs[0].Values);
        Assert.Equal("Combat", specs[0].Values[0]);
    }

    [Fact]
    public void Reference_ParsesIdentifierValue()
    {
        (IReadOnlyList<Specifier> specs, _) = ParseSpecifiers(
            "Within=AActor)",
            SpecifierContext.Class);
        Assert.Single(specs);
        Assert.Equal("Within", specs[0].Key);
        Assert.Equal("AActor", specs[0].Values[0]);
    }

    [Fact]
    public void PipeSyntax_DeprecatedWarningEmitted_AndSplitsIntoFlags()
    {
        (IReadOnlyList<Specifier> specs, List<DiagnosticRecord> diags) = ParseSpecifiers(
            "BlueprintAuthorityOnly|BlueprintCosmetic)",
            SpecifierContext.Delegate);
        Assert.Equal(2, specs.Count);
        Assert.Equal("BlueprintAuthorityOnly", specs[0].Key);
        Assert.Equal("BlueprintCosmetic", specs[1].Key);
        Assert.Contains(diags, d => d.Code == CppSpecifierParser.DiagDeprecatedPipeSyntax);
    }

    [Fact]
    public void MultipleValuesInParenList_CapturedAsValueArray()
    {
        (IReadOnlyList<Specifier> specs, _) = ParseSpecifiers(
            "HideCategories=(\"Foo\",\"Bar\"))",
            SpecifierContext.Class);
        Assert.Single(specs);
        Assert.Equal("HideCategories", specs[0].Key);
        Assert.Equal(2, specs[0].Values.Count);
        Assert.Equal("Foo", specs[0].Values[0]);
        Assert.Equal("Bar", specs[0].Values[1]);
    }

    [Fact]
    public void UnknownSpecifier_EmitsXHT110()
    {
        (IReadOnlyList<Specifier> specs, List<DiagnosticRecord> diags) = ParseSpecifiers(
            "NotARealSpecifier)",
            SpecifierContext.Class);
        Assert.Single(specs); // record still emitted
        Assert.Contains(diags, d => d.Code == CppSpecifierParser.DiagUnknownSpecifier);
    }

    [Fact]
    public void KnownSpecifierWrongContext_EmitsXHT111()
    {
        // EditAnywhere is legal on PropertyMember; using it on Class
        // triggers XHT111 (context-mismatch).
        (IReadOnlyList<Specifier> specs, List<DiagnosticRecord> diags) = ParseSpecifiers(
            "EditAnywhere)",
            SpecifierContext.Class);
        Assert.Single(specs);
        Assert.Contains(diags, d => d.Code == CppSpecifierParser.DiagSpecifierIllegalInContext);
    }

    [Fact]
    public void CaseInsensitiveLookup_MatchesRegisteredCanonical()
    {
        // The registry is case-insensitive; the parser preserves case for
        // diagnostics but resolves via the case-insensitive registry.
        (IReadOnlyList<Specifier> specs, List<DiagnosticRecord> diags) = ParseSpecifiers(
            "blueprintreadwrite)",
            SpecifierContext.PropertyMember);
        Assert.Single(specs);
        Assert.Equal("blueprintreadwrite", specs[0].Key); // case preserved
        Assert.DoesNotContain(diags, d => d.Code == CppSpecifierParser.DiagUnknownSpecifier);
    }

    [Fact]
    public void MalformedGrammar_RecoversAndContinues()
    {
        // First token is a literal -- not a specifier name. Parser logs
        // XHT065 (renumbered from XHT114 per C7 audit), recovers to
        // next ',' or ')'.
        (IReadOnlyList<Specifier> specs, List<DiagnosticRecord> diags) = ParseSpecifiers(
            "\"oops\", EditAnywhere)",
            SpecifierContext.PropertyMember);
        Assert.Single(specs);
        Assert.Equal("EditAnywhere", specs[0].Key);
        Assert.Contains(diags, d => d.Code == CppSpecifierParser.DiagSpecifierSyntaxError);
    }

    [Fact]
    public void TrailingCommaTolerated()
    {
        (IReadOnlyList<Specifier> specs, _) = ParseSpecifiers(
            "EditAnywhere,)",
            SpecifierContext.PropertyMember);
        Assert.Single(specs);
    }

    [Fact]
    public void NestedMetaBlock_CapturedAsValueArray()
    {
        // meta=(Tooltip="..", DisplayName="..")
        (IReadOnlyList<Specifier> specs, _) = ParseSpecifiers(
            "Meta=(Tooltip=\"Hello\", DisplayName=\"X\"))",
            SpecifierContext.PropertyMember);
        Assert.Single(specs);
        Assert.Equal("Meta", specs[0].Key);
        // Each entry captured as an alternating-tokens string.
        Assert.NotEmpty(specs[0].Values);
    }

    [Fact]
    public void Specifier_SpanRecordsKeyLocation()
    {
        (IReadOnlyList<Specifier> specs, _) = ParseSpecifiers(
            "EditAnywhere)",
            SpecifierContext.PropertyMember);
        Assert.Single(specs);
        Assert.Equal(1, specs[0].Span.Line);
        Assert.Equal(1, specs[0].Span.Column);
        Assert.True(specs[0].Span.Length > 0);
    }

    [Fact]
    public void Specifier_PreservesAuthoredCaseInKey()
    {
        // The Key field is preserved verbatim for diagnostic rendering.
        (IReadOnlyList<Specifier> specs, _) = ParseSpecifiers(
            "BlueprintReadWrite)",
            SpecifierContext.PropertyMember);
        Assert.Equal("BlueprintReadWrite", specs[0].Key);
    }
}
