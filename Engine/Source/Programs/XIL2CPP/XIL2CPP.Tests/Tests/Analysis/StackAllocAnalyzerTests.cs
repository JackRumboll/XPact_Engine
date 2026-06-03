// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Xunit;
using XilSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Analysis;

/// <summary>
/// Tests for <see cref="StackAllocAnalyzer"/> per /Documents/XIL2CPP.html
/// Rev 4 Section 5.13 (Q10 resolution; FIX-B-MEDIUM-35 runtime size check;
/// FIX-D-MED-05 static size check) and Section 12 (XIL2CPP081 / XIL2CPP086).
/// Builds a Pass-1 result from each fixture, runs it through the (empty)
/// Pass-2 driver, then drives JUST the StackAllocAnalyzer and asserts the
/// recorded <see cref="StackAllocSite"/>s + diagnostics.
/// </summary>
public sealed class StackAllocAnalyzerTests
{
    private static Pass3Result RunAnalyzer(bool isSimPath, params string[] sources)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(isSimPath, sources);
        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        return Pass3Driver.Run(unit, new ISemanticAnalyzer[] { new StackAllocAnalyzer() });
    }

    private static Pass3Result RunAnalyzer(params string[] sources)
        => RunAnalyzer(isSimPath: false, sources);

    // -----------------------------------------------------------------
    // (1) stackalloc int[16] -> Ok, no diagnostic.
    // -----------------------------------------------------------------

    [Fact]
    public void StackallocInt16_WithinBound_RecordsOkSite_NoDiagnostic()
    {
        Pass3Result result = RunAnalyzer(
            "using System;\n"
            + "namespace M;\n"
            + "public class A { public void F() { Span<int> b = stackalloc int[16]; _ = b.Length; } }");

        Assert.Empty(result.Diagnostics);
        Assert.False(result.HasErrors);

        StackAllocSite site = Assert.Single(result.GetAll<StackAllocSite>());
        Assert.Equal(StackAllocCategory.Ok, site.Category);
        Assert.Equal(16L, site.ConstantCount);
        Assert.Equal(4, site.ElementByteSize);
        Assert.Equal(64L, site.TotalByteSize);
        Assert.Contains("int", site.ElementType);
    }

    // -----------------------------------------------------------------
    // (2) stackalloc int[100000] -> XIL2CPP086 oversize.
    // -----------------------------------------------------------------

    [Fact]
    public void StackallocInt100000_Oversize_EmitsXil2Cpp086()
    {
        Pass3Result result = RunAnalyzer(
            "using System;\n"
            + "namespace M;\n"
            + "public class A { public void F() { Span<int> b = stackalloc int[100000]; _ = b.Length; } }");

        DiagnosticRecord diag = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.StackallocExceedsLimit, diag.Code);
        Assert.Equal(XilSeverity.Error, diag.Severity);
        Assert.True(result.HasErrors);
        Assert.Equal("TestModule", diag.Module);
        Assert.NotNull(diag.File);
        Assert.NotNull(diag.Line);

        StackAllocSite site = Assert.Single(result.GetAll<StackAllocSite>());
        Assert.Equal(StackAllocCategory.OversizeError, site.Category);
        Assert.Equal(100000L, site.ConstantCount);
        Assert.Equal(400000L, site.TotalByteSize);
    }

    [Fact]
    public void StackallocExactlyAtLimit_IsOk_NoDiagnostic()
    {
        // 64 KB / sizeof(int) = 16384 ints == exactly the limit (not over).
        Pass3Result result = RunAnalyzer(
            "using System;\n"
            + "namespace M;\n"
            + "public class A { public void F() { Span<int> b = stackalloc int[16384]; _ = b.Length; } }");

        Assert.Empty(result.Diagnostics);
        StackAllocSite site = Assert.Single(result.GetAll<StackAllocSite>());
        Assert.Equal(StackAllocCategory.Ok, site.Category);
        Assert.Equal(65536L, site.TotalByteSize);
    }

    [Fact]
    public void StackallocOneOverLimit_EmitsXil2Cpp086()
    {
        // 16385 ints == 65540 bytes, one int over the 64 KB limit.
        Pass3Result result = RunAnalyzer(
            "using System;\n"
            + "namespace M;\n"
            + "public class A { public void F() { Span<int> b = stackalloc int[16385]; _ = b.Length; } }");

        DiagnosticRecord diag = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.StackallocExceedsLimit, diag.Code);
        StackAllocSite site = Assert.Single(result.GetAll<StackAllocSite>());
        Assert.Equal(StackAllocCategory.OversizeError, site.Category);
        Assert.Equal(65540L, site.TotalByteSize);
    }

    // -----------------------------------------------------------------
    // (3) stackalloc of an invalid (managed) element type -> XIL2CPP081.
    // -----------------------------------------------------------------

    [Fact]
    public void StackallocManagedReferenceElement_EmitsXil2Cpp081()
    {
        // A reference-type element cannot be stack-allocated. Roslyn also
        // reports CS0208, but our analyzer reads the element type from syntax
        // and emits XIL2CPP081 regardless.
        Pass3Result result = RunAnalyzer(
            "using System;\n"
            + "namespace M;\n"
            + "public class Thing { }\n"
            + "public class A { public void F() { Span<Thing> p = stackalloc Thing[4]; _ = p.Length; } }");

        DiagnosticRecord diag = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.StackallocOverXObjectUnsupported, diag.Code);
        Assert.Equal(XilSeverity.Error, diag.Severity);
        Assert.True(result.HasErrors);

        StackAllocSite site = Assert.Single(result.GetAll<StackAllocSite>());
        Assert.Equal(StackAllocCategory.ManagedElementError, site.Category);
        Assert.Contains("Thing", site.ElementType);
    }

    [Fact]
    public void StackallocStructContainingReferenceElement_EmitsXil2Cpp081()
    {
        // A value type that transitively contains a managed reference is not
        // an unmanaged type -> XIL2CPP081.
        Pass3Result result = RunAnalyzer(
            "using System;\n"
            + "namespace M;\n"
            + "public struct Ref { public object O; }\n"
            + "public class A { public void F() { Span<Ref> p = stackalloc Ref[4]; _ = p.Length; } }");

        DiagnosticRecord diag = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.StackallocOverXObjectUnsupported, diag.Code);
        StackAllocSite site = Assert.Single(result.GetAll<StackAllocSite>());
        Assert.Equal(StackAllocCategory.ManagedElementError, site.Category);
    }

    [Fact]
    public void StackallocXObjectDerivedElement_EmitsXil2Cpp081()
    {
        // The engine's specific concern: stackalloc T[] where T is an XObject
        // reference. A locally-declared XObject stand-in stands in for the
        // engine BCL type (the test BCL set does not carry it).
        Pass3Result result = RunAnalyzer(
            "using System;\n"
            + "namespace XPact.CoreXObject { public abstract class XObject { } }\n"
            + "namespace M {\n"
            + "  public sealed class Actor : XPact.CoreXObject.XObject { }\n"
            + "  public class A { public void F() { Span<Actor> p = stackalloc Actor[2]; _ = p.Length; } }\n"
            + "}");

        DiagnosticRecord diag = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.StackallocOverXObjectUnsupported, diag.Code);
        StackAllocSite site = Assert.Single(result.GetAll<StackAllocSite>());
        Assert.Equal(StackAllocCategory.ManagedElementError, site.Category);
        Assert.Contains("Actor", site.ElementType);
    }

    // -----------------------------------------------------------------
    // (4) Dynamically-sized stackalloc -> recorded, runtime-check, no diag.
    // -----------------------------------------------------------------

    [Fact]
    public void StackallocDynamicSize_RecordsRuntimeCheckedSite_NoDiagnostic()
    {
        Pass3Result result = RunAnalyzer(
            "using System;\n"
            + "namespace M;\n"
            + "public class A { public void F(int n) { Span<int> b = stackalloc int[n]; _ = b.Length; } }");

        Assert.Empty(result.Diagnostics);
        Assert.False(result.HasErrors);

        StackAllocSite site = Assert.Single(result.GetAll<StackAllocSite>());
        Assert.Equal(StackAllocCategory.RuntimeChecked, site.Category);
        Assert.Null(site.ConstantCount);
        Assert.Null(site.TotalByteSize);
        Assert.Equal(4, site.ElementByteSize);
    }

    [Fact]
    public void StackallocConstantExpressionCount_FoldsToConstant_Ok()
    {
        // A const-foldable size expression is a compile-time constant; it is
        // checked statically, not deferred to a runtime guard.
        Pass3Result result = RunAnalyzer(
            "using System;\n"
            + "namespace M;\n"
            + "public class A { const int N = 8; public void F() { Span<int> b = stackalloc int[N * 2]; _ = b.Length; } }");

        Assert.Empty(result.Diagnostics);
        StackAllocSite site = Assert.Single(result.GetAll<StackAllocSite>());
        Assert.Equal(StackAllocCategory.Ok, site.Category);
        Assert.Equal(16L, site.ConstantCount);
        Assert.Equal(64L, site.TotalByteSize);
    }

    // -----------------------------------------------------------------
    // Element-type / form coverage.
    // -----------------------------------------------------------------

    [Fact]
    public void StackallocByteOversize_UsesElementSizeOne()
    {
        // 70000 bytes > 64 KB even at 1 byte/element.
        Pass3Result result = RunAnalyzer(
            "using System;\n"
            + "namespace M;\n"
            + "public class A { public void F() { Span<byte> b = stackalloc byte[70000]; _ = b.Length; } }");

        DiagnosticRecord diag = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.StackallocExceedsLimit, diag.Code);
        StackAllocSite site = Assert.Single(result.GetAll<StackAllocSite>());
        Assert.Equal(1, site.ElementByteSize);
        Assert.Equal(70000L, site.TotalByteSize);
    }

    [Fact]
    public void StackallocImplicitInitializerForm_RecordsRuntimeChecked_NoDiagnostic()
    {
        // stackalloc[] { ... } (implicit element type, initializer-sized).
        Pass3Result result = RunAnalyzer(
            "using System;\n"
            + "namespace M;\n"
            + "public class A { public void F() { Span<int> b = stackalloc[] { 1, 2, 3 }; _ = b.Length; } }");

        Assert.Empty(result.Diagnostics);
        StackAllocSite site = Assert.Single(result.GetAll<StackAllocSite>());
        Assert.Equal(StackAllocCategory.RuntimeChecked, site.Category);
        Assert.Null(site.ConstantCount);
        Assert.Contains("int", site.ElementType);
    }

    [Fact]
    public void StackallocExplicitInitializerForm_RecordsRuntimeChecked_NoDiagnostic()
    {
        // stackalloc int[] { ... } (omitted size; initializer-sized).
        Pass3Result result = RunAnalyzer(
            "using System;\n"
            + "namespace M;\n"
            + "public class A { public void F() { Span<int> b = stackalloc int[] { 1, 2, 3, 4 }; _ = b.Length; } }");

        Assert.Empty(result.Diagnostics);
        StackAllocSite site = Assert.Single(result.GetAll<StackAllocSite>());
        Assert.Equal(StackAllocCategory.RuntimeChecked, site.Category);
        Assert.Null(site.ConstantCount);
    }

    [Fact]
    public void NoStackalloc_RecordsNothing()
    {
        Pass3Result result = RunAnalyzer(
            "namespace M; public class A { public int F() => 1; }");

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.GetAll<StackAllocSite>());
    }

    [Fact]
    public void MultipleStackallocs_RecordedInDocumentOrder()
    {
        Pass3Result result = RunAnalyzer(
            "using System;\n"
            + "namespace M;\n"
            + "public class A { public void F(int n) {\n"
            + "  Span<int> a = stackalloc int[8];\n"
            + "  Span<int> b = stackalloc int[n];\n"
            + "  Span<int> c = stackalloc int[100000];\n"
            + "} }");

        IReadOnlyList<StackAllocSite> sites = result.GetAll<StackAllocSite>();
        Assert.Equal(3, sites.Count);
        Assert.Equal(StackAllocCategory.Ok, sites[0].Category);
        Assert.Equal(StackAllocCategory.RuntimeChecked, sites[1].Category);
        Assert.Equal(StackAllocCategory.OversizeError, sites[2].Category);

        // Sites are in ascending document (line) order.
        Assert.True(sites[0].Span.StartLine < sites[1].Span.StartLine);
        Assert.True(sites[1].Span.StartLine < sites[2].Span.StartLine);

        // Only the oversize site produced a diagnostic.
        DiagnosticRecord diag = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.StackallocExceedsLimit, diag.Code);
    }

    [Fact]
    public void Determinism_TwoRunsProduceIdenticalSitesAndDiagnostics()
    {
        const string source =
            "using System;\n"
            + "namespace M;\n"
            + "public class A { public void F(int n) {\n"
            + "  Span<int> a = stackalloc int[16];\n"
            + "  Span<int> b = stackalloc int[n];\n"
            + "  Span<int> c = stackalloc int[100000];\n"
            + "} }";

        Pass3Result first = RunAnalyzer(source);
        Pass3Result second = RunAnalyzer(source);

        List<StackAllocCategory> firstCats = first.GetAll<StackAllocSite>().Select(s => s.Category).ToList();
        List<StackAllocCategory> secondCats = second.GetAll<StackAllocSite>().Select(s => s.Category).ToList();
        Assert.Equal(firstCats, secondCats);

        List<string> firstCodes = first.Diagnostics.Select(d => d.Code).ToList();
        List<string> secondCodes = second.Diagnostics.Select(d => d.Code).ToList();
        Assert.Equal(firstCodes, secondCodes);
    }
}
