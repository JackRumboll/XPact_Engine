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
/// Tests for <see cref="AsyncStateMachineAnalyzer"/> per
/// /Documents/XIL2CPP.html Rev 4 Sections 5.9 + 7.5. Proves: every async
/// method is recorded as an <see cref="AsyncSite"/> with the
/// <c>XObject*</c>-live-across-await capture set (the state-machine
/// XGCRootSpan view); non-sim-path async is recorded but NOT diagnosed;
/// sim-path async declarations emit XIL2CPP044 + (for Task-like returns /
/// await foreach) XIL2CPP048; and the analyzer is deterministic. The
/// expression-level Task surface (Task.Result / Task.Wait) is intentionally
/// NOT exercised here -- that 048 surface is owned by the sim-path
/// banned-API analyzer; this analyzer owns only the state-machine declaration
/// + await-foreach sites.
/// </summary>
public sealed class AsyncStateMachineAnalyzerTests
{
    /// <summary>
    /// A minimal stand-in <c>XPact.CoreXObject.XObject</c> base type so the
    /// XObject-derivation metadata-name fallback in
    /// <see cref="AnalyzerHelpers"/> binds without the real engine BCL.
    /// </summary>
    private const string XObjectStub =
        "namespace XPact.CoreXObject { public abstract class XObject { } }\n";

