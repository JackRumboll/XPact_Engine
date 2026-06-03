// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Frontend;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;

/// <summary>
/// Tests for <see cref="ConditionalAttributeElisionNormalizer"/> (WU-13) per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.4: a call to a
/// <c>[System.Diagnostics.Conditional("SYM")]</c> method is KEPT iff at least
/// one of its conditional symbols is defined in the compilation, otherwise it
/// is ELIDED. The normalizer is run in isolation through the explicit-set
/// <see cref="Pass2Driver.Run(Pass1Result, IReadOnlyList{INormalizer})"/>
/// overload so the assertions are independent of any other normalizer.
/// </summary>
public sealed class ConditionalAttributeElisionNormalizerTests
{
    /// <summary>
    /// Fixture 1: the conditional symbol is present in the compilation -- the
    /// call is KEPT.
    /// </summary>
    [Fact]
    public void ConditionalSymbolPresent_KeepsCall()
    {
        const string source = """
            namespace M;
            public class A
            {
                [System.Diagnostics.Conditional("XPACT_TRACE")]
                public static void Trace(string msg) { }

                public void Run() { Trace("hi"); }
            }
            """;

        Pass1Result pass1 = BuildPass1(new[] { "XPACT_TRACE" }, source);
        NormalizedUnit unit = Pass2Driver.Run(
            pass1, new INormalizer[] { new ConditionalAttributeElisionNormalizer() });

        InvocationExpressionSyntax call = TraceCall(pass1);
        ConditionalElisionAnnotation? annotation =
            unit.GetAnnotation<ConditionalElisionAnnotation>(call);

        Assert.NotNull(annotation);
        Assert.True(annotation!.Keep);
        Assert.Equal(new[] { "XPACT_TRACE" }, annotation.ConditionalSymbols);
        Assert.Equal("conditional-elision", annotation.Kind);
    }

    /// <summary>
    /// Fixture 2: the conditional symbol is absent from the compilation -- the
    /// call is ELIDED.
    /// </summary>
    [Fact]
    public void ConditionalSymbolAbsent_ElidesCall()
    {
        const string source = """
            namespace M;
            public class A
            {
                [System.Diagnostics.Conditional("XPACT_TRACE")]
                public static void Trace(string msg) { }

                public void Run() { Trace("hi"); }
            }
            """;

        // No preprocessor symbols defined.
        Pass1Result pass1 = BuildPass1(System.Array.Empty<string>(), source);
        NormalizedUnit unit = Pass2Driver.Run(
            pass1, new INormalizer[] { new ConditionalAttributeElisionNormalizer() });

        InvocationExpressionSyntax call = TraceCall(pass1);
        ConditionalElisionAnnotation? annotation =
            unit.GetAnnotation<ConditionalElisionAnnotation>(call);

        Assert.NotNull(annotation);
        Assert.False(annotation!.Keep);
        Assert.Equal(new[] { "XPACT_TRACE" }, annotation.ConditionalSymbols);
    }

    /// <summary>
    /// Fixture 3: a method carrying multiple <c>[Conditional]</c> attributes is
    /// kept iff ANY of its symbols is defined; the annotation records the full
    /// (ordinal-sorted, de-duplicated) symbol set.
    /// </summary>
    [Fact]
    public void MultipleConditionalSymbols_KeptWhenAnyDefined_RecordsAllSorted()
    {
        const string source = """
            namespace M;
            public class A
            {
                [System.Diagnostics.Conditional("XPACT_VERBOSE")]
                [System.Diagnostics.Conditional("XPACT_TRACE")]
                public static void Trace(string msg) { }

                public void Run() { Trace("hi"); }
            }
            """;

        // Only the second-declared symbol (XPACT_TRACE) is defined; the OR
        // rule keeps the call. The recorded set is ordinal-sorted regardless
        // of declaration / define order.
        Pass1Result pass1 = BuildPass1(new[] { "XPACT_TRACE" }, source);
        NormalizedUnit unit = Pass2Driver.Run(
            pass1, new INormalizer[] { new ConditionalAttributeElisionNormalizer() });

        InvocationExpressionSyntax call = TraceCall(pass1);
        ConditionalElisionAnnotation? annotation =
            unit.GetAnnotation<ConditionalElisionAnnotation>(call);

        Assert.NotNull(annotation);
        Assert.True(annotation!.Keep);
        Assert.Equal(new[] { "XPACT_TRACE", "XPACT_VERBOSE" }, annotation.ConditionalSymbols);
    }

    /// <summary>
    /// A call to an unconditional (non-<c>[Conditional]</c>) method is never
    /// annotated: the normalizer only records the keep/elide lowering for
    /// conditional methods.
    /// </summary>
    [Fact]
    public void NonConditionalCall_IsNotAnnotated()
    {
        const string source = """
            namespace M;
            public class A
            {
                public static void Plain(string msg) { }

                public void Run() { Plain("hi"); }
            }
            """;

        Pass1Result pass1 = BuildPass1(new[] { "XPACT_TRACE" }, source);
        NormalizedUnit unit = Pass2Driver.Run(
            pass1, new INormalizer[] { new ConditionalAttributeElisionNormalizer() });

        InvocationExpressionSyntax call = pass1.ParsedFiles[0].Tree.GetRoot()
            .DescendantNodes().OfType<InvocationExpressionSyntax>().Single();

        Assert.Null(unit.GetAnnotation<ConditionalElisionAnnotation>(call));
        Assert.Empty(unit.GetAnnotations(call));
    }

    // ---------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------

    /// <summary>
    /// Build a single-file, non-sim-path Pass-1 result over
    /// <paramref name="source"/>, parsed with the supplied preprocessor
    /// symbols defined. Mirrors <see cref="NormalizationTestHelpers"/> but
    /// threads the conditional symbol set through the parse options (the
    /// authoritative "is SYM defined" surface the normalizer reads).
    /// </summary>
    private static Pass1Result BuildPass1(IEnumerable<string> preprocessorSymbols, string source)
    {
        CSharpParseOptions parseOptions = ParseOptionsFactory.Create(preprocessorSymbols);

        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(source);
        ModuleParser.ParsedFile parsed = ModuleParser.ParseBytes("Source0.cs", bytes, parseOptions);

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "TestModule",
            syntaxTrees: new[] { parsed.Tree },
            references: FrontendTestHelpers.BclReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return new Pass1Result(
            "TestModule",
            new List<ModuleParser.ParsedFile> { parsed },
            compilation,
            new List<DiagnosticRecord>());
    }

    /// <summary>The single invocation of the <c>Trace(...)</c> method in the fixture.</summary>
    private static InvocationExpressionSyntax TraceCall(Pass1Result pass1)
        => pass1.ParsedFiles[0].Tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression is IdentifierNameSyntax { Identifier.ValueText: "Trace" });
}
