// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Simgenics.XPact.XIL2CPP.Frontend;
using Xunit;
using MetadataReferenceResolver = Simgenics.XPact.XIL2CPP.Frontend.MetadataReferenceResolver;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Frontend;

/// <summary>
/// A representative subset of the C# 12 syntax features XIL2CPP must parse +
/// bind cleanly per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 4.1. The
/// EXHAUSTIVE one-fixture-per-feature corpus is a separate later unit; this
/// suite covers a cross-section (records, primary constructors, pattern
/// matching, collection expressions, raw strings, switch expressions,
/// local functions, lambdas, generics, nullable, top-level statements off)
/// to prove the Pass-1 parse + bind path is sound.
/// </summary>
public sealed class Pass1FeatureCoverageTests
{
    private static CSharpCompilation Bind(string source)
    {
        ModuleParser.ParsedFile parsed = ModuleParser.ParseBytes(
            "/repo/Feature.cs",
            System.Text.Encoding.UTF8.GetBytes(source),
            ParseOptionsFactory.Create());

        Assert.Empty(parsed.SyntaxDiagnostics);

        ReferenceSet refs = MetadataReferenceResolver.BuildFromReferences(
            FrontendTestHelpers.BclReferences(),
            System.Array.Empty<string>(),
            _ => null);

        return CompilationBuilder.Build("FeatureModule", new[] { parsed.Tree }, refs);
    }

    private static void AssertBindsClean(string source)
    {
        CSharpCompilation compilation = Bind(source);
        Diagnostic[] errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(
            errors.Length == 0,
            "Expected clean bind but got: " + string.Join(" | ", errors.Select(e => e.ToString())));
    }

    [Theory]
    [MemberData(nameof(Features))]
    public void Feature_ParsesAndBindsClean(string featureName, string source)
    {
        _ = featureName; // surfaced in the test display name for triage.
        AssertBindsClean(source);
    }

    public static IEnumerable<object[]> Features()
    {
        yield return new object[]
        {
            "record class (positional)",
            "namespace N; public record Point(int X, int Y);",
        };
        yield return new object[]
        {
            "record struct",
            "namespace N; public record struct Vec(double X, double Y);",
        };
        yield return new object[]
        {
            "primary constructor on class",
            "namespace N; public class Greeter(string name) { public string Hello() => \"hi \" + name; }",
        };
        yield return new object[]
        {
            "init-only + required members",
            "namespace N; public class Cfg { public required int Port { get; init; } }",
        };
        yield return new object[]
        {
            "pattern matching (property + relational + logical)",
            """
            namespace N;
            public class M
            {
                public string Classify(int n) => n switch
                {
                    < 0 => "neg",
                    0 => "zero",
                    > 0 and < 10 => "small",
                    _ => "big",
                };
            }
            """,
        };
        yield return new object[]
        {
            "list pattern",
            """
            namespace N;
            public class M
            {
                public bool Match(int[] xs) => xs is [1, 2, .. var rest] && rest.Length >= 0;
            }
            """,
        };
        yield return new object[]
        {
            "collection expression",
            "namespace N; using System.Collections.Generic; public class M { public List<int> L() => [1, 2, 3]; }",
        };
        yield return new object[]
        {
            "raw string literal",
            "namespace N; public class M { public string S() => \"\"\"\nraw\n\"\"\"; }",
        };
        yield return new object[]
        {
            "string interpolation",
            "namespace N; public class M { public string S(string n) => $\"hi {n}\"; }",
        };
        yield return new object[]
        {
            "local function (capturing)",
            """
            namespace N;
            public class M
            {
                public int Sum(int a, int b)
                {
                    int Add() => a + b;
                    return Add();
                }
            }
            """,
        };
        yield return new object[]
        {
            "lambda + Func",
            "namespace N; using System; public class M { public int Apply(Func<int,int> f, int x) => f(x); }",
        };
        yield return new object[]
        {
            "generic class + constraint",
            "namespace N; public class Box<T> where T : class { public T? Value { get; set; } }",
        };
        yield return new object[]
        {
            "nullable reference annotations",
            "namespace N; public class M { public string? Maybe(bool b) => b ? \"x\" : null; }",
        };
        yield return new object[]
        {
            "switch expression with tuple",
            """
            namespace N;
            public class M
            {
                public string Q(int a, int b) => (a, b) switch
                {
                    (0, 0) => "origin",
                    _ => "other",
                };
            }
            """,
        };
        yield return new object[]
        {
            "using declaration + IDisposable",
            """
            namespace N;
            using System;
            public class M
            {
                public void Run(IDisposable d)
                {
                    using var x = d;
                }
            }
            """,
        };
    }
}
