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
/// Tests for <see cref="CrossModuleNoThrowAnalyzer"/> (XIL2CPP Phase 6.b,
/// WU-25) per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2 / 3.3. Each
/// fixture builds a Pass-1 result from synthetic C# source, runs Pass 2 with
/// no normalizers, then runs JUST the cross-module NoThrow analyzer in
/// isolation via
/// <see cref="Pass3Driver.Run(NormalizedUnit, IReadOnlyList{ISemanticAnalyzer})"/>,
/// and asserts the emitted diagnostics + the <see cref="CrossModuleNoThrowTable"/>
/// singleton. Cross-module callees are modelled with the Phase-6.b synthetic
/// marker attributes <c>[XExternalModule]</c> / <c>[XFunction(NoThrow = ...)]</c>.
/// </summary>
public sealed class CrossModuleNoThrowAnalyzerTests
{
    /// <summary>
    /// The synthetic stand-in attributes every fixture declares: a NoThrow
    /// proof attribute and a Phase-6.b cross-module marker (with an optional
    /// reflection-entry toggle). These mirror the canonical
    /// <c>XPact.CoreXObject.XFunctionAttribute</c> the doc names.
    /// </summary>
    private const string AttributeShims = """
        namespace XPact.CoreXObject
        {
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public sealed class XFunctionAttribute : System.Attribute
            {
                public bool NoThrow { get; set; }
                public bool CanThrow { get; set; }
            }

            [System.AttributeUsage(System.AttributeTargets.Method)]
            public sealed class XExternalModuleAttribute : System.Attribute
            {
                public bool HasReflectionEntry { get; set; } = true;
            }
        }
        """;

    private static Pass3Result Run(params string[] sources)
    {
        // Always prepend the attribute shim source.
        string[] all = new string[sources.Length + 1];
        all[0] = AttributeShims;
        sources.CopyTo(all, 1);

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(isSimPath: false, all);
        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        return Pass3Driver.Run(
            unit, new ISemanticAnalyzer[] { new CrossModuleNoThrowAnalyzer() });
    }

    private static IReadOnlyList<DiagnosticRecord> OfCode(Pass3Result r, string code)
        => r.Diagnostics.Where(d => d.Code == code).ToList();

    // ---------------------------------------------------------------
    // Scenario 1: NoThrow-annotated cross-module callee -> Tier-2 eligible.
    // No diagnostics; the table records Tier2Eligible; caller NOT conservative.
    // ---------------------------------------------------------------

    [Fact]
    public void NoThrowAnnotatedCallee_IsTier2Eligible_NoDiagnostics()
    {
        const string source = """
            using XPact.CoreXObject;
            namespace M;
            public static class Dep
            {
                [XExternalModule]
                [XFunction(NoThrow = true)]
                public static void SafeCallee() { }
            }
            public class Caller
            {
                public void Run() => Dep.SafeCallee();
            }
            """;

        Pass3Result result = Run(source);

        Assert.Empty(OfCode(result, DiagnosticCodes.ConservativeTier1UnresolvedCallee));
        Assert.Empty(OfCode(result, DiagnosticCodes.NoThrowLookupFailed));
        Assert.Empty(OfCode(result, DiagnosticCodes.NoThrowProofFailed));
        Assert.False(result.HasErrors);

        CrossModuleNoThrowTable? table = result.GetSingleton<CrossModuleNoThrowTable>();
        Assert.NotNull(table);

        CrossModuleNoThrowCalleeResult callee = Assert.Single(
            table!.CalleeResults,
            c => c.CalleeDisplay.Contains("SafeCallee"));
        Assert.Equal(CrossModuleNoThrowStatus.Tier2Eligible, callee.Status);
        Assert.Contains("Run", callee.CallerDisplay);

        CrossModuleCallerTierFlag callerFlag = Assert.Single(
            table.CallerFlags, f => f.CallerDisplay.Contains(".Run("));
        Assert.False(callerFlag.ConservativeTier1);
        Assert.Empty(callerFlag.ForcingCallees);
    }

    // ---------------------------------------------------------------
    // Scenario 2: unannotated cross-module-style callee -> XIL2CPP031 warning,
    // caller flagged conservative Tier 1.
    // ---------------------------------------------------------------

