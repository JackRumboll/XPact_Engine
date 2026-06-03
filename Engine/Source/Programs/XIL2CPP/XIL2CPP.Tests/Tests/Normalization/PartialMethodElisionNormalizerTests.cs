// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Xunit;
using XilSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;

/// <summary>
/// Unit tests for <see cref="PartialMethodElisionNormalizer"/> (WU-12,
/// /Documents/XIL2CPP.html Rev 4 Section 5.1). Each test builds a Pass-1
/// result from a fixture source, runs ONLY this normalizer in isolation via
/// the explicit-set <see cref="Pass2Driver"/> overload, and asserts the
/// expected per-node <see cref="PartialMethodElisionAnnotation"/> and/or the
/// <c>XIL2CPP018</c> diagnostic.
/// </summary>
public sealed class PartialMethodElisionNormalizerTests
{
    // ---------------------------------------------------------------
    // Fixture 1: unbodied partial method, called with NO args -> the call
    // is elided (annotated), and there are no side-effecting arguments so
    // NO XIL2CPP018 is emitted.
    // ---------------------------------------------------------------

    [Fact]
    public void UnbodiedPartial_NoArgs_CallSiteElided_NoDiagnostic()
    {
        const string source = """
            namespace M;
            public partial class A
            {
                partial void Hook();

                public void Use()
                {
                    Hook();
                }
            }
            """;

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);
        NormalizedUnit unit = Pass2Driver.Run(
            pass1, new INormalizer[] { new PartialMethodElisionNormalizer() });

        InvocationExpressionSyntax call = HookCall(pass1);
        PartialMethodElisionAnnotation? annotation =
            unit.GetAnnotation<PartialMethodElisionAnnotation>(call);

        Assert.NotNull(annotation);
        Assert.Equal("partial-method-elision", annotation!.Kind);
        Assert.Equal("Hook", annotation.TargetDisplayName);
        Assert.False(annotation.HasSideEffectingArguments);

        // No side-effecting args -> no XIL2CPP018.
        Assert.Empty(unit.Diagnostics);
    }

    // ---------------------------------------------------------------
    // Fixture 2: unbodied partial method, called with a SIDE-EFFECTING
    // argument -> the call is elided (annotated) AND XIL2CPP018 is emitted
    // at the call site (a Warning) because the argument must still evaluate.
    // ---------------------------------------------------------------

    [Fact]
    public void UnbodiedPartial_SideEffectingArg_Elided_EmitsXil2Cpp018()
    {
        const string source = """
            namespace M;
            public partial class A
            {
                partial void Hook(int x);

                private int Compute() => 1;

                public void Use()
                {
                    Hook(Compute());
                }
            }
            """;

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);
        NormalizedUnit unit = Pass2Driver.Run(
            pass1, new INormalizer[] { new PartialMethodElisionNormalizer() });

        InvocationExpressionSyntax call = HookCall(pass1);
        PartialMethodElisionAnnotation? annotation =
            unit.GetAnnotation<PartialMethodElisionAnnotation>(call);

        Assert.NotNull(annotation);
        Assert.Equal("Hook", annotation!.TargetDisplayName);
        Assert.True(annotation.HasSideEffectingArguments);

        // Exactly one XIL2CPP018 warning, anchored at the call site.
        DiagnosticRecord diag = Assert.Single(unit.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnbodiedPartialMethodSideEffectingArgs, diag.Code);
        Assert.Equal(XilSeverity.Warning, diag.Severity);
        Assert.Contains("Hook", diag.Message);
        Assert.Equal("Source0.cs", diag.File);

        // The anchor is the 1-based start of the elided invocation.
        FileLinePositionSpan lineSpan = call.GetLocation().GetLineSpan();
        Assert.Equal(lineSpan.StartLinePosition.Line + 1, diag.Line);
        Assert.Equal(lineSpan.StartLinePosition.Character + 1, diag.Column);
    }

    // ---------------------------------------------------------------
    // Fixture 3: BODIED partial method (an implementing part exists) ->
    // the call is NOT elided (no annotation) and NO diagnostic, even though
    // the argument is side-effecting (a bodied partial emits normally).
    // ---------------------------------------------------------------

    [Fact]
    public void BodiedPartial_NotElided_NoAnnotation_NoDiagnostic()
    {
        const string source = """
            namespace M;
            public partial class A
            {
                partial void Hook(int x);

                partial void Hook(int x)
                {
                    System.Console.WriteLine(x);
                }

                private int Compute() => 1;

                public void Use()
                {
                    Hook(Compute());
                }
            }
            """;

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);
        NormalizedUnit unit = Pass2Driver.Run(
            pass1, new INormalizer[] { new PartialMethodElisionNormalizer() });

        InvocationExpressionSyntax call = HookCall(pass1);

        // Bodied partial: emits normally, so the call is left untouched.
        Assert.Null(unit.GetAnnotation<PartialMethodElisionAnnotation>(call));
        Assert.Empty(unit.GetAnnotations(call));
        Assert.Empty(unit.Diagnostics);
    }

    // ---------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------

    /// <summary>
    /// The <c>Hook(...)</c> invocation inside the <c>Use</c> method body of
    /// the fixture's first parsed file.
    /// </summary>
    private static InvocationExpressionSyntax HookCall(Pass1Result pass1)
        => pass1.ParsedFiles[0].Tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(i => i.Expression is IdentifierNameSyntax { Identifier.ValueText: "Hook" });
}
