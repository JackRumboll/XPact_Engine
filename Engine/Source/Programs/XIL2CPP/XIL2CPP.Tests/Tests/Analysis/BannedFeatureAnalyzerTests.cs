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
/// Tests for <see cref="BannedFeatureAnalyzer"/> (WU-15) per
/// /Documents/XIL2CPP.html Rev 4 Sections 4.1, 5.18-5.20, and Section 12.
/// Each test builds a Pass-1 result from a self-contained fixture, runs Pass 2
/// (no normalizers), then runs JUST the BannedFeatureAnalyzer through
/// <see cref="Pass3Driver"/> and asserts the diagnostic band + the recorded
/// <see cref="BannedFeatureSite"/> emit-metadata.
/// </summary>
public sealed class BannedFeatureAnalyzerTests
{
    private static Pass3Result Run(bool isSimPath, params string[] sources)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(isSimPath, sources);
        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        return Pass3Driver.Run(
            unit,
            new ISemanticAnalyzer[] { new BannedFeatureAnalyzer() });
    }

    private static Pass3Result Run(params string[] sources) => Run(isSimPath: false, sources);

    private static IReadOnlyList<DiagnosticRecord> WithCode(Pass3Result result, string code)
        => result.Diagnostics.Where(d => d.Code == code).ToList();

    // -----------------------------------------------------------------
    // XIL2CPP011 -- dynamic.
    // -----------------------------------------------------------------

    [Fact]
    public void Dynamic_Parameter_EmitsXIL2CPP011()
    {
        Pass3Result result = Run(
            "namespace N; public class M { public void Run(dynamic d) { } }");

        IReadOnlyList<DiagnosticRecord> diags = WithCode(result, DiagnosticCodes.DynamicNotSupported);
        DiagnosticRecord diag = Assert.Single(diags);
        Assert.Equal(XilSeverity.Error, diag.Severity);
        Assert.Equal("TestModule", diag.Module);
        Assert.NotNull(diag.Line);
        Assert.True(result.HasErrors);

        BannedFeatureSite site = Assert.Single(
            result.GetAll<BannedFeatureSite>(),
            s => s.Code == DiagnosticCodes.DynamicNotSupported);
        Assert.Equal("dynamic", site.Detail);
    }

    [Fact]
    public void Dynamic_LocalDeclaration_EmitsXIL2CPP011()
    {
        Pass3Result result = Run(
            "namespace N; public class M { public void Run() { dynamic d = 1; d.ToString(); } }");

        Assert.NotEmpty(WithCode(result, DiagnosticCodes.DynamicNotSupported));
    }

    // -----------------------------------------------------------------
    // XIL2CPP012 -- DllImport / P/Invoke.
    // -----------------------------------------------------------------

    [Fact]
    public void DllImport_EmitsXIL2CPP012()
    {
        Pass3Result result = Run(
            """
            namespace N;
            using System.Runtime.InteropServices;
            public static class Native
            {
                [DllImport("kernel32.dll")]
                public static extern uint GetCurrentThreadId();
            }
            """);

        IReadOnlyList<DiagnosticRecord> diags = WithCode(result, DiagnosticCodes.PInvokeNotSupported);
        DiagnosticRecord diag = Assert.Single(diags);
        Assert.Equal(XilSeverity.Error, diag.Severity);

        BannedFeatureSite site = Assert.Single(
            result.GetAll<BannedFeatureSite>(),
            s => s.Code == DiagnosticCodes.PInvokeNotSupported);
        Assert.Equal("GetCurrentThreadId", site.Detail);
    }

    // -----------------------------------------------------------------
    // XIL2CPP013 -- direct thread creation.
    // -----------------------------------------------------------------

    [Fact]
    public void ThreadCreation_NewThreadAndStartAndTaskRun_EmitsXIL2CPP013()
    {
        Pass3Result result = Run(
            """
            namespace N;
            using System.Threading;
            using System.Threading.Tasks;
            public class M
            {
                public void Run()
                {
                    var t = new Thread(() => { });
                    t.Start();
                    Task.Run(() => { });
                }
            }
            """);

        // new Thread(...), t.Start(), Task.Run(...) -> three XIL2CPP013 sites.
        IReadOnlyList<DiagnosticRecord> diags =
            WithCode(result, DiagnosticCodes.DirectThreadCreationBanned);
        Assert.Equal(3, diags.Count);
        Assert.All(diags, d => Assert.Equal(XilSeverity.Error, d.Severity));
    }

    // -----------------------------------------------------------------
    // XIL2CPP014 -- direct I/O.
    // -----------------------------------------------------------------

    [Fact]
    public void DirectIo_FileStreamParameterType_EmitsXIL2CPP014()
    {
        Pass3Result result = Run(
            """
            namespace N;
            using System.IO;
            public class M
            {
                public string Run(FileStream fs) => File.ReadAllText("x");
            }
            """);

        IReadOnlyList<DiagnosticRecord> diags =
            WithCode(result, DiagnosticCodes.DirectIoNotInBclSurface);
        // FileStream is named at a parameter-TYPE position -> one direct-I/O
        // site. (File.ReadAllText's 'File' is a member-access qualifier, not a
        // type-usage position, so it is not double-counted here.)
        Assert.NotEmpty(diags);
        Assert.All(diags, d => Assert.Equal(XilSeverity.Error, d.Severity));
        Assert.Contains(
            result.GetAll<BannedFeatureSite>(),
            s => s.Code == DiagnosticCodes.DirectIoNotInBclSurface
                 && s.Detail == "System.IO.FileStream");
    }

    // -----------------------------------------------------------------
    // XIL2CPP015 -- params ReadOnlySpan<T>.
    // -----------------------------------------------------------------

    [Fact]
    public void ParamsReadOnlySpan_EmitsXIL2CPP015()
    {
        Pass3Result result = Run(
            """
            namespace N;
            using System;
            public class M
            {
                public int Sum(params ReadOnlySpan<int> xs)
                {
                    int t = 0;
                    foreach (int x in xs) t += x;
                    return t;
                }
            }
            """);

        IReadOnlyList<DiagnosticRecord> diags =
            WithCode(result, DiagnosticCodes.ParamsReadOnlySpanPostMvp);
        DiagnosticRecord diag = Assert.Single(diags);
        Assert.Equal(XilSeverity.Error, diag.Severity);
    }

    // -----------------------------------------------------------------
    // XIL2CPP016 -- static abstract interface members.
    // -----------------------------------------------------------------

    [Fact]
    public void StaticAbstractInterfaceMember_OperatorAndProperty_EmitsXIL2CPP016()
    {
        Pass3Result result = Run(
            """
            namespace N;
            public interface IAddable<T> where T : IAddable<T>
            {
                static abstract T operator +(T a, T b);
                static abstract T Zero { get; }
            }
            """);

        IReadOnlyList<DiagnosticRecord> diags =
            WithCode(result, DiagnosticCodes.StaticAbstractInterfaceMembersPostMvp);
        // operator + (a method) and Zero (a property): two static abstract members.
        Assert.Equal(2, diags.Count);
        Assert.All(diags, d => Assert.Equal(XilSeverity.Error, d.Severity));
    }

    [Fact]
    public void StaticAbstractInterfaceMethod_EmitsXIL2CPP016()
    {
        Pass3Result result = Run(
            """
            namespace N;
            public interface IFactory<T>
            {
                static abstract T Create();
            }
            """);

        DiagnosticRecord diag = Assert.Single(
            WithCode(result, DiagnosticCodes.StaticAbstractInterfaceMembersPostMvp));
        Assert.Equal(XilSeverity.Error, diag.Severity);
    }

    // -----------------------------------------------------------------
    // XIL2CPP019 -- volatile field with platform-variable alignment.
    // -----------------------------------------------------------------

    [Fact]
    public void VolatileIntPtrField_EmitsXIL2CPP019()
    {
        Pass3Result result = Run(
            """
            namespace N;
            using System;
            public class M
            {
                private volatile IntPtr _handle;
                public void Set(IntPtr v) { _handle = v; }
            }
            """);

        IReadOnlyList<DiagnosticRecord> diags =
            WithCode(result, DiagnosticCodes.VolatileFieldAlignmentIncompatible);
        DiagnosticRecord diag = Assert.Single(diags);
        Assert.Equal(XilSeverity.Error, diag.Severity);
    }

    [Fact]
    public void VolatileIntField_DoesNotEmitXIL2CPP019()
    {
        // A volatile int has a fixed 4-byte alignment portable across the
        // target matrix; only pointer-width types are flagged.
        Pass3Result result = Run(
            "namespace N; public class M { private volatile int _flag; }");

        Assert.Empty(WithCode(result, DiagnosticCodes.VolatileFieldAlignmentIncompatible));
    }

    // -----------------------------------------------------------------
    // XIL2CPP010 -- BCL type not in the mapped subset.
    // -----------------------------------------------------------------

    [Fact]
    public void UnmappedBclType_Regex_EmitsXIL2CPP010()
    {
        Pass3Result result = Run(
            """
            namespace N;
            using System.Text.RegularExpressions;
            public class M
            {
                public bool Run(Regex r, string s) => r.IsMatch(s);
            }
            """);

        IReadOnlyList<DiagnosticRecord> diags =
            WithCode(result, DiagnosticCodes.BclTypeNotInMappedSubset);
        Assert.NotEmpty(diags);
        Assert.All(diags, d => Assert.Equal(XilSeverity.Error, d.Severity));
        Assert.Contains(
            result.GetAll<BannedFeatureSite>(),
            s => s.Code == DiagnosticCodes.BclTypeNotInMappedSubset
                 && s.Detail == "System.Text.RegularExpressions.Regex");
    }

    // -----------------------------------------------------------------
    // XIL2CPP061 -- anonymous types.
    // -----------------------------------------------------------------

    [Fact]
    public void AnonymousType_EmitsXIL2CPP061()
    {
        Pass3Result result = Run(
            "namespace N; public class M { public object Run() => new { Actor = \"hero\", Count = 5 }; }");

        IReadOnlyList<DiagnosticRecord> diags =
            WithCode(result, DiagnosticCodes.SimPathAnonymousTypesBanned);
        DiagnosticRecord diag = Assert.Single(diags);
        Assert.Equal(XilSeverity.Error, diag.Severity);

        Assert.Contains(
            result.GetAll<BannedFeatureSite>(),
            s => s.Code == DiagnosticCodes.SimPathAnonymousTypesBanned);
    }

    // -----------------------------------------------------------------
    // XIL2CPP062 -- implicit boxing of value type to object.
    // -----------------------------------------------------------------

    [Fact]
    public void ImplicitBoxing_EmitsXIL2CPP062()
    {
        Pass3Result result = Run(
            "namespace N; public class M { public object Run() { object o = 5; return o; } }");

        IReadOnlyList<DiagnosticRecord> diags =
            WithCode(result, DiagnosticCodes.ImplicitBoxingNotSupported);
        DiagnosticRecord diag = Assert.Single(diags);
        Assert.Equal(XilSeverity.Error, diag.Severity);

        BannedFeatureSite site = Assert.Single(
            result.GetAll<BannedFeatureSite>(),
            s => s.Code == DiagnosticCodes.ImplicitBoxingNotSupported);
        Assert.Equal("Int32", site.Detail);
    }

    [Fact]
    public void ListOfObjectInitializer_DoesNotEmitXIL2CPP062()
    {
        // Boxing value types into an object-typed container is the container
        // analyzer's XIL2CPP073 site (070-073 band); this analyzer must NOT
        // double-flag those elements with XIL2CPP062.
        Pass3Result result = Run(
            """
            namespace N;
            using System.Collections.Generic;
            public class M
            {
                public object Run() { var l = new List<object> { 1, 2, 3 }; return l[0]; }
            }
            """);

        Assert.Empty(WithCode(result, DiagnosticCodes.ImplicitBoxingNotSupported));
    }

    // -----------------------------------------------------------------
    // XIL2CPP060 -- value-type pattern on object.
    // -----------------------------------------------------------------

    [Fact]
    public void ValueTypePatternOnObject_EmitsXIL2CPP060()
    {
        Pass3Result result = Run(
            """
            namespace N;
            public class M
            {
                public string Run(object o) => o switch { int n => "int:" + n, _ => "other" };
            }
            """);

        IReadOnlyList<DiagnosticRecord> diags =
            WithCode(result, DiagnosticCodes.PatternMatchValueTypeRequiresBoxing);
        DiagnosticRecord diag = Assert.Single(diags);
        Assert.Equal(XilSeverity.Error, diag.Severity);

        Assert.Contains(
            result.GetAll<BannedFeatureSite>(),
            s => s.Code == DiagnosticCodes.PatternMatchValueTypeRequiresBoxing
                 && s.Detail == "Int32");
    }

    // -----------------------------------------------------------------
    // XIL2CPP005 -- ref/out XObject-reference parameter.
    // -----------------------------------------------------------------

    [Fact]
    public void RefAndOutXObjectParameter_EmitsXIL2CPP005()
    {
        // The engine XObject root lives in XPact.CoreXObject; AnalyzerHelpers
        // matches the metadata-name + namespace fallback for this locally-
        // declared stand-in (the Phase 6.b binding model -- see AnalyzerHelpers).
        Pass3Result result = Run(
            "namespace XPact.CoreXObject; public abstract class XObject { }",
            """
            namespace N;
            using XPact.CoreXObject;
            public class Actor : XObject { }
            public class M
            {
                public void Take(ref Actor a) { }
                public void Make(out Actor a) { a = null!; }
            }
            """);

        IReadOnlyList<DiagnosticRecord> diags =
            WithCode(result, DiagnosticCodes.RefOutXObjectParameterUnsupported);
        // ref Actor and out Actor -> two banned parameters.
        Assert.Equal(2, diags.Count);
        Assert.All(diags, d => Assert.Equal(XilSeverity.Error, d.Severity));
    }

    [Fact]
    public void RefValueTypeParameter_DoesNotEmitXIL2CPP005()
    {
        // ref/out of a value type (or non-XObject reference type) is supported.
        Pass3Result result = Run(
            "namespace N; public class M { public void Run(ref int x) { x = 1; } }");

        Assert.Empty(WithCode(result, DiagnosticCodes.RefOutXObjectParameterUnsupported));
    }

    // -----------------------------------------------------------------
    // Negative: clean, fully-supported source emits nothing.
    // -----------------------------------------------------------------

    [Fact]
    public void CleanSupportedSource_EmitsNoDiagnostics()
    {
        Pass3Result result = Run(
            """
            namespace N;
            using System.Collections.Generic;
            public class M
            {
                private readonly List<int> _items = new();
                public int Run(int a, int b) => a + b;
                public void Add(int x) => _items.Add(x);
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.False(result.HasErrors);
        Assert.Empty(result.GetAll<BannedFeatureSite>());
    }

    // -----------------------------------------------------------------
    // Determinism: two runs over identical input produce identical
    // diagnostic + site sequences.
    // -----------------------------------------------------------------

    [Fact]
    public void RepeatedRuns_AreDeterministic()
    {
        const string source =
            """
            namespace N;
            using System;
            using System.Runtime.InteropServices;
            public class M
            {
                public object Run(dynamic d)
                {
                    object o = 5;
                    return o;
                }
            }
            public static class Native
            {
                [DllImport("k.dll")] public static extern int F();
            }
            """;

        Pass3Result first = Run(source);
        Pass3Result second = Run(source);

        string[] firstCodes = first.Diagnostics
            .Select(d => $"{d.Code}:{d.Line}:{d.Column}").ToArray();
        string[] secondCodes = second.Diagnostics
            .Select(d => $"{d.Code}:{d.Line}:{d.Column}").ToArray();

        Assert.Equal(firstCodes, secondCodes);
    }
}