    [Fact]
    public void UnannotatedCrossModuleCallee_EmitsConservativeTier1Warning()
    {
        const string source = """
            using XPact.CoreXObject;
            namespace M;
            public static class Dep
            {
                [XExternalModule]
                public static void PlainCallee() { }
            }
            public class Caller
            {
                public void Run() => Dep.PlainCallee();
            }
            """;

        Pass3Result result = Run(source);

        DiagnosticRecord warn = Assert.Single(
            OfCode(result, DiagnosticCodes.ConservativeTier1UnresolvedCallee));
        Assert.Equal(XilSeverity.Warning, warn.Severity);
        Assert.Contains("PlainCallee", warn.Message);
        Assert.Contains("conservatively Tier 1", warn.Message);
        Assert.NotNull(warn.Line);
        Assert.Equal("TestModule", warn.Module);

        Assert.Empty(OfCode(result, DiagnosticCodes.NoThrowLookupFailed));
        Assert.False(result.HasErrors); // only a warning

        CrossModuleNoThrowTable? table = result.GetSingleton<CrossModuleNoThrowTable>();
        Assert.NotNull(table);

        CrossModuleNoThrowCalleeResult callee = Assert.Single(
            table!.CalleeResults, c => c.CalleeDisplay.Contains("PlainCallee"));
        Assert.Equal(CrossModuleNoThrowStatus.ConservativeTier1, callee.Status);

        CrossModuleCallerTierFlag callerFlag = Assert.Single(
            table.CallerFlags, f => f.CallerDisplay.Contains(".Run("));
        Assert.True(callerFlag.ConservativeTier1);
        Assert.Single(callerFlag.ForcingCallees, c => c.Contains("PlainCallee"));
    }

    // ---------------------------------------------------------------
    // Scenario 3: cross-module callee with no reflection entry -> XIL2CPP036.
    // ---------------------------------------------------------------

    [Fact]
    public void MissingReflectionEntry_EmitsLookupFailedError()
    {
        const string source = """
            using XPact.CoreXObject;
            namespace M;
            public static class Dep
            {
                [XExternalModule(HasReflectionEntry = false)]
                public static void GhostCallee() { }
            }
            public class Caller
            {
                public void Run() => Dep.GhostCallee();
            }
            """;

        Pass3Result result = Run(source);

        DiagnosticRecord err = Assert.Single(
            OfCode(result, DiagnosticCodes.NoThrowLookupFailed));
        Assert.Equal(XilSeverity.Error, err.Severity);
        Assert.Contains("GhostCallee", err.Message);
        Assert.Contains("no XHT-emitted reflection entry", err.Message);
        Assert.True(result.HasErrors);

        Assert.Empty(OfCode(result, DiagnosticCodes.ConservativeTier1UnresolvedCallee));

        CrossModuleNoThrowTable? table = result.GetSingleton<CrossModuleNoThrowTable>();
        Assert.NotNull(table);

        CrossModuleNoThrowCalleeResult callee = Assert.Single(
            table!.CalleeResults, c => c.CalleeDisplay.Contains("GhostCallee"));
        Assert.Equal(CrossModuleNoThrowStatus.MissingReflectionEntry, callee.Status);

        CrossModuleCallerTierFlag callerFlag = Assert.Single(
            table.CallerFlags, f => f.CallerDisplay.Contains(".Run("));
        Assert.True(callerFlag.ConservativeTier1);
    }

    // ---------------------------------------------------------------
    // Scenario 4: [XFunction(NoThrow = true)] caller with a body throw ->
    // XIL2CPP030, enriched with the failing chain (the throw site).
    // ---------------------------------------------------------------

    [Fact]
    public void NoThrowCaller_WithBodyThrow_EmitsProofFailedWithChain()
    {
        const string source = """
            using XPact.CoreXObject;
            namespace M;
            public class Caller
            {
                [XFunction(NoThrow = true)]
                public void Run()
                {
                    throw new System.InvalidOperationException();
                }
            }
            """;

        Pass3Result result = Run(source);

        DiagnosticRecord err = Assert.Single(
            OfCode(result, DiagnosticCodes.NoThrowProofFailed));
        Assert.Equal(XilSeverity.Error, err.Severity);
        Assert.Contains("proof failed", err.Message);
        Assert.Contains("Failing callee chain", err.Message);
        Assert.Contains("contains throw", err.Message);
        Assert.True(result.HasErrors);
    }

    // ---------------------------------------------------------------
    // Scenario 5: [XFunction(NoThrow = true)] caller whose proof fails because
    // it calls a non-NoThrow cross-module callee -> XIL2CPP030 + XIL2CPP031.
    // The 030 chain names the failing cross-module callee.
    // ---------------------------------------------------------------

