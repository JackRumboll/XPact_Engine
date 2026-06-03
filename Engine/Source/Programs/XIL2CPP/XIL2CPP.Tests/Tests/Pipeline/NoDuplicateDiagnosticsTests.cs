// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Pipeline;

/// <summary>
/// The standing guard against cross-analyzer double-emission: running the
/// FULL reflection-discovered Pass 3 over a corpus that triggers MULTIPLE
/// analyzers simultaneously must never produce two diagnostics sharing the
/// same <c>(Code, File, Line, Column)</c> tuple. Per /Documents/XIL2CPP.html
/// Rev 4 Section 7.4 each banned code has exactly ONE owning analyzer
/// (e.g. the sim-path 044/048 band is owned solely by
/// <see cref="SimPathBannedApiAnalyzer"/>, while
/// <see cref="AsyncStateMachineAnalyzer"/> records async state-machine
/// metadata but emits no diagnostics); this test proves that ownership holds
/// when every analyzer runs together.
/// </summary>
/// <remarks>
/// If this test finds a genuine duplicate (two analyzers emitting the same
/// code at the same site), it must NOT be suppressed: the offending
/// (code, analyzers) pair is the bug, owned by one of the analyzers, and the
/// duplicate is reported by the assertion message so the owning analyzer can
/// be fixed.
/// </remarks>
public sealed class NoDuplicateDiagnosticsTests
{
    /// <summary>
    /// Run the FULL reflection-discovered Pass 3 (every production analyzer,
    /// no injected set) over the supplied sources, through the real Pass 2
    /// pipeline, at the given sim-path flag.
    /// </summary>
    private static Pass3Result RunFullPass3(bool isSimPath, params string[] sources)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(isSimPath, sources);
        NormalizedUnit unit = Pass2Driver.Run(pass1);
        return Pass3Driver.Run(unit);
    }

    /// <summary>
    /// Assert no two diagnostics in <paramref name="result"/> share the same
    /// <c>(Code, File, Line, Column)</c> tuple. On failure the assertion
    /// message lists every colliding tuple so the orchestrator can identify
    /// the owning analyzer.
    /// </summary>
    private static void AssertNoDuplicateSites(Pass3Result result)
    {
        IEnumerable<IGrouping<string, DiagnosticRecord>> groups = result.Diagnostics
            .GroupBy(SiteKey);

        List<string> duplicates = groups
            .Where(g => g.Count() > 1)
            .Select(g => string.Format(
                CultureInfo.InvariantCulture,
                "{0} x{1}", g.Key, g.Count()))
            .OrderBy(s => s, System.StringComparer.Ordinal)
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            "Cross-analyzer duplicate diagnostics found at identical sites: "
            + string.Join("; ", duplicates));
    }

    private static string SiteKey(DiagnosticRecord d) => string.Format(
        CultureInfo.InvariantCulture,
        "{0}:{1}:{2}:{3}",
        d.Code,
        d.File ?? "<none>",
        d.Line?.ToString(CultureInfo.InvariantCulture) ?? "?",
        d.Column?.ToString(CultureInfo.InvariantCulture) ?? "?");

    // ==================================================================
    // The critical 044/048 overlap case: a SIM-PATH module with an async
    // method + Task usage + await foreach, where AsyncStateMachineAnalyzer
    // and SimPathBannedApiAnalyzer could overlap on 044/048.
    // ==================================================================

    [Fact]
    public void SimPath_AsyncTaskAwaitForeach_NoDuplicateSites()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Threading.Tasks;

            namespace M
            {
                public class C
                {
                    private Task<int>? _pending;

                    public async Task Drain(IAsyncEnumerable<int> xs)
                    {
                        await foreach (var x in xs) { }
                        await Task.Yield();
                    }

                    public int Peek(Task<int> t) => t.Result;
                }
            }
            """;

        Pass3Result result = RunFullPass3(isSimPath: true, source);

        // The overlap band must actually fire (the test is not vacuous):
        // 044 (async/await) and 048 (Task / await foreach / Task.Result).
        List<string> codes = result.Diagnostics.Select(d => d.Code).ToList();
        Assert.Contains(DiagnosticCodes.SimPathAsyncAwaitBanned, codes);
        Assert.Contains(DiagnosticCodes.SimPathTaskBanned, codes);

        AssertNoDuplicateSites(result);
    }

    // ==================================================================
    // An XObject-derived new + a banned feature on the same module: the
    // new-expression (XIL2CPP001, NewExpressionAnalyzer) and a banned
    // feature (BannedFeatureAnalyzer) must not collide.
    // ==================================================================

    [Fact]
    public void NonSimPath_XObjectNewPlusBannedFeature_NoDuplicateSites()
    {
        const string source = """
            using System;
            using XPact.CoreXObject;

            namespace M
            {
                public sealed class Widget : XObject { }

                public class C
                {
                    // XObject-derived new -> XIL2CPP001 (NewExpressionAnalyzer).
                    public Widget Make() => new Widget();

                    // 'dynamic' -> XIL2CPP011 (BannedFeatureAnalyzer).
                    public void UseDynamic() { dynamic d = 1; d.ToString(); }

                    // ref XObject parameter -> XIL2CPP005 (BannedFeatureAnalyzer).
                    public void Swap(ref Widget w) { }
                }
            }
            """;

        Pass3Result result = RunFullPass3(
            isSimPath: false, PipelineTestCorpus.XObjectStub, source);

        List<string> codes = result.Diagnostics.Select(d => d.Code).ToList();
        // The new-expression analyzer fired (XIL2CPP001) and at least one
        // banned-feature code fired, so multiple analyzers ran together.
        Assert.Contains(DiagnosticCodes.NewExpressionOnXObjectDerived, codes);
        Assert.Contains(codes, c =>
            c == DiagnosticCodes.DynamicNotSupported
            || c == DiagnosticCodes.RefOutXObjectParameterUnsupported);

        AssertNoDuplicateSites(result);
    }

    // ==================================================================
    // A combined SIM-PATH module that fires async (044/048), an
    // XObject-derived new (001), and a sim-path banned API (040): the
    // broadest cross-analyzer overlap in one pass.
    // ==================================================================

    [Fact]
    public void SimPath_AsyncPlusXObjectNewPlusBannedApi_NoDuplicateSites()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            using XPact.CoreXObject;

            namespace M
            {
                public sealed class Widget : XObject { }

                public class C
                {
                    public Widget Make() => new Widget();           // 001
                    public long When() => DateTime.Now.Ticks;       // 040
                    public async Task Go() { await Task.Yield(); }  // 044 + 048-family
                    public int Read(Task<int> t) => t.Result;       // 048
                }
            }
            """;

        Pass3Result result = RunFullPass3(
            isSimPath: true, PipelineTestCorpus.XObjectStub, source);

        List<string> codes = result.Diagnostics.Select(d => d.Code).ToList();
        Assert.Contains(DiagnosticCodes.NewExpressionOnXObjectDerived, codes);
        Assert.Contains(DiagnosticCodes.SimPathBannedApiCall, codes);
        Assert.Contains(DiagnosticCodes.SimPathAsyncAwaitBanned, codes);

        AssertNoDuplicateSites(result);
    }

    // ==================================================================
    // The multi-feature corpus shared with the determinism test also runs
    // every analyzer; it must be duplicate-free too.
    // ==================================================================

    [Fact]
    public void NonSimPath_MultiFeatureCorpus_NoDuplicateSites()
    {
        Pass3Result result = RunFullPass3(
            isSimPath: false,
            PipelineTestCorpus.XObjectStub,
            PipelineTestCorpus.MultiFeatureNonSimPath);

        AssertNoDuplicateSites(result);
    }
}
