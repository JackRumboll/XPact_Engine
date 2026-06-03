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
/// Tests for <see cref="SimPathBannedApiAnalyzer"/> (WU-16) per
/// /Documents/XIL2CPP.html Rev 4 Section 7.4 (build-time banned-API
/// enforcement) + Section 7.8 (sim-path determinism mapping). Verifies each
/// owned code (XIL2CPP040/041/042/043/044/048/049/055/057/058/059/064) is
/// emitted on a sim-path module for its triggering construct, that each hit is
/// paired with a recorded <see cref="BannedApiHit"/>, and that a non-sim-path
/// module emits nothing.
/// </summary>
public sealed class SimPathBannedApiAnalyzerTests
{
    /// <summary>
    /// Run JUST the analyzer over the supplied sources at the given sim-path
    /// flag, via the real Pass-2 -&gt; Pass-3 pipeline.
    /// </summary>
    private static Pass3Result Run(bool isSimPath, params string[] sources)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(isSimPath, sources);
        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        return Pass3Driver.Run(unit, new ISemanticAnalyzer[] { new SimPathBannedApiAnalyzer() });
    }

    /// <summary>Codes present in the result's diagnostics, in order.</summary>
    private static List<string> Codes(Pass3Result result)
        => result.Diagnostics.Select(d => d.Code).ToList();

    /// <summary>
    /// A minimal stand-in <c>FName</c> type so XIL2CPP059 binds without the
    /// engine BCL.
    /// </summary>
    private const string FNameStub =
        "namespace Engine { public sealed class FName { public FName(string s) { } } }";

    // ==================================================================
    // XIL2CPP040 -- generic banned API.
    // ==================================================================

    [Fact]
    public void DateTimeNow_OnSimPath_EmitsXIL2CPP040()
    {
        Pass3Result result = Run(isSimPath: true,
            "using System; namespace M { public class C { public long F() => DateTime.Now.Ticks; } }");

        Assert.Contains(DiagnosticCodes.SimPathBannedApiCall, Codes(result));
        DiagnosticRecord hit = result.Diagnostics.Single(d => d.Code == DiagnosticCodes.SimPathBannedApiCall);
        Assert.Equal(DiagnosticSeverity.Error, hit.Severity);
        Assert.Contains("System.DateTime.Now", hit.Message);
        Assert.True(result.HasErrors);

        BannedApiHit recorded = result.GetAll<BannedApiHit>()
            .Single(h => h.Code == DiagnosticCodes.SimPathBannedApiCall);
        Assert.Equal("System.DateTime.Now", recorded.FullName);
    }

    [Fact]
    public void EnvironmentTickCount_OnSimPath_EmitsXIL2CPP040()
    {
        Pass3Result result = Run(isSimPath: true,
            "using System; namespace M { public class C { public int F() => Environment.TickCount; } }");

        Assert.Contains(DiagnosticCodes.SimPathBannedApiCall, Codes(result));
        Assert.Contains(result.GetAll<BannedApiHit>(), h => h.FullName == "System.Environment.TickCount");
    }

    [Fact]
    public void SystemLinq_OnSimPath_EmitsXIL2CPP040()
    {
        Pass3Result result = Run(isSimPath: true,
            "using System.Linq; using System.Collections.Generic; namespace M { public class C { public int F(List<int> xs) => xs.Count(); } }");

        Assert.Contains(DiagnosticCodes.SimPathBannedApiCall, Codes(result));
        Assert.Contains(result.GetAll<BannedApiHit>(), h => h.FullName.Contains("Enumerable.Count"));
    }

    // ==================================================================
    // XIL2CPP041 -- foreach over HashSet/Dictionary.
    // ==================================================================

    [Fact]
    public void ForeachOverHashSet_OnSimPath_EmitsXIL2CPP041()
    {
        Pass3Result result = Run(isSimPath: true,
            "using System.Collections.Generic; namespace M { public class C { public void F(HashSet<int> xs) { foreach (var x in xs) { } } } }");

        Assert.Contains(DiagnosticCodes.NonDeterministicIterationOrder, Codes(result));
        DiagnosticRecord rec = result.Diagnostics.Single(d => d.Code == DiagnosticCodes.NonDeterministicIterationOrder);
        Assert.Equal(DiagnosticSeverity.Error, rec.Severity);
    }

    [Fact]
    public void ForeachOverDictionary_OnSimPath_EmitsXIL2CPP041()
    {
        Pass3Result result = Run(isSimPath: true,
            "using System.Collections.Generic; namespace M { public class C { public void F(Dictionary<int,int> d) { foreach (var kv in d) { } } } }");

        Assert.Contains(DiagnosticCodes.NonDeterministicIterationOrder, Codes(result));
    }

    [Fact]
    public void ForeachOverList_OnSimPath_DoesNotEmit041()
    {
        // A concrete List<T> iterated by its concrete type is deterministic.
        Pass3Result result = Run(isSimPath: true,
            "using System.Collections.Generic; namespace M { public class C { public void F(List<int> xs) { foreach (var x in xs) { } } } }");

        Assert.DoesNotContain(DiagnosticCodes.NonDeterministicIterationOrder, Codes(result));
    }

    // ==================================================================
    // XIL2CPP042 -- string index at the UTF-8 boundary.
    // ==================================================================

    [Fact]
    public void StringIndex_OnSimPath_EmitsXIL2CPP042()
    {
        Pass3Result result = Run(isSimPath: true,
            "namespace M { public class C { public char F(string s) => s[0]; } }");

        Assert.Contains(DiagnosticCodes.SimPathStringIndexNonDeterministic, Codes(result));
        Assert.Contains(result.GetAll<BannedApiHit>(), h => h.Code == DiagnosticCodes.SimPathStringIndexNonDeterministic);
    }

    [Fact]
    public void ArrayIndex_OnSimPath_DoesNotEmit042()
    {
        Pass3Result result = Run(isSimPath: true,
            "namespace M { public class C { public int F(int[] a) => a[0]; } }");

        Assert.DoesNotContain(DiagnosticCodes.SimPathStringIndexNonDeterministic, Codes(result));
    }

    // ==================================================================
    // XIL2CPP043 -- lock.
    // ==================================================================

    [Fact]
    public void Lock_OnSimPath_EmitsXIL2CPP043()
    {
        Pass3Result result = Run(isSimPath: true,
            "namespace M { public class C { private readonly object _g = new object(); public void F() { lock (_g) { } } } }");

        Assert.Contains(DiagnosticCodes.SimPathLockBanned, Codes(result));
        DiagnosticRecord rec = result.Diagnostics.Single(d => d.Code == DiagnosticCodes.SimPathLockBanned);
        Assert.Equal(DiagnosticSeverity.Error, rec.Severity);
    }

    // ==================================================================
    // XIL2CPP044 -- async/await.
    // ==================================================================

    [Fact]
    public void AsyncMethod_OnSimPath_EmitsXIL2CPP044()
    {
        Pass3Result result = Run(isSimPath: true,
            "using System.Threading.Tasks; namespace M { public class C { public async Task F() { await Task.Yield(); } } }");

        Assert.Contains(DiagnosticCodes.SimPathAsyncAwaitBanned, Codes(result));
    }

    [Fact]
    public void AwaitExpression_OnSimPath_EmitsXIL2CPP044()
    {
        // The await keyword itself emits 044 (in addition to the async
        // declaration). Assert at least one 044 hit is present.
        Pass3Result result = Run(isSimPath: true,
            "using System.Threading.Tasks; namespace M { public class C { public async Task F() { await Task.Yield(); } } }");

        Assert.True(Codes(result).Count(c => c == DiagnosticCodes.SimPathAsyncAwaitBanned) >= 1);
    }

    // ==================================================================
    // XIL2CPP048 -- Task / ValueTask / IAsyncEnumerable / await foreach /
    // Task.Result / Task.Wait.
    // ==================================================================

    [Fact]
    public void TaskTypedField_OnSimPath_EmitsXIL2CPP048()
    {
        Pass3Result result = Run(isSimPath: true,
            "using System.Threading.Tasks; namespace M { public class C { private Task<int>? _t; } }");

        Assert.Contains(DiagnosticCodes.SimPathTaskBanned, Codes(result));
    }

    [Fact]
    public void TaskResult_OnSimPath_EmitsXIL2CPP048()
    {
        Pass3Result result = Run(isSimPath: true,
            "using System.Threading.Tasks; namespace M { public class C { public int F(Task<int> t) => t.Result; } }");

        Assert.Contains(DiagnosticCodes.SimPathTaskBanned, Codes(result));
    }

    [Fact]
    public void TaskWait_OnSimPath_EmitsXIL2CPP048()
    {
        Pass3Result result = Run(isSimPath: true,
            "using System.Threading.Tasks; namespace M { public class C { public void F(Task t) { t.Wait(); } } }");

        Assert.Contains(DiagnosticCodes.SimPathTaskBanned, Codes(result));
    }

    [Fact]
    public void AwaitForeach_OnSimPath_EmitsXIL2CPP048()
    {
        Pass3Result result = Run(isSimPath: true,
            "using System.Collections.Generic; using System.Threading.Tasks; namespace M { public class C { public async Task F(IAsyncEnumerable<int> xs) { await foreach (var x in xs) { } } } }");

        Assert.Contains(DiagnosticCodes.SimPathTaskBanned, Codes(result));
    }

    // ==================================================================
    // XIL2CPP049 -- Random.*.
    // ==================================================================

    [Fact]
    public void RandomNext_OnSimPath_EmitsXIL2CPP049()
    {
        Pass3Result result = Run(isSimPath: true,
            "using System; namespace M { public class C { public int F(Random r) => r.Next(); } }");

        Assert.Contains(DiagnosticCodes.SimPathRandomBanned, Codes(result));
        DiagnosticRecord rec = result.Diagnostics.Single(d => d.Code == DiagnosticCodes.SimPathRandomBanned);
        Assert.Equal(DiagnosticSeverity.Error, rec.Severity);
    }

    [Fact]
    public void RandomShared_OnSimPath_EmitsXIL2CPP049_Once()
    {
        // Random.Shared.Next() must collapse to a single 049 hit (the chain
        // skip rule), not one per member access.
        Pass3Result result = Run(isSimPath: true,
            "using System; namespace M { public class C { public int F() => Random.Shared.Next(); } }");

        Assert.Equal(1, Codes(result).Count(c => c == DiagnosticCodes.SimPathRandomBanned));
    }

    // ==================================================================
    // XIL2CPP055 -- Interlocked.*.
    // ==================================================================

    [Fact]
    public void Interlocked_OnSimPath_EmitsXIL2CPP055()
    {
        Pass3Result result = Run(isSimPath: true,
            "using System.Threading; namespace M { public class C { private int _x; public void F() { Interlocked.Increment(ref _x); } } }");

        Assert.Contains(DiagnosticCodes.SimPathInterlockedBanned, Codes(result));
        Assert.Equal(1, Codes(result).Count(c => c == DiagnosticCodes.SimPathInterlockedBanned));
    }

    // ==================================================================
    // XIL2CPP057 -- [ThreadStatic].
    // ==================================================================

    [Fact]
    public void ThreadStatic_OnSimPath_EmitsXIL2CPP057()
    {
        Pass3Result result = Run(isSimPath: true,
            "using System; namespace M { public class C { [ThreadStatic] private static int _x; } }");

        Assert.Contains(DiagnosticCodes.SimPathThreadStaticBanned, Codes(result));
        DiagnosticRecord rec = result.Diagnostics.Single(d => d.Code == DiagnosticCodes.SimPathThreadStaticBanned);
        Assert.Equal(DiagnosticSeverity.Error, rec.Severity);
    }

    // ==================================================================
    // XIL2CPP058 -- locale-dependent ToString / Parse.
    // ==================================================================

    [Fact]
    public void IntParse_NoCulture_OnSimPath_EmitsXIL2CPP058()
    {
        Pass3Result result = Run(isSimPath: true,
            "namespace M { public class C { public int F(string s) => int.Parse(s); } }");

        Assert.Contains(DiagnosticCodes.SimPathLocaleDependentToStringParse, Codes(result));
    }

    [Fact]
    public void DoubleToString_NoCulture_OnSimPath_EmitsXIL2CPP058()
    {
        Pass3Result result = Run(isSimPath: true,
            "namespace M { public class C { public string F(double d) => d.ToString(); } }");

        Assert.Contains(DiagnosticCodes.SimPathLocaleDependentToStringParse, Codes(result));
    }

    [Fact]
    public void IntParse_WithInvariantCulture_OnSimPath_DoesNotEmit058()
    {
        // The explicitly-culture-aware overload is allowed.
        Pass3Result result = Run(isSimPath: true,
            "using System.Globalization; namespace M { public class C { public int F(string s) => int.Parse(s, CultureInfo.InvariantCulture); } }");

        Assert.DoesNotContain(DiagnosticCodes.SimPathLocaleDependentToStringParse, Codes(result));
    }

    // ==================================================================
    // XIL2CPP059 -- FName from non-literal string (Warning).
    // ==================================================================

    [Fact]
    public void FNameFromNonLiteral_OnSimPath_EmitsXIL2CPP059_Warning()
    {
        Pass3Result result = Run(isSimPath: true,
            FNameStub,
            "using Engine; namespace M { public class C { public FName F(string s) => new FName(s); } }");

        Assert.Contains(DiagnosticCodes.SimPathFNameFromNonLiteral, Codes(result));
        DiagnosticRecord rec = result.Diagnostics.Single(d => d.Code == DiagnosticCodes.SimPathFNameFromNonLiteral);
        Assert.Equal(DiagnosticSeverity.Warning, rec.Severity);
        // A warning-only hit must not flip HasErrors.
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void FNameFromLiteral_OnSimPath_DoesNotEmit059()
    {
        Pass3Result result = Run(isSimPath: true,
            FNameStub,
            "using Engine; namespace M { public class C { public FName F() => new FName(\"Tag\"); } }");

        Assert.DoesNotContain(DiagnosticCodes.SimPathFNameFromNonLiteral, Codes(result));
    }

    // ==================================================================
    // XIL2CPP064 -- foreach over IEnumerable<T> interface.
    // ==================================================================

    [Fact]
    public void ForeachOverIEnumerableInterface_OnSimPath_EmitsXIL2CPP064()
    {
        Pass3Result result = Run(isSimPath: true,
            "using System.Collections.Generic; namespace M { public class C { public void F(IEnumerable<int> xs) { foreach (var x in xs) { } } } }");

        Assert.Contains(DiagnosticCodes.SimPathForeachOverIEnumerableBanned, Codes(result));
        DiagnosticRecord rec = result.Diagnostics.Single(d => d.Code == DiagnosticCodes.SimPathForeachOverIEnumerableBanned);
        Assert.Equal(DiagnosticSeverity.Error, rec.Severity);
    }

    // ==================================================================
    // Non-sim-path: the analyzer must emit NOTHING.
    // ==================================================================

    [Fact]
    public void NonSimPath_EmitsNothing_AcrossEveryBannedConstruct()
    {
        const string kitchenSink =
            "using System; using System.Linq; using System.Threading; using System.Threading.Tasks; using System.Collections.Generic; " +
            "namespace M { public class C { " +
            "[ThreadStatic] private static int _ts; " +
            "private readonly object _g = new object(); " +
            "private Task<int>? _t; " +
            "public async Task A() { await Task.Yield(); } " +
            "public long B() => DateTime.Now.Ticks; " +
            "public int Rng(Random r) => r.Next(); " +
            "public void Il() { int x = 0; Interlocked.Increment(ref x); } " +
            "public char Str(string s) => s[0]; " +
            "public int Pi(string s) => int.Parse(s); " +
            "public void Lk() { lock (_g) { } } " +
            "public void Fe(HashSet<int> h, IEnumerable<int> e) { foreach (var i in h) { } foreach (var j in e) { } } " +
            "public int Lq(List<int> xs) => xs.Count(); " +
            "} }";

        Pass3Result result = Run(isSimPath: false, kitchenSink);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.GetAll<BannedApiHit>());
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void SimPath_CleanModule_EmitsNothing()
    {
        // A sim-path module that uses no banned API emits nothing.
        Pass3Result result = Run(isSimPath: true,
            "namespace M { public class C { public int Add(int a, int b) => a + b; } }");

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.GetAll<BannedApiHit>());
    }

    // ==================================================================
    // Cross-cutting: every emitted diagnostic is anchored + paired with a hit.
    // ==================================================================

    [Fact]
    public void EverySimPathHit_IsAnchored_AndPairedWithABannedApiHit()
    {
        Pass3Result result = Run(isSimPath: true,
            "using System; namespace M { public class C { public long F() => DateTime.Now.Ticks; } }");

        Assert.NotEmpty(result.Diagnostics);
        foreach (DiagnosticRecord d in result.Diagnostics)
        {
            Assert.NotNull(d.File);
            Assert.NotNull(d.Line);
            Assert.NotNull(d.Column);
            Assert.Equal("TestModule", d.Module);
        }

        // Diagnostics and hits are one-to-one.
        Assert.Equal(result.Diagnostics.Count, result.GetAll<BannedApiHit>().Count);
    }

    [Fact]
    public void Determinism_TwoRuns_ProduceIdenticalDiagnosticSequences()
    {
        const string source =
            "using System; using System.Threading; namespace M { public class C { " +
            "public long A() => DateTime.Now.Ticks; " +
            "public int B(Random r) => r.Next(); " +
            "public void D() { int x = 0; Interlocked.Increment(ref x); } } }";

        List<string> first = Codes(Run(isSimPath: true, source));
        List<string> second = Codes(Run(isSimPath: true, source));

        Assert.Equal(first, second);
    }
}