    [Fact]
    public void NoThrowCaller_CallingNonNoThrowCrossModuleCallee_EmitsProofFailedNamingCallee()
    {
        const string source = """
            using XPact.CoreXObject;
            namespace M;
            public static class Dep
            {
                [XExternalModule]
                public static void PlainCallee() { }
            }
            public class Caller
            {
                [XFunction(NoThrow = true)]
                public void Run() => Dep.PlainCallee();
            }
            """;

        Pass3Result result = Run(source);

        DiagnosticRecord proof = Assert.Single(
            OfCode(result, DiagnosticCodes.NoThrowProofFailed));
        Assert.Contains("PlainCallee", proof.Message);
        Assert.Contains("cross-module", proof.Message);

        // The same non-NoThrow cross-module call also yields the conservative
        // Tier-1 warning.
        Assert.Single(OfCode(result, DiagnosticCodes.ConservativeTier1UnresolvedCallee));
        Assert.True(result.HasErrors);
    }

    // ---------------------------------------------------------------
    // Scenario 6: a NoThrow-annotated caller that only calls a NoThrow
    // cross-module callee proves clean (no XIL2CPP030).
    // ---------------------------------------------------------------

    [Fact]
    public void NoThrowCaller_CallingOnlyNoThrowCallees_ProvesClean()
    {
        const string source = """
            using XPact.CoreXObject;
            namespace M;
            public static class Dep
            {
                [XExternalModule]
                [XFunction(NoThrow = true)]
                public static void SafeCallee() { }
            }
            public class Caller
            {
                [XFunction(NoThrow = true)]
                public void Run() => Dep.SafeCallee();
            }
            """;

        Pass3Result result = Run(source);

        Assert.Empty(OfCode(result, DiagnosticCodes.NoThrowProofFailed));
        Assert.Empty(OfCode(result, DiagnosticCodes.ConservativeTier1UnresolvedCallee));
        Assert.False(result.HasErrors);
    }

    // ---------------------------------------------------------------
    // Scenario 7: same-module callees never trigger cross-module codes.
    // ---------------------------------------------------------------

    [Fact]
    public void SameModuleCallee_NoCrossModuleDiagnostics()
    {
        const string source = """
            namespace M;
            public class Caller
            {
                public void Run() => Helper();
                private void Helper() { }
            }
            """;

        Pass3Result result = Run(source);

        Assert.Empty(OfCode(result, DiagnosticCodes.ConservativeTier1UnresolvedCallee));
        Assert.Empty(OfCode(result, DiagnosticCodes.NoThrowLookupFailed));
        Assert.Empty(OfCode(result, DiagnosticCodes.NoThrowProofFailed));

        CrossModuleNoThrowTable? table = result.GetSingleton<CrossModuleNoThrowTable>();
        Assert.NotNull(table);
        // No cross-module callee results recorded.
        Assert.DoesNotContain(table!.CalleeResults, c => c.CalleeDisplay.Contains("Helper"));
    }

    // ---------------------------------------------------------------
    // Scenario 8: BCL callees are exempt from the cross-module NoThrow lookup
    // (the curated BCL surface is not an XPact cross-module callee).
    // ---------------------------------------------------------------

    [Fact]
    public void BclCallee_IsExemptFromLookup()
    {
        const string source = """
            namespace M;
            public class Caller
            {
                public string Run(int x) => x.ToString();
            }
            """;

        Pass3Result result = Run(source);

        Assert.Empty(OfCode(result, DiagnosticCodes.ConservativeTier1UnresolvedCallee));
        Assert.Empty(OfCode(result, DiagnosticCodes.NoThrowLookupFailed));
        Assert.False(result.HasErrors);
    }

    // ---------------------------------------------------------------
    // Scenario 9: determinism -- two runs over the same source produce an
    // identical diagnostic sequence (codes + messages + spans).
    // ---------------------------------------------------------------

    [Fact]
    public void Deterministic_AcrossRuns()
    {
        const string source = """
            using XPact.CoreXObject;
            namespace M;
            public static class Dep
            {
                [XExternalModule] public static void A() { }
                [XExternalModule(HasReflectionEntry = false)] public static void B() { }
            }
            public class Caller
            {
                public void Run()
                {
                    Dep.A();
                    Dep.B();
                    Dep.A();
                }
            }
            """;

        Pass3Result first = Run(source);
        Pass3Result second = Run(source);

        List<string> firstKeys = first.Diagnostics
            .Select(d => $"{d.Code}|{d.Line}|{d.Column}|{d.Message}").ToList();
        List<string> secondKeys = second.Diagnostics
            .Select(d => $"{d.Code}|{d.Line}|{d.Column}|{d.Message}").ToList();

        Assert.Equal(firstKeys, secondKeys);

        // The forcing-callee list inside the caller flag is ordinally sorted.
        CrossModuleNoThrowTable table = first.GetSingleton<CrossModuleNoThrowTable>()!;
        CrossModuleCallerTierFlag flag = Assert.Single(
            table.CallerFlags, f => f.CallerDisplay.Contains(".Run("));
        List<string> sorted = flag.ForcingCallees.OrderBy(s => s, System.StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, flag.ForcingCallees);
    }
}
