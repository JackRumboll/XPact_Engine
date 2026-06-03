// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Analysis;

/// <summary>
/// Tests for <see cref="GenericInstantiationAnalyzer"/> (WU-24, Section 5.8):
/// the closed-instantiation walk records the reached
/// <see cref="ClosedInstantiation"/> entries into a
/// <see cref="GenericClosureResult"/> singleton, bounds recursion at depth 16
/// (<c>XIL2CPP123</c>), cycle-detects self-referential closures
/// (<c>XIL2CPP124</c>), and rejects an XObject-derived argument bound to a
/// <c>where T : unmanaged</c> type parameter (<c>XIL2CPP125</c>).
/// </summary>
/// <remarks>
/// Fixtures bind against the deterministic .NET 8 test BCL with a
/// locally-declared <c>abstract class XObject</c> stand-in (the
/// metadata-name + namespace fallback in
/// <see cref="AnalyzerHelpers.IsXObjectDerived"/> recognises it without the
/// curated engine BCL ref).
/// </remarks>
public sealed class GenericInstantiationAnalyzerTests
{
    private const string XObjectStub =
        "namespace XPact.CoreXObject { public abstract class XObject { } }";

    /// <summary>
    /// Build a NormalizedUnit (Pass-2 with no normalizers) over the supplied
    /// sources, mirroring the Pass3DriverTests harness.
    /// </summary>
    private static NormalizedUnit BuildUnit(bool isSimPath, params string[] sources)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(isSimPath, sources);
        return Pass2Driver.Run(pass1, new List<INormalizer>());
    }

    /// <summary>Run JUST the WU-24 analyzer over a unit.</summary>
    private static Pass3Result Run(NormalizedUnit unit)
        => Pass3Driver.Run(
            unit,
            new ISemanticAnalyzer[] { new GenericInstantiationAnalyzer() });

    /// <summary>
    /// Compose a source declaring <c>class Box&lt;T&gt;</c> and a holder whose
    /// field is <paramref name="depth"/> nested <c>Box&lt;...&gt;</c> levels
    /// closed over <c>int</c> (so the innermost instantiation is reached at
    /// recursion depth <paramref name="depth"/>).
    /// </summary>
    private static string NestedBoxSource(int depth)
    {
        string type = "int";
        for (int i = 0; i < depth; i++)
        {
            type = "Box<" + type + ">";
        }
        return "namespace M { public class Box<T> { } public class Holder { public "
            + type + " Field; } }";
    }

    // -----------------------------------------------------------------
    // Depth 1..15: closure walk passes, entries recorded, no diagnostics.
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(15)]
    public void Closure_WithinDepthBound_RecordsEntries_NoDiagnostics(int depth)
    {
        NormalizedUnit unit = BuildUnit(isSimPath: false, NestedBoxSource(depth));

        Pass3Result result = Run(unit);

        Assert.Empty(result.Diagnostics);
        Assert.False(result.HasErrors);

        Assert.True(result.TryGetSingleton(out GenericClosureResult closure));
        Assert.NotNull(closure);

        // Each nesting level is a distinct closed instantiation:
        // Box<int>, Box<Box<int>>, ... up to the full nesting -> 'depth' of
        // them, de-duplicated by closed display.
        Assert.Equal(depth, closure.Instantiations.Count);

        // Every recorded instantiation is principal in single-module Phase 6.b.
        Assert.All(closure.Instantiations, c => Assert.True(c.IsPrincipalModule));

        // The shallowest (Box<int>) is reached at the deepest recursion depth
        // (it is the innermost type argument); the entries' Depth values cover
        // 1..depth.
        List<int> depths = closure.Instantiations.Select(c => c.Depth).OrderBy(d => d).ToList();
        Assert.Equal(Enumerable.Range(1, depth).ToList(), depths);
    }

    [Fact]
    public void Closure_RecordsOpenDefinitionAndTypeArguments()
    {
        NormalizedUnit unit = BuildUnit(isSimPath: false, NestedBoxSource(2));

        Pass3Result result = Run(unit);

        Assert.True(result.TryGetSingleton(out GenericClosureResult closure));

        // Singleton is sorted by closed display (ordinal).
        List<string> displays = closure.Instantiations.Select(c => c.ClosedDisplay).ToList();
        List<string> sorted = displays.OrderBy(s => s, System.StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, displays);

        ClosedInstantiation inner = closure.Instantiations
            .Single(c => c.ClosedDisplay == "global::M.Box<int>");
        Assert.Equal("global::M.Box<T>", inner.OpenDefinitionDisplay);
        Assert.Equal(new[] { "int" }, inner.TypeArgumentDisplays);

        ClosedInstantiation outer = closure.Instantiations
            .Single(c => c.ClosedDisplay == "global::M.Box<global::M.Box<int>>");
        Assert.Equal("global::M.Box<T>", outer.OpenDefinitionDisplay);
        Assert.Equal(new[] { "global::M.Box<int>" }, outer.TypeArgumentDisplays);
    }

    // -----------------------------------------------------------------
    // Depth 17: XIL2CPP123.
    // -----------------------------------------------------------------

    [Fact]
    public void Closure_ExceedingDepth16_Emits123()
    {
        // 17 nested Box levels -> the innermost is reached at recursion depth
        // 17, one past the bound of 16.
        NormalizedUnit unit = BuildUnit(isSimPath: false, NestedBoxSource(17));

        Pass3Result result = Run(unit);

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            d => d.Code == DiagnosticCodes.GenericInstantiationDepthExceeded
                 && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Closure_AtExactlyDepth16_DoesNotEmit123()
    {
        NormalizedUnit unit = BuildUnit(isSimPath: false, NestedBoxSource(16));

        Pass3Result result = Run(unit);

        Assert.DoesNotContain(
            result.Diagnostics,
            d => d.Code == DiagnosticCodes.GenericInstantiationDepthExceeded);
        Assert.True(result.TryGetSingleton(out GenericClosureResult closure));
        Assert.Equal(16, closure.Instantiations.Count);
    }

    // -----------------------------------------------------------------
    // Self-recursive generic: XIL2CPP124.
    // -----------------------------------------------------------------

    [Fact]
    public void Closure_SelfRecursiveGeneric_Emits124()
    {
        // Node<int> holds a field of its OWN closed type Node<int>; walking
        // Node<int> re-encounters the SAME (open, args) key already on the
        // active path -> a cycle.
        const string source =
            "namespace M { public class Node<T> { public Node<T> Self; } "
            + "public class Holder { public Node<int> Root; } }";

        NormalizedUnit unit = BuildUnit(isSimPath: false, source);

        Pass3Result result = Run(unit);

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            d => d.Code == DiagnosticCodes.CyclicGenericTypeClosure
                 && d.Severity == DiagnosticSeverity.Error);
    }

    // -----------------------------------------------------------------
    // XObject-derived argument bound to 'where T : unmanaged': XIL2CPP125.
    // -----------------------------------------------------------------

    [Fact]
    public void Closure_XObjectArgInUnmanagedConstraint_Emits125()
    {
        // Buffer<T> where T : unmanaged, instantiated with an XObject-derived
        // type. Roslyn reports its own constraint CS diagnostic, but the
        // construction still binds the XObject-derived type argument, which
        // the analyzer rejects with XIL2CPP125.
        const string source =
            "namespace M { public class MyObj : XPact.CoreXObject.XObject { } "
            + "public struct Buffer<T> where T : unmanaged { } "
            + "public class Holder { public Buffer<MyObj> Field; } }";

        NormalizedUnit unit = BuildUnit(isSimPath: false, XObjectStub, source);

        Pass3Result result = Run(unit);

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            d => d.Code == DiagnosticCodes.UnmanagedConstraintViolatedByXObject
                 && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Closure_UnmanagedConstraintWithValueTypeArg_DoesNotEmit125()
    {
        // The same Buffer<T> with a legitimate unmanaged (int) argument: no
        // XIL2CPP125.
        const string source =
            "namespace M { public struct Buffer<T> where T : unmanaged { } "
            + "public class Holder { public Buffer<int> Field; } }";

        NormalizedUnit unit = BuildUnit(isSimPath: false, source);

        Pass3Result result = Run(unit);

        Assert.DoesNotContain(
            result.Diagnostics,
            d => d.Code == DiagnosticCodes.UnmanagedConstraintViolatedByXObject);
    }

    // -----------------------------------------------------------------
    // Non-generic / no-generic modules.
    // -----------------------------------------------------------------

    [Fact]
    public void NoGenerics_RecordsEmptyClosure_NoDiagnostics()
    {
        NormalizedUnit unit = BuildUnit(isSimPath: false,
            "namespace M { public class A { public int F; } }");

        Pass3Result result = Run(unit);

        Assert.Empty(result.Diagnostics);
        Assert.True(result.TryGetSingleton(out GenericClosureResult closure));
        Assert.Empty(closure.Instantiations);
    }

    [Fact]
    public void Closure_RecordedFromObjectCreation()
    {
        const string source =
            "namespace M { public class Box<T> { } "
            + "public class Holder { public object Make() { return new Box<int>(); } } }";

        NormalizedUnit unit = BuildUnit(isSimPath: false, source);

        Pass3Result result = Run(unit);

        Assert.True(result.TryGetSingleton(out GenericClosureResult closure));
        Assert.Contains(closure.Instantiations, c => c.ClosedDisplay == "global::M.Box<int>");
    }

    [Fact]
    public void Closure_DedupesSameInstantiationReachedFromManySites()
    {
        // Box<int> appears at two sites; it must be recorded once and any
        // diagnostic (none here) de-duplicated.
        const string source =
            "namespace M { public class Box<T> { } "
            + "public class Holder { public Box<int> A; public Box<int> B; } }";

        NormalizedUnit unit = BuildUnit(isSimPath: false, source);

        Pass3Result result = Run(unit);

        Assert.True(result.TryGetSingleton(out GenericClosureResult closure));
        Assert.Single(closure.Instantiations, c => c.ClosedDisplay == "global::M.Box<int>");
    }

    // -----------------------------------------------------------------
    // Determinism: two runs over the same source are byte-identical.
    // -----------------------------------------------------------------

    [Fact]
    public void Closure_IsDeterministicAcrossRuns()
    {
        string source = NestedBoxSource(5);

        Pass3Result a = Run(BuildUnit(isSimPath: false, source));
        Pass3Result b = Run(BuildUnit(isSimPath: false, source));

        Assert.True(a.TryGetSingleton(out GenericClosureResult ca));
        Assert.True(b.TryGetSingleton(out GenericClosureResult cb));

        List<string> da = ca.Instantiations.Select(c => c.ClosedDisplay).ToList();
        List<string> db = cb.Instantiations.Select(c => c.ClosedDisplay).ToList();
        Assert.Equal(da, db);
    }
}
