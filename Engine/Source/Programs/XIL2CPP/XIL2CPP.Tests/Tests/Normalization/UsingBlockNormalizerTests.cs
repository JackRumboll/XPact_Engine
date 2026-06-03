// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;

/// <summary>
/// Tests for <see cref="UsingBlockNormalizer"/> (Pass-2 WU-4): lowering
/// <c>using</c>-blocks and <c>using</c>-declarations to the canonical
/// try/finally-with-<c>Dispose</c> (XScopedGuard) form per
/// /Documents/XIL2CPP.html Rev 4 Section 3.2 + 5.15. Each fixture builds a
/// Pass-1 result from synthetic source, runs JUST this normalizer in
/// isolation through <see cref="Pass2Driver.Run(Pass1Result, IReadOnlyList{INormalizer})"/>,
/// then asserts the recorded <see cref="UsingLoweringAnnotation"/>.
/// </summary>
public sealed class UsingBlockNormalizerTests
{
    // -------------------------------------------------------------------
    // Fixture 1: using-statement with declaration.
    //   using (var x = e) { body }
    // -------------------------------------------------------------------

    [Fact]
    public void UsingStatement_WithDeclaration_AnnotatesAsDeclarationForm()
    {
        const string source = """
            namespace N;
            using System;
            public class M
            {
                public void Run(IDisposable d)
                {
                    using (var x = d) { }
                }
            }
            """;

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);
        NormalizedUnit unit = RunNormalizer(pass1);

        UsingStatementSyntax usingStatement = FirstUsingStatement(pass1);
        UsingLoweringAnnotation? annotation = unit.GetAnnotation<UsingLoweringAnnotation>(usingStatement);

        Assert.NotNull(annotation);
        Assert.Equal("using-block", annotation!.Kind);
        Assert.True(annotation.IsDeclarationForm);

