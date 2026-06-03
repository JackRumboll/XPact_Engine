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
/// Tests for <see cref="AsyncStateMachineAnalyzer"/> per
/// /Documents/XIL2CPP.html Rev 4 Section 5.9. Proves: every async method
/// (method / lambda / local function) is recorded as an <see cref="AsyncSite"/>
/// with the <c>XObject*</c>-live-across-await capture set (the state-machine
/// XGCRootSpan view), on both sim-path and non-sim-path modules, and that this
/// analyzer emits NO diagnostics -- the sim-path async ban (XIL2CPP044 /
/// XIL2CPP048) is owned solely by <see cref="SimPathBannedApiAnalyzer"/>, so
/// it is not exercised here.
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
        string[] withStub = new string[sources.Length + 1];
        withStub[0] = XObjectStub;
        for (int i = 0; i < sources.Length; i++)
        {
            withStub[i + 1] = sources[i];
        }

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(isSimPath, withStub);

        // A fixture typo must never masquerade as an analyzer result.
        Assert.DoesNotContain(
            pass1.Compilation.GetDiagnostics(),
            d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        return Pass3Driver.Run(
            unit, new ISemanticAnalyzer[] { new AsyncStateMachineAnalyzer() });
    }

    // -----------------------------------------------------------------
    // Recording (all modules); this analyzer never diagnoses.
    // -----------------------------------------------------------------

    [Fact]
    public void AsyncMethod_IsRecorded_NoDiagnostic()
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

        AsyncSite site = Assert.Single(result.GetAll<AsyncSite>());
        Assert.EndsWith(".RunAsync()", site.MethodDisplay);
        Assert.Equal("RunAsync", site.MethodMetadataName);
        Assert.True(site.ReturnsTaskLike);
        Assert.False(site.IsAsyncIterator);
        Assert.Empty(result.Diagnostics);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void NoAsync_RecordsNothing()
    {
        const string src = @"
namespace M;
public class A { public int F() => 1; }";

        Pass3Result result = Run(isSimPath: false, src);

        Assert.Empty(result.GetAll<AsyncSite>());
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void SimPathAsync_StillRecorded_ButThisAnalyzerDoesNotDiagnose()
    {
        // On a sim-path module the async ban is owned by
        // SimPathBannedApiAnalyzer; this analyzer still RECORDS the
        // state-machine site but emits no diagnostic of its own.
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

        Assert.Single(result.GetAll<AsyncSite>());
        Assert.Empty(result.Diagnostics);
        Assert.False(result.HasErrors);
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
    // Async iterator / lambda / local function are state-machine sites too.
    // -----------------------------------------------------------------

    [Fact]
    public void AsyncIterator_IsRecorded()
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

        Pass3Result result = Run(isSimPath: false, src);

        AsyncSite site = Assert.Single(result.GetAll<AsyncSite>());
        Assert.True(site.IsAsyncIterator);
        Assert.True(site.ReturnsTaskLike);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void AsyncVoid_IsRecorded_NotTaskLike()
    {
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

        Pass3Result result = Run(isSimPath: false, src);

        AsyncSite site = Assert.Single(result.GetAll<AsyncSite>());
        Assert.False(site.ReturnsTaskLike);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void AsyncLambda_IsRecorded()
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

        Pass3Result result = Run(isSimPath: false, src);
        Assert.Single(result.GetAll<AsyncSite>());
        Assert.Empty(result.Diagnostics);
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
    // Determinism: identical input -> identical recorded order.
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

        Pass3Result r1 = Run(isSimPath: false, src);
        Pass3Result r2 = Run(isSimPath: false, src);

        List<string> sites1 = r1.GetAll<AsyncSite>().Select(s => s.MethodMetadataName).ToList();
        List<string> sites2 = r2.GetAll<AsyncSite>().Select(s => s.MethodMetadataName).ToList();
        Assert.Equal(sites1, sites2);
        Assert.Equal(new[] { "OneAsync", "TwoAsync" }, sites1);
    }
}
