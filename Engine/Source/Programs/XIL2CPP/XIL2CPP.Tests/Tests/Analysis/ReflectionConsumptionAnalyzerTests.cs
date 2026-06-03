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
/// Tests for <see cref="ReflectionConsumptionAnalyzer"/> (WU-22) per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.11. Verifies the read-side
/// reflection sites are recorded (<c>typeof</c> closed / unbound,
/// <c>obj.GetType()</c>) and the banned dynamic / write-side reflection
/// surface is diagnosed (<c>XIL2CPP050</c>..<c>054</c>).
/// </summary>
public sealed class ReflectionConsumptionAnalyzerTests
{
    private static Pass3Result Run(params string[] sources)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(isSimPath: false, sources);
        NormalizedUnit unit = Pass2Driver.Run(pass1);
        return Pass3Driver.Run(
            unit,
            new ISemanticAnalyzer[] { new ReflectionConsumptionAnalyzer() });
    }

    private static IReadOnlyList<DiagnosticRecord> DiagnosticsWithCode(
        Pass3Result result, string code)
        => result.Diagnostics.Where(d => d.Code == code).ToList();

    // -----------------------------------------------------------------
    // typeof(closed) -> recorded ReflectionSite (TypeOf).
    // -----------------------------------------------------------------

    [Fact]
    public void TypeOf_ClosedType_RecordsTypeOfSite_NoDiagnostics()
    {
        const string src = @"
namespace M;
public class A
{
    public System.Type F() => typeof(A);
}";
        Pass3Result result = Run(src);

        Assert.Empty(result.Diagnostics);

        IReadOnlyList<ReflectionSite> sites = result.GetAll<ReflectionSite>();
        ReflectionSite site = Assert.Single(sites);
        Assert.Equal(ReflectionSiteKind.TypeOf, site.Kind);
        Assert.Equal("global::M.A", site.TargetTypeDisplay);
    }

    [Fact]
    public void TypeOf_ClosedGeneric_RecordsTypeOfSite_NoDiagnostics()
    {
        const string src = @"
namespace M;
public class A
{
    public System.Type F() => typeof(System.Collections.Generic.List<int>);
}";
        Pass3Result result = Run(src);

        Assert.Empty(result.Diagnostics);
        ReflectionSite site = Assert.Single(result.GetAll<ReflectionSite>());
        Assert.Equal(ReflectionSiteKind.TypeOf, site.Kind);
        Assert.Contains("List", site.TargetTypeDisplay);
    }

    [Fact]
    public void TypeOf_UnboundGenericDefinition_RecordsTypeOfSite_NoDiagnostics()
    {
        // typeof(List<>) resolves to the generic-type-definition FClass via
        // XReflectionRuntime::FindClass -- it is NOT an unresolved
        // open-generic site, so no XIL2CPP054.
        const string src = @"
namespace M;
public class A
{
    public System.Type F() => typeof(System.Collections.Generic.List<>);
}";
        Pass3Result result = Run(src);

        Assert.Empty(DiagnosticsWithCode(result, DiagnosticCodes.TypeofOpenGenericUnresolvable));
        ReflectionSite site = Assert.Single(result.GetAll<ReflectionSite>());
        Assert.Equal(ReflectionSiteKind.TypeOf, site.Kind);
    }

    // -----------------------------------------------------------------
    // typeof(open-generic) -> XIL2CPP054.
    // -----------------------------------------------------------------

    [Fact]
    public void TypeOf_BareTypeParameter_EmitsXil2Cpp054_NoSite()
    {
        const string src = @"
namespace M;
public class A
{
    public System.Type F<T>() => typeof(T);
}";
        Pass3Result result = Run(src);

        DiagnosticRecord diag = Assert.Single(
            DiagnosticsWithCode(result, DiagnosticCodes.TypeofOpenGenericUnresolvable));
        Assert.Equal(XilSeverity.Error, diag.Severity);
        Assert.True(result.HasErrors);

        // No ReflectionSite recorded for the unresolved open-generic site.
        Assert.Empty(result.GetAll<ReflectionSite>());
    }

    [Fact]
    public void TypeOf_GenericContainingTypeParameter_EmitsXil2Cpp054()
    {
        // typeof(List<T>) where T is unsubstituted -> cannot resolve.
        const string src = @"
namespace M;
public class A
{
    public System.Type F<T>()
        => typeof(System.Collections.Generic.List<T>);
}";
        Pass3Result result = Run(src);

        Assert.Single(DiagnosticsWithCode(result, DiagnosticCodes.TypeofOpenGenericUnresolvable));
        Assert.Empty(result.GetAll<ReflectionSite>());
    }

    [Fact]
    public void TypeOf_TypeParameterArray_EmitsXil2Cpp054()
    {
        const string src = @"
namespace M;
public class A
{
    public System.Type F<T>() => typeof(T[]);
}";
        Pass3Result result = Run(src);

        Assert.Single(DiagnosticsWithCode(result, DiagnosticCodes.TypeofOpenGenericUnresolvable));
        Assert.Empty(result.GetAll<ReflectionSite>());
    }

    [Fact]
    public void TypeOf_ClassTypeParameter_EmitsXil2Cpp054()
    {
        // T is a type parameter of the containing class.
        const string src = @"
namespace M;
public class A<T>
{
    public System.Type F() => typeof(T);
}";
        Pass3Result result = Run(src);

        Assert.Single(DiagnosticsWithCode(result, DiagnosticCodes.TypeofOpenGenericUnresolvable));
    }

    // -----------------------------------------------------------------
    // obj.GetType() -> recorded ReflectionSite (GetType).
    // -----------------------------------------------------------------

    [Fact]
    public void GetType_OnObject_RecordsGetTypeSite_NoDiagnostics()
    {
        const string src = @"
namespace M;
public class A
{
    public System.Type F(object o) => o.GetType();
}";
        Pass3Result result = Run(src);

        Assert.Empty(result.Diagnostics);
        ReflectionSite site = Assert.Single(result.GetAll<ReflectionSite>());
        Assert.Equal(ReflectionSiteKind.GetType, site.Kind);
        Assert.Null(site.TargetTypeDisplay);
    }

    [Fact]
    public void GetType_OnReferenceTyped_RecordsGetTypeSite()
    {
        const string src = @"
namespace M;
public class A
{
    public System.Type F(A a) => a.GetType();
}";
        Pass3Result result = Run(src);

        Assert.Empty(result.Diagnostics);
        ReflectionSite site = Assert.Single(result.GetAll<ReflectionSite>());
        Assert.Equal(ReflectionSiteKind.GetType, site.Kind);
    }

    // -----------------------------------------------------------------
    // Type.GetMethods() / Type.InvokeMember() / MethodInfo.Invoke() -> 050.
    // -----------------------------------------------------------------

    [Fact]
    public void TypeGetMethods_EmitsXil2Cpp050()
    {
        const string src = @"
namespace M;
public class A
{
    public void F()
    {
        System.Reflection.MethodInfo[] ms = typeof(A).GetMethods();
    }
}";
        Pass3Result result = Run(src);

        DiagnosticRecord diag = Assert.Single(
            DiagnosticsWithCode(result, DiagnosticCodes.DynamicReflectionInvocationPostMvp));
        Assert.Equal(XilSeverity.Error, diag.Severity);

        // typeof(A) is still recorded as a closed TypeOf site.
        Assert.Single(result.GetAll<ReflectionSite>());
    }

    [Fact]
    public void MethodInfoInvoke_EmitsXil2Cpp050()
    {
        const string src = @"
namespace M;
public class A
{
    public void F(System.Reflection.MethodInfo mi, object target)
    {
        object r = mi.Invoke(target, null);
    }
}";
        Pass3Result result = Run(src);

        Assert.Single(
            DiagnosticsWithCode(result, DiagnosticCodes.DynamicReflectionInvocationPostMvp));
    }

    // -----------------------------------------------------------------
    // Activator.CreateInstance -> XIL2CPP051.
    // -----------------------------------------------------------------

    [Fact]
    public void ActivatorCreateInstance_EmitsXil2Cpp051()
    {
        const string src = @"
namespace M;
public class A
{
    public object F() => System.Activator.CreateInstance(typeof(A));
}";
        Pass3Result result = Run(src);

        DiagnosticRecord diag = Assert.Single(
            DiagnosticsWithCode(result, DiagnosticCodes.ActivatorCreateInstanceBanned));
        Assert.Equal(XilSeverity.Error, diag.Severity);

        // typeof(A) inside the call is still recorded.
        Assert.Single(result.GetAll<ReflectionSite>());
    }

    // -----------------------------------------------------------------
    // System.Reflection.Emit -> XIL2CPP052.
    // -----------------------------------------------------------------

    [Fact]
    public void ReflectionEmit_EmitsXil2Cpp052()
    {
        const string src = @"
namespace M;
public class A
{
    public void F(System.Reflection.Emit.ILGenerator il)
    {
        il.Emit(System.Reflection.Emit.OpCodes.Nop);
    }
}";
        Pass3Result result = Run(src);

        Assert.NotEmpty(DiagnosticsWithCode(result, DiagnosticCodes.ReflectionEmitNotSupported));
        Assert.All(
            DiagnosticsWithCode(result, DiagnosticCodes.ReflectionEmitNotSupported),
            d => Assert.Equal(XilSeverity.Error, d.Severity));
    }

    // -----------------------------------------------------------------
    // Type.MakeGenericType / MethodInfo.MakeGenericMethod -> XIL2CPP053.
    // -----------------------------------------------------------------

    [Fact]
    public void TypeMakeGenericType_EmitsXil2Cpp053()
    {
        const string src = @"
namespace M;
public class A
{
    public System.Type F(System.Type def, System.Type arg)
        => def.MakeGenericType(arg);
}";
        Pass3Result result = Run(src);

        DiagnosticRecord diag = Assert.Single(
            DiagnosticsWithCode(result, DiagnosticCodes.MakeGenericTypeNotSupported));
        Assert.Equal(XilSeverity.Error, diag.Severity);
    }

    [Fact]
    public void MethodInfoMakeGenericMethod_EmitsXil2Cpp053()
    {
        const string src = @"
namespace M;
public class A
{
    public System.Reflection.MethodInfo F(System.Reflection.MethodInfo mi, System.Type arg)
        => mi.MakeGenericMethod(arg);
}";
        Pass3Result result = Run(src);

        Assert.Single(DiagnosticsWithCode(result, DiagnosticCodes.MakeGenericTypeNotSupported));
    }

    // -----------------------------------------------------------------
    // Negative + cross-cutting cases.
    // -----------------------------------------------------------------

    [Fact]
    public void NoReflection_RecordsNothing_NoDiagnostics()
    {
        const string src = @"
namespace M;
public class A
{
    public int F() => 1 + 2;
}";
        Pass3Result result = Run(src);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.GetAll<ReflectionSite>());
    }

    [Fact]
    public void SpanAnchoring_IsFileLineColumn()
    {
        const string src = "namespace M; public class A { public System.Type F() => typeof(A); }";
        Pass3Result result = Run(src);

        ReflectionSite site = Assert.Single(result.GetAll<ReflectionSite>());
        Assert.Equal("Source0.cs", site.Span.File);
        Assert.Equal(1, site.Span.StartLine);
        Assert.True(site.Span.StartColumn >= 1);
    }

    [Fact]
    public void Determinism_StableOrderAcrossRuns()
    {
        const string src = @"
namespace M;
public class A
{
    public System.Type F(object o)
    {
        System.Type t1 = typeof(A);
        System.Type t2 = o.GetType();
        System.Type t3 = typeof(int);
        return t1;
    }
}";
        Pass3Result first = Run(src);
        Pass3Result second = Run(src);

        IReadOnlyList<ReflectionSite> a = first.GetAll<ReflectionSite>();
        IReadOnlyList<ReflectionSite> b = second.GetAll<ReflectionSite>();

        Assert.Equal(3, a.Count);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Kind, b[i].Kind);
            Assert.Equal(a[i].TargetTypeDisplay, b[i].TargetTypeDisplay);
            Assert.Equal(a[i].Span, b[i].Span);
        }

        // Source-order: typeof(A) first, GetType() second, typeof(int) third.
        Assert.Equal(ReflectionSiteKind.TypeOf, a[0].Kind);
        Assert.Equal(ReflectionSiteKind.GetType, a[1].Kind);
        Assert.Equal(ReflectionSiteKind.TypeOf, a[2].Kind);
    }

    [Fact]
    public void MultipleBannedSurfaces_AllDiagnosed()
    {
        const string src = @"
namespace M;
public class A
{
    public void F(System.Reflection.MethodInfo mi, object target)
    {
        object x = System.Activator.CreateInstance(typeof(A));
        System.Reflection.MethodInfo[] ms = typeof(A).GetMethods();
        object r = mi.Invoke(target, null);
    }
}";
        Pass3Result result = Run(src);

        Assert.Single(DiagnosticsWithCode(result, DiagnosticCodes.ActivatorCreateInstanceBanned));
        Assert.Equal(
            2,
            DiagnosticsWithCode(result, DiagnosticCodes.DynamicReflectionInvocationPostMvp).Count);
    }
}