    private static Pass3Result Run(bool isSimPath, params string[] sources)
    {
        // The XObject stub is a block-namespace declaration, so it must live
        // in its OWN parsed file -- a file-scoped namespace (the convention the
        // fixtures use) must be the only namespace form in its file.
        string[] withStub = new string[sources.Length + 1];
        withStub[0] = XObjectStub;
        for (int i = 0; i < sources.Length; i++)
        {
            withStub[i + 1] = sources[i];
        }

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(isSimPath, withStub);

        // No binder errors should slip through unnoticed in a fixture; assert
        // the source binds cleanly so a fixture typo never masquerades as an
        // analyzer result.
        Assert.DoesNotContain(
            pass1.Compilation.GetDiagnostics(),
            d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        return Pass3Driver.Run(
            unit, new ISemanticAnalyzer[] { new AsyncStateMachineAnalyzer() });
    }

    private static IReadOnlyList<DiagnosticRecord> CodeRecords(Pass3Result result, string code)
        => result.Diagnostics.Where(d => d.Code == code).ToList();

    // -----------------------------------------------------------------
    // Non-sim-path: recorded, never diagnosed.
    // -----------------------------------------------------------------

    [Fact]
    public void NonSimPath_AsyncMethod_IsRecorded_NoDiagnostic()
    {
        const string src = @"
namespace M;
using System.Threading.Tasks;
public class A
{
    public async Task RunAsync()
    {
        await Task.CompletedTask;
    }
}";

        Pass3Result result = Run(isSimPath: false, src);

        IReadOnlyList<AsyncSite> sites = result.GetAll<AsyncSite>();
        Assert.Single(sites);
        Assert.EndsWith(".RunAsync()", sites[0].MethodDisplay);
        Assert.Equal("RunAsync", sites[0].MethodMetadataName);
        Assert.True(sites[0].ReturnsTaskLike);
        Assert.False(sites[0].IsAsyncIterator);

        // Non-sim-path async is in the MVP: no ban diagnostics.
        Assert.Empty(result.Diagnostics);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void NonSimPath_NoAsync_RecordsNothing()
    {
        const string src = @"
namespace M;
public class A { public int F() => 1; }";

        Pass3Result result = Run(isSimPath: false, src);

        Assert.Empty(result.GetAll<AsyncSite>());
        Assert.Empty(result.Diagnostics);
    }

    // -----------------------------------------------------------------
    // Cross-await XObject capture (the XGCRootSpan view).
    // -----------------------------------------------------------------

    [Fact]
    public void CrossAwait_XObjectLocal_IsCapturedAsRoot()
    {
        const string src = @"
namespace M;
using System.Threading.Tasks;
using XPact.CoreXObject;
public sealed class Widget : XObject { }
public class A
{
    public async Task RunAsync()
    {
        Widget w = new Widget();
        await Task.CompletedTask;
        System.GC.KeepAlive(w);
    }
}";

        Pass3Result result = Run(isSimPath: false, src);

        AsyncSite site = Assert.Single(result.GetAll<AsyncSite>());
        AsyncCapturedRoot root = Assert.Single(site.CapturedRoots);
        Assert.Equal("w", root.VariableDisplay);
        Assert.Equal(Microsoft.CodeAnalysis.SymbolKind.Local, root.VariableKind);
        Assert.Contains("Widget", root.TypeDisplay);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void CrossAwait_XObjectParameter_IsCapturedAsRoot()
    {
        const string src = @"
namespace M;
using System.Threading.Tasks;
using XPact.CoreXObject;
public sealed class Widget : XObject { }
public class A
{
    public async Task RunAsync(Widget w)
    {
        await Task.CompletedTask;
        System.GC.KeepAlive(w);
    }
}";

        Pass3Result result = Run(isSimPath: false, src);

        AsyncSite site = Assert.Single(result.GetAll<AsyncSite>());
        AsyncCapturedRoot root = Assert.Single(site.CapturedRoots);
        Assert.Equal("w", root.VariableDisplay);
        Assert.Equal(Microsoft.CodeAnalysis.SymbolKind.Parameter, root.VariableKind);
    }

    [Fact]
    public void NonXObjectLocal_AcrossAwait_IsNotCaptured()
    {
        const string src = @"
namespace M;
using System.Threading.Tasks;
public class A
{
    public async Task RunAsync()
    {
        int n = 5;
        await Task.CompletedTask;
        System.GC.KeepAlive(n);
    }
}";

        Pass3Result result = Run(isSimPath: false, src);

        AsyncSite site = Assert.Single(result.GetAll<AsyncSite>());
        Assert.Empty(site.CapturedRoots);
    }

    [Fact]
    public void XObjectLocal_NotLiveAcrossAwait_IsNotCaptured()
    {
        // The XObject is created AND consumed before the await; nothing of it
        // flows into the post-await region, so it is not a state-machine root.
        const string src = @"
namespace M;
using System.Threading.Tasks;
using XPact.CoreXObject;
public sealed class Widget : XObject { }
public class A
{
    public async Task RunAsync()
    {
        Widget w = new Widget();
        System.GC.KeepAlive(w);
        await Task.CompletedTask;
    }
}";

        Pass3Result result = Run(isSimPath: false, src);

        AsyncSite site = Assert.Single(result.GetAll<AsyncSite>());
        Assert.Empty(site.CapturedRoots);
    }

    // -----------------------------------------------------------------
    // Sim-path: async banned (XIL2CPP044 + XIL2CPP048).
    // -----------------------------------------------------------------

    [Fact]
    public void SimPath_AsyncTaskMethod_Emits044_And048()
    {
        const string src = @"
namespace M;
using System.Threading.Tasks;
public class A
{
    public async Task RunAsync()
    {
        await Task.CompletedTask;
    }
}";

        Pass3Result result = Run(isSimPath: true, src);

        // Still recorded (the state-machine view is recorded on all modules).
        Assert.Single(result.GetAll<AsyncSite>());

        DiagnosticRecord d044 = Assert.Single(CodeRecords(result, DiagnosticCodes.SimPathAsyncAwaitBanned));
        Assert.Equal(XilSeverity.Error, d044.Severity);
        Assert.Equal("TestModule", d044.Module);
        Assert.NotNull(d044.Line);

        DiagnosticRecord d048 = Assert.Single(CodeRecords(result, DiagnosticCodes.SimPathTaskBanned));
        Assert.Equal(XilSeverity.Error, d048.Severity);

        Assert.True(result.HasErrors);
    }

    [Fact]
    public void SimPath_AsyncVoid_Emits044_ButNot048()
    {
        // async void is NOT Task-like, so only the async/await ban (044)
        // fires at the declaration site, not the Task-surface ban (048).
        const string src = @"
namespace M;
using System.Threading.Tasks;
public class A
{
    public async void Handler()
    {
        await Task.CompletedTask;
    }
}";

        Pass3Result result = Run(isSimPath: true, src);

        Assert.Single(CodeRecords(result, DiagnosticCodes.SimPathAsyncAwaitBanned));
        Assert.Empty(CodeRecords(result, DiagnosticCodes.SimPathTaskBanned));
    }

    [Fact]
    public void SimPath_AwaitForeach_Emits048()
    {
        const string src = @"
namespace M;
using System.Threading.Tasks;
using System.Collections.Generic;
public class A
{
    public async Task RunAsync(IAsyncEnumerable<int> items)
    {
        await foreach (int i in items)
        {
            System.GC.KeepAlive(i);
        }
    }
}";

        Pass3Result result = Run(isSimPath: true, src);

        // The async method declaration (Task return) emits 044 + 048, and the
        // await-foreach statement emits a second 048 at its own site.
        Assert.Single(CodeRecords(result, DiagnosticCodes.SimPathAsyncAwaitBanned));
        IReadOnlyList<DiagnosticRecord> task = CodeRecords(result, DiagnosticCodes.SimPathTaskBanned);
        Assert.Equal(2, task.Count);
    }

    [Fact]
    public void SimPath_AsyncIterator_Emits044_And048()
    {
        const string src = @"
namespace M;
using System.Threading.Tasks;
using System.Collections.Generic;
public class A
{
    public async IAsyncEnumerable<int> StreamAsync()
    {
        await Task.CompletedTask;
        yield return 1;
    }
}";

        Pass3Result result = Run(isSimPath: true, src);

        AsyncSite site = Assert.Single(result.GetAll<AsyncSite>());
        Assert.True(site.IsAsyncIterator);
        Assert.True(site.ReturnsTaskLike);

        Assert.Single(CodeRecords(result, DiagnosticCodes.SimPathAsyncAwaitBanned));
        Assert.Single(CodeRecords(result, DiagnosticCodes.SimPathTaskBanned));
    }

    [Fact]
    public void SimPath_NonAsyncMethod_NoDiagnostic()
    {
        const string src = @"
namespace M;
public class A { public int F() => 1; }";

        Pass3Result result = Run(isSimPath: true, src);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.GetAll<AsyncSite>());
    }

    // -----------------------------------------------------------------
    // Async lambda + async local function are state-machine sites too.
    // -----------------------------------------------------------------

    [Fact]
    public void AsyncLambda_IsRecorded_AndBannedOnSimPath()
    {
        const string src = @"
namespace M;
using System;
using System.Threading.Tasks;
public class A
{
    public void Setup()
    {
        Func<Task> f = async () => { await Task.CompletedTask; };
        System.GC.KeepAlive(f);
    }
}";

        Pass3Result simResult = Run(isSimPath: true, src);
        Assert.Single(simResult.GetAll<AsyncSite>());
        Assert.Single(CodeRecords(simResult, DiagnosticCodes.SimPathAsyncAwaitBanned));

        Pass3Result nonSim = Run(isSimPath: false, src);
        Assert.Single(nonSim.GetAll<AsyncSite>());
        Assert.Empty(nonSim.Diagnostics);
    }

    [Fact]
    public void AsyncLocalFunction_IsRecorded()
    {
        const string src = @"
namespace M;
using System.Threading.Tasks;
public class A
{
    public void Outer()
    {
        async Task LocalAsync() { await Task.CompletedTask; }
        System.GC.KeepAlive((System.Func<Task>)LocalAsync);
    }
}";

        Pass3Result result = Run(isSimPath: false, src);

        AsyncSite site = Assert.Single(result.GetAll<AsyncSite>());
        Assert.Equal("LocalAsync", site.MethodMetadataName);
        Assert.True(site.ReturnsTaskLike);
        Assert.Empty(result.Diagnostics);
    }

    // -----------------------------------------------------------------
    // Determinism: identical input -> identical recorded order + diagnostics.
    // -----------------------------------------------------------------

    [Fact]
    public void Deterministic_AcrossRuns()
    {
        const string src = @"
namespace M;
using System.Threading.Tasks;
using XPact.CoreXObject;
public sealed class Widget : XObject { }
public class A
{
    public async Task OneAsync(Widget a)
    {
        await Task.CompletedTask;
        System.GC.KeepAlive(a);
    }
    public async Task TwoAsync(Widget b)
    {
        await Task.CompletedTask;
        System.GC.KeepAlive(b);
    }
}";

        Pass3Result r1 = Run(isSimPath: true, src);
        Pass3Result r2 = Run(isSimPath: true, src);

        List<string> sites1 = r1.GetAll<AsyncSite>().Select(s => s.MethodMetadataName).ToList();
        List<string> sites2 = r2.GetAll<AsyncSite>().Select(s => s.MethodMetadataName).ToList();
        Assert.Equal(sites1, sites2);

        List<string> diag1 = r1.Diagnostics.Select(d => $"{d.Code}:{d.Line}:{d.Column}").ToList();
        List<string> diag2 = r2.Diagnostics.Select(d => $"{d.Code}:{d.Line}:{d.Column}").ToList();
        Assert.Equal(diag1, diag2);
    }
}
