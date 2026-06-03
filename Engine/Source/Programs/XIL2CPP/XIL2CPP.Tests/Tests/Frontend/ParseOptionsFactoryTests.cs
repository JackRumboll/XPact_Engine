// Copyright Simgenics. All Rights Reserved.

using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Simgenics.XPact.XIL2CPP.Frontend;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Frontend;

/// <summary>
/// Tests for <see cref="ParseOptionsFactory"/> -- the single source of
/// truth for deterministic <see cref="CSharpParseOptions"/> per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2 + 9.9.
/// </summary>
public sealed class ParseOptionsFactoryTests
{
    [Fact]
    public void Create_PinsCSharp12()
    {
        CSharpParseOptions options = ParseOptionsFactory.Create();
        Assert.Equal(LanguageVersion.CSharp12, options.LanguageVersion);
    }

    [Fact]
    public void Create_UsesDocumentationModeParse()
    {
        CSharpParseOptions options = ParseOptionsFactory.Create();
        Assert.Equal(DocumentationMode.Parse, options.DocumentationMode);
    }

    [Fact]
    public void Create_UsesRegularSourceKind()
    {
        CSharpParseOptions options = ParseOptionsFactory.Create();
        Assert.Equal(SourceCodeKind.Regular, options.Kind);
    }

    [Fact]
    public void Create_ClearsAmbientFeatures()
    {
        CSharpParseOptions options = ParseOptionsFactory.Create();
        Assert.Empty(options.Features);
    }

    [Fact]
    public void Create_NoSymbols_HasEmptyPreprocessorSet()
    {
        CSharpParseOptions options = ParseOptionsFactory.Create();
        Assert.Empty(options.PreprocessorSymbolNames);
    }

    [Fact]
    public void Create_WithSymbols_AppliesThem()
    {
        CSharpParseOptions options = ParseOptionsFactory.Create(new[] { "DEBUG", "TRACE" });
        Assert.Contains("DEBUG", options.PreprocessorSymbolNames);
        Assert.Contains("TRACE", options.PreprocessorSymbolNames);
    }

    [Fact]
    public void Create_SymbolOrderIsCanonical_RegardlessOfInputOrder()
    {
        // Determinism: the parse options must be identical regardless of the
        // manifest's define list order. The factory ordinal-sorts the set so
        // the resulting symbol sequence is stable.
        CSharpParseOptions a = ParseOptionsFactory.Create(new[] { "TRACE", "DEBUG", "FOO" });
        CSharpParseOptions b = ParseOptionsFactory.Create(new[] { "FOO", "DEBUG", "TRACE" });

        Assert.Equal(
            a.PreprocessorSymbolNames.ToArray(),
            b.PreprocessorSymbolNames.ToArray());
    }

    [Fact]
    public void Create_DeduplicatesAndSkipsBlankSymbols()
    {
        CSharpParseOptions options = ParseOptionsFactory.Create(
            new[] { "DEBUG", "DEBUG", "  ", "" });
        Assert.Single(options.PreprocessorSymbolNames);
        Assert.Contains("DEBUG", options.PreprocessorSymbolNames);
    }

    [Fact]
    public void Create_NullSymbols_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ParseOptionsFactory.Create(null!));
    }

    [Fact]
    public void Create_ActiveDefinesGateTheParseTree()
    {
        // The defined symbol's #if branch is parsed as a real declaration;
        // the inactive branch becomes disabled trivia (no ClassDeclaration
        // node). This proves the preprocessor symbols are load-bearing.
        const string source = """
            #if FEATURE_X
            public class Present { }
            #else
            public class Absent { }
            #endif
            """;

        CSharpParseOptions withSymbol = ParseOptionsFactory.Create(new[] { "FEATURE_X" });
        CSharpParseOptions withoutSymbol = ParseOptionsFactory.Create();

        SyntaxTree treeWith = CSharpSyntaxTree.ParseText(source, withSymbol);
        SyntaxTree treeWithout = CSharpSyntaxTree.ParseText(source, withoutSymbol);

        string[] withNames = treeWith.GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
            .Select(c => c.Identifier.Text)
            .ToArray();
        string[] withoutNames = treeWithout.GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
            .Select(c => c.Identifier.Text)
            .ToArray();

        Assert.Equal(new[] { "Present" }, withNames);
        Assert.Equal(new[] { "Absent" }, withoutNames);
    }
}
