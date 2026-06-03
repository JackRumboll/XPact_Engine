// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Analysis;

/// <summary>
/// Tests for <see cref="AnalyzerHelpers"/>: XObject-derivation detection via
/// the metadata-name fallback (Phase 6.b tests bind against
/// Basic.Reference.Assemblies.Net80 with a locally-declared
/// <c>abstract class XObject</c> in namespace <c>XPact.CoreXObject</c>, NOT
/// the curated XObject BCL), and the deterministic member / type enumeration
/// helpers. Per /Documents/XIL2CPP.html Rev 4 Section 3.2.
/// </summary>
public sealed class AnalyzerHelpersTests
{
    // A locally-declared stand-in for the engine root reference type. The
    // metadata-name + namespace fallback in IsXObjectDerived must recognise
    // it even though the real curated XPact.CSharp.BCL XObject ref is absent.
    private const string XObjectStub =
        "namespace XPact.CoreXObject { public abstract class XObject { } }";

    private static (NormalizedUnit Unit, INamedTypeSymbol Symbol) TypeNamed(
        string typeName, params string[] sources)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(sources);
        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());

        foreach ((TypeDeclarationSyntax _, INamedTypeSymbol symbol) in
                 AnalyzerHelpers.EnumerateTypeDeclarations(unit))
        {
            if (symbol.Name == typeName)
            {
                return (unit, symbol);
            }
        }

        throw new System.InvalidOperationException($"Type '{typeName}' not found in the unit.");
    }

    [Fact]
    public void IsXObjectDerived_DirectlyDerivedType_ViaMetadataFallback_ReturnsTrue()
    {
        (NormalizedUnit _, INamedTypeSymbol foo) = TypeNamed(
            "Foo",
            XObjectStub,
            "namespace M { public class Foo : XPact.CoreXObject.XObject { } }");

        Assert.True(AnalyzerHelpers.IsXObjectDerived(foo));
    }

    [Fact]
    public void IsXObjectDerived_TransitivelyDerivedType_ReturnsTrue()
    {
        (NormalizedUnit _, INamedTypeSymbol bar) = TypeNamed(
            "Bar",
            XObjectStub,
            "namespace M { public class Foo : XPact.CoreXObject.XObject { } public class Bar : Foo { } }");

        Assert.True(AnalyzerHelpers.IsXObjectDerived(bar));
    }

    [Fact]
    public void IsXObjectDerived_PlainType_ReturnsFalse()
    {
        (NormalizedUnit _, INamedTypeSymbol plain) = TypeNamed(
            "Plain",
            XObjectStub,
            "namespace M { public class Plain { } }");

        Assert.False(AnalyzerHelpers.IsXObjectDerived(plain));
    }

    [Fact]
    public void IsXObjectDerived_XObjectItself_ReturnsFalse()
    {
        (NormalizedUnit _, INamedTypeSymbol xobject) = TypeNamed("XObject", XObjectStub);

        // XObject is not derived FROM XObject.
        Assert.False(AnalyzerHelpers.IsXObjectDerived(xobject));
    }

    [Fact]
    public void IsXObjectDerived_SameNameDifferentNamespace_ReturnsFalse()
    {
        // A class XObject in a DIFFERENT namespace must not match.
        (NormalizedUnit _, INamedTypeSymbol decoy) = TypeNamed(
            "Decoy",
            "namespace Other { public abstract class XObject { } }",
            "namespace M { public class Decoy : Other.XObject { } }");

        Assert.False(AnalyzerHelpers.IsXObjectDerived(decoy));
    }

    [Fact]
    public void IsXObjectType_RecognisesXObject_ButNotDerived()
    {
        (NormalizedUnit _, INamedTypeSymbol xobject) = TypeNamed("XObject", XObjectStub);
        Assert.True(AnalyzerHelpers.IsXObjectType(xobject));

        (NormalizedUnit _, INamedTypeSymbol foo) = TypeNamed(
            "Foo",
            XObjectStub,
            "namespace M { public class Foo : XPact.CoreXObject.XObject { } }");
        Assert.False(AnalyzerHelpers.IsXObjectType(foo));
    }

    [Fact]
    public void IsXObjectDerived_Null_ReturnsFalse()
    {
        Assert.False(AnalyzerHelpers.IsXObjectDerived(null));
        Assert.False(AnalyzerHelpers.IsXObjectType(null));
    }

    [Fact]
    public void EnumerateMemberDeclarations_IsDeterministic_AcrossTreesInOrder()
    {
        NormalizedUnit unit = Pass2Driver.Run(
            NormalizationTestHelpers.BuildPass1(
                "namespace M; public class A { public int X; public void M1() { } }",
                "namespace M; public class B { public int Y; }"),
            new List<INormalizer>());

        List<string> first = AnalyzerHelpers.EnumerateMemberDeclarations(unit)
            .Select(DescribeMember).ToList();
        List<string> second = AnalyzerHelpers.EnumerateMemberDeclarations(unit)
            .Select(DescribeMember).ToList();

        Assert.Equal(first, second);

        // Type A (tree 0) appears before type B (tree 1).
        int indexA = first.FindIndex(s => s.Contains("class A"));
        int indexB = first.FindIndex(s => s.Contains("class B"));
        Assert.True(indexA >= 0 && indexB >= 0);
        Assert.True(indexA < indexB);
    }

    [Fact]
    public void EnumerateTypeDeclarations_PairsDeclarationsWithSymbols()
    {
        NormalizedUnit unit = Pass2Driver.Run(
            NormalizationTestHelpers.BuildPass1(
                "namespace M; public class A { } public class B { }"),
            new List<INormalizer>());

        List<string> names = AnalyzerHelpers.EnumerateTypeDeclarations(unit)
            .Select(t => t.Symbol.Name)
            .ToList();

        Assert.Contains("A", names);
        Assert.Contains("B", names);
        // Declaration order within a tree: A before B.
        Assert.True(names.IndexOf("A") < names.IndexOf("B"));
    }

    private static string DescribeMember(MemberDeclarationSyntax m) => m switch
    {
        ClassDeclarationSyntax c => "class " + c.Identifier.ValueText,
        MethodDeclarationSyntax mm => "method " + mm.Identifier.ValueText,
        FieldDeclarationSyntax f => "field " + f.Declaration.Variables.First().Identifier.ValueText,
        _ => m.Kind().ToString(),
    };
}
