// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Parser.CSharp;
using Simgenics.XPact.XHT.Tables;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Parser;

/// <summary>
/// Tests for <see cref="CSharpSpecifierExtractor"/>. Argument-form
/// recognition + registry validation per
/// <c>/Documents/XHT.html</c> Rev 7 Section 3.2 + Section 7.2.
/// </summary>
public class CSharpSpecifierExtractorTests
{
    private const string Path = "Test.cs";

    private static (IReadOnlyList<Specifier> specs, List<DiagnosticRecord> diags) Extract(
        string attributeArgs,
        SpecifierContext context,
        SpecifierRegistry? registry = null)
    {
        // Wrap the args in a synthetic [XClass(...)] attribute on a class
        // so Roslyn parses it cleanly. We then locate the AttributeSyntax
        // and pass it to the extractor.
        string src = $@"
[XClass({attributeArgs})]
public class Foo {{ }}
";
        SyntaxTree tree = CSharpSyntaxTree.ParseText(src, path: Path);
        CompilationUnitSyntax root = (CompilationUnitSyntax)tree.GetRoot();
        ClassDeclarationSyntax cls = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        AttributeSyntax attr = cls.AttributeLists[0].Attributes[0];

        registry ??= new SpecifierRegistry(registerBuiltIns: true);
        List<DiagnosticRecord> diags = new();
        IReadOnlyList<Specifier> specs = CSharpSpecifierExtractor.Extract(
            attr, context, registry, Path, diags);
        return (specs, diags);
    }

    [Fact]
    public void EmptyArgs_ReturnsNoSpecifiers()
    {
        const string src = @"
[XClass]
public class Foo { }
";
        SyntaxTree tree = CSharpSyntaxTree.ParseText(src, path: Path);
        CompilationUnitSyntax root = (CompilationUnitSyntax)tree.GetRoot();
        AttributeSyntax attr = root.DescendantNodes().OfType<AttributeSyntax>().First();
        List<DiagnosticRecord> diags = new();
        IReadOnlyList<Specifier> specs = CSharpSpecifierExtractor.Extract(
            attr, SpecifierContext.Class, new SpecifierRegistry(), Path, diags);
        Assert.Empty(specs);
        Assert.Empty(diags);
    }

    [Fact]
    public void SingleFlag_ParsesAsFlagSpecifier()
    {
        (IReadOnlyList<Specifier> specs, List<DiagnosticRecord> diags) = Extract(
            "Blueprintable", SpecifierContext.Class);
        Assert.Single(specs);
        Assert.Equal("Blueprintable", specs[0].Key);
        Assert.Empty(specs[0].Values);
        Assert.Empty(diags);
    }

    [Fact]
    public void TwoFlags_ParseAsTwoSpecifiers()
    {
        (IReadOnlyList<Specifier> specs, List<DiagnosticRecord> diags) = Extract(
            "Blueprintable, Abstract", SpecifierContext.Class);
        Assert.Equal(2, specs.Count);
        Assert.Contains(specs, s => s.Key == "Blueprintable");
        Assert.Contains(specs, s => s.Key == "Abstract");
        Assert.Empty(diags);
    }

    [Fact]
    public void NamedStringArg_ParsesAsSingleValueSpecifier()
    {
        (IReadOnlyList<Specifier> specs, List<DiagnosticRecord> diags) = Extract(
            "ClassGroup = \"Combat\"", SpecifierContext.Class);
        Assert.Single(specs);
        Assert.Equal("ClassGroup", specs[0].Key);
        Assert.Single(specs[0].Values);
        Assert.Equal("Combat", specs[0].Values[0]);
        Assert.Empty(diags);
    }

    [Fact]
    public void TypeOfArg_ExtractsTypeName()
    {
        (IReadOnlyList<Specifier> specs, List<DiagnosticRecord> diags) = Extract(
            "Within = typeof(Actor)", SpecifierContext.Class);
        Assert.Single(specs);
        Assert.Equal("Within", specs[0].Key);
        Assert.Single(specs[0].Values);
        Assert.Equal("Actor", specs[0].Values[0]);
        Assert.Empty(diags);
    }

    [Fact]
    public void IdentifierValue_ParsesAsValue()
    {
        // Use a key=identifier form: e.g. NoThrow = SomeIdentifier
        // (NoThrow is KeyEqValue on Function context).
        (IReadOnlyList<Specifier> specs, _) = Extract(
            "BlueprintCallable, NoThrow = TrueLiteral",
            SpecifierContext.Function);
        Specifier nt = specs.Single(s => s.Key == "NoThrow");
        Assert.Single(nt.Values);
        Assert.Equal("TrueLiteral", nt.Values[0]);
    }

    [Fact]
    public void MixedFlagAndNamed_ParsesBoth()
    {
        (IReadOnlyList<Specifier> specs, _) = Extract(
            "Blueprintable, ClassGroup = \"Combat\"", SpecifierContext.Class);
        Assert.Equal(2, specs.Count);
        Specifier flag = specs.Single(s => s.Key == "Blueprintable");
        Assert.Empty(flag.Values);
        Specifier kv = specs.Single(s => s.Key == "ClassGroup");
        Assert.Equal("Combat", kv.Values[0]);
    }

    [Fact]
    public void UnknownSpecifier_EmitsXHT110()
    {
        (IReadOnlyList<Specifier> specs, List<DiagnosticRecord> diags) = Extract(
            "DefinitelyNotARealSpecifier", SpecifierContext.Class);
        // Still emitted as a specifier so AST shape stays stable.
        Assert.Single(specs);
        Assert.Contains(diags, d => d.Code == CSharpSpecifierExtractor.DiagUnknownSpecifier);
    }

    [Fact]
    public void RegisteredSpecifierInWrongContext_EmitsXHT111()
    {
        // 'EditAnywhere' is PropertyMember-only. Use it in Function context.
        (IReadOnlyList<Specifier> specs, List<DiagnosticRecord> diags) = Extract(
            "EditAnywhere", SpecifierContext.Function);
        Assert.Single(specs);
        Assert.Contains(diags, d => d.Code == CSharpSpecifierExtractor.DiagSpecifierIllegalInContext);
    }

    [Fact]
    public void MalformedExpression_EmitsSyntaxError_OtherSpecifiersStillExtracted()
    {
        // 'x' + 1 is an unsupported argument form.
        (IReadOnlyList<Specifier> specs, List<DiagnosticRecord> diags) = Extract(
            "Blueprintable, ClassGroup = \"x\" + 1", SpecifierContext.Class);
        // 'Blueprintable' still parses; 'ClassGroup' fails grammar.
        Assert.Single(specs);
        Assert.Equal("Blueprintable", specs[0].Key);
        Assert.Contains(diags, d => d.Code == CSharpSpecifierExtractor.DiagSpecifierSyntaxError);
    }
}
