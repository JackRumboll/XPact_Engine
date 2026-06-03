// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Pipeline;

/// <summary>
/// Integration test that locks determinism across the WHOLE discovered Pass
/// pipeline (Pass 2 normalizers + Pass 3 analyzers run via reflection
/// discovery, not an injected set) per /Documents/XIL2CPP.html Rev 4
/// Section 9.9 (gate X-IL2CPP-CSPATH-DET). Running the full pipeline twice
/// over identical input must produce byte-identical results: both the
/// ordered diagnostic stream AND every recorded result object.
/// </summary>
/// <remarks>
/// This is the cross-pass complement to the per-analyzer determinism tests:
/// those pin one analyzer; this pins the union of all 11 normalizers + 12
/// analyzers run under reflection discovery, so a future pass that introduces
/// ambient state (DateTime / Random / hash-order iteration) is caught here
/// even if its own unit test happens not to exercise the non-determinism.
/// </remarks>
public sealed class FullPipelineDeterminismTests
{
    /// <summary>
    /// Run the FULL reflection-discovered pipeline -- Pass2Driver.Run(pass1)
    /// then Pass3Driver.Run(unit), both with NO injected set -- over the
    /// supplied sources at the given sim-path flag.
    /// </summary>
    private static (NormalizedUnit Unit, Pass3Result Result) RunFullPipeline(
        bool isSimPath, params string[] sources)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(isSimPath, sources);
        NormalizedUnit unit = Pass2Driver.Run(pass1);
        Pass3Result result = Pass3Driver.Run(unit);
        return (unit, result);
    }

    /// <summary>
    /// Project a diagnostic stream to its ordered, stable identity:
    /// <c>Code:File:Line:Column</c> per record, joined by newlines.
    /// </summary>
    private static string DiagnosticStream(IEnumerable<DiagnosticRecord> diagnostics)
    {
        StringBuilder sb = new();
        foreach (DiagnosticRecord d in diagnostics)
        {
            sb.Append(d.Code)
              .Append(':').Append(d.File ?? "<none>")
              .Append(':').Append(d.Line?.ToString(CultureInfo.InvariantCulture) ?? "?")
              .Append(':').Append(d.Column?.ToString(CultureInfo.InvariantCulture) ?? "?")
              .Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Serialize every recorded append-list result object in the Pass-3
    /// result deterministically. Discovers every public result record type in
    /// the production <c>XIL2CPP.Analysis</c> assembly, calls
    /// <see cref="Pass3Result.GetAll{T}"/> for each via reflection, and dumps
    /// each item with a recursive, order-preserving serializer (records'
    /// declared-member order is stable, and the analyzers append + sort their
    /// own collections deterministically). This exercises the parallel-safe
    /// result bag (<c>GetAll&lt;T&gt;()</c>) exactly as the task requires.
    /// </summary>
    private static string SerializeRecordedResults(Pass3Result result)
    {
        Assembly analysisAssembly = typeof(Pass3Driver).Assembly;

        // Stable iteration over candidate result types: every public,
        // non-abstract record type in the production analysis assembly,
        // ordered by full name (ordinal). GetAll<T> returns an empty list for
        // any type nothing was added under, so over-enumerating is harmless.
        IEnumerable<Type> resultTypes = analysisAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.IsPublic && IsRecordType(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        MethodInfo getAllOpen = typeof(Pass3Result)
            .GetMethod(nameof(Pass3Result.GetAll), BindingFlags.Public | BindingFlags.Instance)!;

        StringBuilder sb = new();
        foreach (Type type in resultTypes)
        {
            MethodInfo getAll = getAllOpen.MakeGenericMethod(type);
            object? listObj = getAll.Invoke(result, Array.Empty<object>());
            if (listObj is not IEnumerable list)
            {
                continue;
            }

            List<object?> items = list.Cast<object?>().ToList();
            if (items.Count == 0)
            {
                continue;
            }

            sb.Append("== ").Append(type.FullName).Append(" (").Append(items.Count).Append(") ==\n");
            foreach (object? item in items)
            {
                Dump(item, sb, depth: 0);
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    private static bool IsRecordType(Type t)
        // A C# record gets a compiler-synthesized EqualityContract property.
        => t.GetProperty("EqualityContract", BindingFlags.NonPublic | BindingFlags.Instance) is not null;

    /// <summary>
    /// Recursive, deterministic dump: scalars by invariant ToString, records
    /// by declared property order, enumerables element-by-element in their
    /// (stable) iteration order. No hashing / reference identity is observed.
    /// </summary>
    private static void Dump(object? value, StringBuilder sb, int depth)
    {
        if (depth > 8)
        {
            sb.Append("<max-depth>");
            return;
        }

        switch (value)
        {
            case null:
                sb.Append("<null>");
                return;
            case string s:
                sb.Append('"').Append(s).Append('"');
                return;
            case IFormattable f:
                sb.Append(f.ToString(null, CultureInfo.InvariantCulture));
                return;
            case IEnumerable e:
                sb.Append('[');
                bool firstElem = true;
                foreach (object? element in e)
                {
                    if (!firstElem) { sb.Append(", "); }
                    firstElem = false;
                    Dump(element, sb, depth + 1);
                }
                sb.Append(']');
                return;
        }

        Type type = value.GetType();
        if (IsRecordType(type))
        {
            sb.Append(type.Name).Append('{');
            PropertyInfo[] props = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract")
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToArray();
            bool firstProp = true;
            foreach (PropertyInfo p in props)
            {
                if (!firstProp) { sb.Append(", "); }
                firstProp = false;
                sb.Append(p.Name).Append('=');
                Dump(p.GetValue(value), sb, depth + 1);
            }
            sb.Append('}');
            return;
        }

        // Fallback for any non-record object: invariant ToString.
        sb.Append(value.ToString());
    }

    // ==================================================================
    // Determinism over a multi-feature NON-sim-path module.
    // ==================================================================

    [Fact]
    public void FullPipeline_NonSimPath_IsByteIdenticalAcrossTwoRuns()
    {
        (NormalizedUnit unit1, Pass3Result r1) = RunFullPipeline(
            isSimPath: false,
            PipelineTestCorpus.XObjectStub,
            PipelineTestCorpus.MultiFeatureNonSimPath);

        (NormalizedUnit unit2, Pass3Result r2) = RunFullPipeline(
            isSimPath: false,
            PipelineTestCorpus.XObjectStub,
            PipelineTestCorpus.MultiFeatureNonSimPath);

        // 1. The ordered diagnostic stream (Pass-2 then Pass-3) is identical.
        string stream1 = DiagnosticStream(unit1.Diagnostics) + "--P3--\n" + DiagnosticStream(r1.Diagnostics);
        string stream2 = DiagnosticStream(unit2.Diagnostics) + "--P3--\n" + DiagnosticStream(r2.Diagnostics);
        Assert.Equal(stream1, stream2);

        // 2. Every recorded result object (GetAll<T>()) is identical.
        Assert.Equal(SerializeRecordedResults(r1), SerializeRecordedResults(r2));

        // Sanity: this corpus actually exercises the pipeline (XIL2CPP001 for
        // the XObject-derived new is present), so the determinism lock is not
        // vacuously over an empty stream.
        Assert.Contains(
            r1.Diagnostics,
            d => d.Code == DiagnosticCodes.NewExpressionOnXObjectDerived);
        Assert.NotEqual(string.Empty, SerializeRecordedResults(r1));
    }

    // ==================================================================
    // Determinism over a SIM-PATH module that fires the banned-API analyzer.
    // ==================================================================

    [Fact]
    public void FullPipeline_SimPath_IsByteIdenticalAcrossTwoRuns()
    {
        // A sim-path module exercising several banned constructs at once:
        // DateTime.Now (040), Random (049), Interlocked (055), async/await
        // (044), Task usage (048).
        const string simPathSource = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;

            namespace M
            {
                public class C
                {
                    public long When() => DateTime.Now.Ticks;
                    public int Roll(Random r) => r.Next();
                    public void Bump() { int x = 0; Interlocked.Increment(ref x); }
                    public async Task Go() { await Task.Yield(); }
                    public int Read(Task<int> t) => t.Result;
                }
            }
            """;

        (NormalizedUnit unit1, Pass3Result r1) = RunFullPipeline(isSimPath: true, simPathSource);
        (NormalizedUnit unit2, Pass3Result r2) = RunFullPipeline(isSimPath: true, simPathSource);

        string stream1 = DiagnosticStream(unit1.Diagnostics) + "--P3--\n" + DiagnosticStream(r1.Diagnostics);
        string stream2 = DiagnosticStream(unit2.Diagnostics) + "--P3--\n" + DiagnosticStream(r2.Diagnostics);
        Assert.Equal(stream1, stream2);

        Assert.Equal(SerializeRecordedResults(r1), SerializeRecordedResults(r2));

        // Sanity: a sim-path banned-API error is present (so the lock is not
        // vacuous), and the run is reproducible.
        Assert.Contains(r1.Diagnostics, d => d.Code == DiagnosticCodes.SimPathBannedApiCall);
        Assert.True(r1.HasErrors);
    }
}