        UsingResource resource = Assert.Single(annotation.Resources);
        Assert.Equal("x", resource.VariableName);
        Assert.NotNull(resource.ResourceExpression);
        Assert.Equal("d", resource.ResourceExpression!.ToString());
        Assert.Equal("global::System.IDisposable", resource.DisposalTargetType);
        Assert.False(resource.IsXObjectDerived);
    }

    // -------------------------------------------------------------------
    // Fixture 2: using-declaration (using var x = ...;).
    // -------------------------------------------------------------------

    [Fact]
    public void UsingDeclaration_UsingVar_AnnotatesLocalDeclaration()
    {
        const string source = """
            namespace N;
            using System;
            public class M
            {
                public void Run(IDisposable d)
                {
                    using var x = d;
                }
            }
            """;

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);
        NormalizedUnit unit = RunNormalizer(pass1);

        LocalDeclarationStatementSyntax usingDeclaration = FirstUsingDeclaration(pass1);
        UsingLoweringAnnotation? annotation =
            unit.GetAnnotation<UsingLoweringAnnotation>(usingDeclaration);

        Assert.NotNull(annotation);
        Assert.Equal("using-block", annotation!.Kind);
        Assert.True(annotation.IsDeclarationForm);

        UsingResource resource = Assert.Single(annotation.Resources);
        Assert.Equal("x", resource.VariableName);
        Assert.Equal("d", resource.ResourceExpression!.ToString());
        Assert.Equal("global::System.IDisposable", resource.DisposalTargetType);
        Assert.False(resource.IsXObjectDerived);

        // A plain using statement must NOT be annotated for this fixture (none exists),
        // and the using-declaration is the ONLY annotated node.
        Assert.Empty(pass1.ParsedFiles[0].Tree.GetRoot()
            .DescendantNodes().OfType<UsingStatementSyntax>());
    }

    // -------------------------------------------------------------------
    // Fixture 3: using over an XObject-typed resource.
    //   The disposal target derives from the engine XObject -> Dispose()
    //   maps to MarkForKill() per Section 5.15.
    // -------------------------------------------------------------------

    [Fact]
    public void UsingStatement_OverXObjectResource_RecordsXObjectDisposalTarget()
    {
        // Local XObject stand-in in the engine's XPact.CoreXObject namespace so
        // the metadata-name + namespace fallback resolves it as XObject-derived
        // even without the real BCL ref (mirrors the Frontend corpus pattern).
        const string source = """
            namespace XPact.CoreXObject
            {
                using System;
                public abstract class XObject : IDisposable { public void Dispose() { } }
            }
            namespace N
            {
                using XPact.CoreXObject;
                public sealed class Handle : XObject { }
                public class M
                {
                    public void Run(Handle h)
                    {
                        using (Handle x = h) { }
                    }
                }
            }
            """;

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);
        NormalizedUnit unit = RunNormalizer(pass1);

        UsingStatementSyntax usingStatement = FirstUsingStatement(pass1);
        UsingLoweringAnnotation? annotation = unit.GetAnnotation<UsingLoweringAnnotation>(usingStatement);

        Assert.NotNull(annotation);
        Assert.True(annotation!.IsDeclarationForm);

        UsingResource resource = Assert.Single(annotation.Resources);
        Assert.Equal("x", resource.VariableName);
        Assert.Equal("global::N.Handle", resource.DisposalTargetType);
        Assert.True(resource.IsXObjectDerived);
    }

    // -------------------------------------------------------------------
    // Fixture 4: nested using.
    //   using (var outer = a) { using (var inner = b) { } }
    //   Both nesting levels are annotated; the inner using is inside the
    //   outer's body.
    // -------------------------------------------------------------------

    [Fact]
    public void NestedUsing_AnnotatesBothLevels()
    {
        const string source = """
            namespace N;
            using System;
            public class M
            {
                public void Run(IDisposable a, IDisposable b)
                {
                    using (var outer = a)
                    {
                        using (var inner = b) { }
                    }
                }
            }
            """;

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);
        NormalizedUnit unit = RunNormalizer(pass1);

        List<UsingStatementSyntax> usings = pass1.ParsedFiles[0].Tree.GetRoot()
            .DescendantNodes().OfType<UsingStatementSyntax>().ToList();
        Assert.Equal(2, usings.Count);

        // Document order: outer first (it lexically encloses inner).
        UsingStatementSyntax outer = usings[0];
        UsingStatementSyntax inner = usings[1];
        Assert.True(inner.Span.Start > outer.Span.Start);
        Assert.True(outer.Span.End >= inner.Span.End);

        UsingLoweringAnnotation? outerAnnotation = unit.GetAnnotation<UsingLoweringAnnotation>(outer);
        UsingLoweringAnnotation? innerAnnotation = unit.GetAnnotation<UsingLoweringAnnotation>(inner);

        Assert.NotNull(outerAnnotation);
        Assert.NotNull(innerAnnotation);

        Assert.Equal("outer", Assert.Single(outerAnnotation!.Resources).VariableName);
        Assert.Equal("inner", Assert.Single(innerAnnotation!.Resources).VariableName);

        Assert.Equal(
            "global::System.IDisposable",
            Assert.Single(outerAnnotation.Resources).DisposalTargetType);
        Assert.Equal(
            "global::System.IDisposable",
            Assert.Single(innerAnnotation.Resources).DisposalTargetType);
    }

    // -------------------------------------------------------------------
    // Resource-acquisition form coverage: using (e) { } declares no
    // variable, so it is NOT the declaration form.
    // -------------------------------------------------------------------

    [Fact]
    public void UsingStatement_ResourceAcquisitionForm_IsNotDeclarationForm()
    {
        const string source = """
            namespace N;
            using System;
            public class M
            {
                public void Run(IDisposable d)
                {
                    using (d) { }
                }
            }
            """;

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);
        NormalizedUnit unit = RunNormalizer(pass1);

        UsingStatementSyntax usingStatement = FirstUsingStatement(pass1);
        UsingLoweringAnnotation? annotation = unit.GetAnnotation<UsingLoweringAnnotation>(usingStatement);

        Assert.NotNull(annotation);
        Assert.False(annotation!.IsDeclarationForm);

        UsingResource resource = Assert.Single(annotation.Resources);
        Assert.Null(resource.VariableName);
        Assert.Equal("d", resource.ResourceExpression!.ToString());
        Assert.Equal("global::System.IDisposable", resource.DisposalTargetType);
    }

    // -------------------------------------------------------------------
    // Helpers.
    // -------------------------------------------------------------------

    private static NormalizedUnit RunNormalizer(Pass1Result pass1)
        => Pass2Driver.Run(pass1, new INormalizer[] { new UsingBlockNormalizer() });

    private static UsingStatementSyntax FirstUsingStatement(Pass1Result pass1)
        => pass1.ParsedFiles[0].Tree.GetRoot()
            .DescendantNodes().OfType<UsingStatementSyntax>().First();

    private static LocalDeclarationStatementSyntax FirstUsingDeclaration(Pass1Result pass1)
        => pass1.ParsedFiles[0].Tree.GetRoot()
            .DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
            .First(local => !local.UsingKeyword.IsKind(SyntaxKind.None));
}
