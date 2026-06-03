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
/// Tests for <see cref="ContainerAnalyzer"/> (WU-18) per
/// /Documents/XIL2CPP.html Rev 4 Sections 5.7 (container emit) + 6.2
/// (container roots: XGCRootSpan emit) + Section 12 (codes XIL2CPP070-073).
/// Proves the analyzer records a strong <see cref="ContainerSite"/> for an
/// XObject-element container, records no site for a pure-value container,
/// records a conservative site + emits XIL2CPP070 for an object-typed
/// container (error on sim-path), emits XIL2CPP073 for a value-typed entry in
/// an object container, and emits XIL2CPP071 for a by-value XGC-aware
/// container parameter. The fixtures declare local stand-ins for the engine
/// <c>XObject</c> / <c>XActor</c> / <c>FName</c> types because the Phase 6.b
/// test BCL set has no engine reference DLL.
/// </summary>
public sealed class ContainerAnalyzerTests
{
    // Engine-type stand-ins. AnalyzerHelpers.IsXObjectDerived recognises a
    // class deriving from a type whose metadata name is "XObject" in namespace
    // "XPact.CoreXObject" even without the curated BCL ref.
    private const string EngineStubs = """
        namespace XPact.CoreXObject { public abstract class XObject { } }
        namespace Engine
        {
            public sealed class XActor : XPact.CoreXObject.XObject { }
            public readonly struct FName { }
        }
        """;

    private static Pass3Result Run(bool isSimPath, string source)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(
            isSimPath, EngineStubs, source);
        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        return Pass3Driver.Run(unit, new ISemanticAnalyzer[] { new ContainerAnalyzer() });
    }

    private static Pass3Result Run(string source) => Run(isSimPath: false, source);

    // ---------------------------------------------------------------
    // Strong sites.
    // ---------------------------------------------------------------

    [Fact]
    public void ListOfXActor_RecordsStrongSite_WithTArrayXPtrSignature()
    {
        Pass3Result result = Run(
            "namespace M { using Engine; public class Holder { private System.Collections.Generic.List<XActor> _actors; } }");

        ContainerSite site = Assert.Single(result.GetAll<ContainerSite>());
        Assert.Equal(ContainerKind.List, site.Kind);
        Assert.Equal(ContainerGcKind.Strong, site.GcKind);
        Assert.Equal(ContainerStorage.Field, site.Storage);
        Assert.Equal("XActor", site.ElementType);
        Assert.Null(site.KeyType);
        Assert.Equal("TArray<XPtr<XActor>>", site.EmitSignature);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void DictionaryOfFNameXActor_RecordsStrongSite_OverValues()
    {
        Pass3Result result = Run(
            "namespace M { using Engine; public class Holder { private System.Collections.Generic.Dictionary<FName, XActor> _byName; } }");

        ContainerSite site = Assert.Single(result.GetAll<ContainerSite>());
        Assert.Equal(ContainerKind.Dictionary, site.Kind);
        Assert.Equal(ContainerGcKind.Strong, site.GcKind);
        Assert.Equal("FName", site.KeyType);
        Assert.Equal("XActor", site.ValueType);
        Assert.Equal("TMap<FName, XPtr<XActor>>", site.EmitSignature);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void HashSetOfXActor_RecordsStrongSite_WithTSetSignature()
    {
        Pass3Result result = Run(
            "namespace M { using Engine; public class Holder { private System.Collections.Generic.HashSet<XActor> _set; } }");

        ContainerSite site = Assert.Single(result.GetAll<ContainerSite>());
        Assert.Equal(ContainerKind.HashSet, site.Kind);
        Assert.Equal(ContainerGcKind.Strong, site.GcKind);
        Assert.Equal("XActor", site.ElementType);
        Assert.Equal("TSet<XPtr<XActor>>", site.EmitSignature);

        Assert.Empty(result.Diagnostics);
    }

    // ---------------------------------------------------------------
    // No site for pure-value containers.
    // ---------------------------------------------------------------

    [Fact]
    public void ListOfInt_RecordsNoSite_AndNoDiagnostics()
    {
        Pass3Result result = Run(
            "namespace M { public class Holder { private System.Collections.Generic.List<int> _nums; } }");

        Assert.Empty(result.GetAll<ContainerSite>());
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void DictionaryOfIntInt_RecordsNoSite()
    {
        Pass3Result result = Run(
            "namespace M { public class Holder { private System.Collections.Generic.Dictionary<int,int> _m; } }");

        Assert.Empty(result.GetAll<ContainerSite>());
        Assert.Empty(result.Diagnostics);
    }

    // ---------------------------------------------------------------
    // Conservative (object-typed) containers: XIL2CPP070.
    // ---------------------------------------------------------------

    [Fact]
    public void ListOfObject_RecordsConservativeSite_AndWarnsXIL2CPP070()
    {
        Pass3Result result = Run(
            "namespace M { public class Holder { private System.Collections.Generic.List<object> _things; } }");

        ContainerSite site = Assert.Single(result.GetAll<ContainerSite>());
        Assert.Equal(ContainerGcKind.Conservative, site.GcKind);
        Assert.Equal("TArray<void*>", site.EmitSignature);

        DiagnosticRecord diag = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.ConservativeXGCRootSpan, diag.Code);
        Assert.Equal(XilSeverity.Warning, diag.Severity);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void ListOfObject_OnSimPath_ElevatesXIL2CPP070ToError()
    {
        Pass3Result result = Run(
            isSimPath: true,
            "namespace M { public class Holder { private System.Collections.Generic.List<object> _things; } }");

        DiagnosticRecord diag = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.ConservativeXGCRootSpan, diag.Code);
        Assert.Equal(XilSeverity.Error, diag.Severity);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void DictionaryWithObjectValue_RecordsConservativeSite_AndWarns()
    {
        Pass3Result result = Run(
            "namespace M { using Engine; public class Holder { private System.Collections.Generic.Dictionary<FName, object> _m; } }");

        ContainerSite site = Assert.Single(result.GetAll<ContainerSite>());
        Assert.Equal(ContainerGcKind.Conservative, site.GcKind);
        Assert.Equal("TMap<FName, void*>", site.EmitSignature);

        DiagnosticRecord diag = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.ConservativeXGCRootSpan, diag.Code);
    }

    [Fact]
    public void ListOfNonXObjectInterface_IsConservative()
    {
        Pass3Result result = Run(
            "namespace M { public interface IThing { } public class Holder { private System.Collections.Generic.List<IThing> _things; } }");

        ContainerSite site = Assert.Single(result.GetAll<ContainerSite>());
        Assert.Equal(ContainerGcKind.Conservative, site.GcKind);

        DiagnosticRecord diag = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.ConservativeXGCRootSpan, diag.Code);
    }

    // ---------------------------------------------------------------
    // Value-type entry in object container: XIL2CPP073.
    // ---------------------------------------------------------------

    [Fact]
    public void ObjectContainerWithValueTypeEntry_EmitsXIL2CPP073()
    {
        // A local List<object> initialised with int literals: each value-type
        // entry would require boxing into the object slot (banned).
        Pass3Result result = Run(
            "namespace M { public class Holder { public void M() { var list = new System.Collections.Generic.List<object> { 1, 2 }; } } }");

        Assert.Single(result.GetAll<ContainerSite>());

        List<DiagnosticRecord> boxing = result.Diagnostics
            .Where(d => d.Code == DiagnosticCodes.ValueTypeInObjectContainerRequiresBoxing)
            .ToList();
        Assert.Equal(2, boxing.Count);
        Assert.All(boxing, d => Assert.Equal(XilSeverity.Error, d.Severity));

        // The conservative warning still fires for the container itself.
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.ConservativeXGCRootSpan);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void ObjectContainerWithReferenceEntry_DoesNotEmitXIL2CPP073()
    {
        // An XObject reference entry is legal in a List<object> (no boxing).
        Pass3Result result = Run(
            "namespace M { using Engine; public class Holder { public void M(XActor a) { var list = new System.Collections.Generic.List<object> { a }; } } }");

        Assert.DoesNotContain(
            result.Diagnostics,
            d => d.Code == DiagnosticCodes.ValueTypeInObjectContainerRequiresBoxing);

        // Conservative warning still fires.
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.ConservativeXGCRootSpan);
    }

    // ---------------------------------------------------------------
    // By-value XGC-aware container parameter: XIL2CPP071.
    // ---------------------------------------------------------------

    [Fact]
    public void ByValueStrongContainerParameter_EmitsXIL2CPP071()
    {
        Pass3Result result = Run(
            "namespace M { using Engine; public class Holder { public void M(System.Collections.Generic.List<XActor> actors) { } } }");

        // The parameter records a strong site...
        Assert.Single(result.GetAll<ContainerSite>());

        DiagnosticRecord diag = Assert.Single(
            result.Diagnostics.Where(d => d.Code == DiagnosticCodes.ContainerCopySemanticsForbidden));
        Assert.Equal(XilSeverity.Error, diag.Severity);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void RefStrongContainerParameter_DoesNotEmitXIL2CPP071()
    {
        Pass3Result result = Run(
            "namespace M { using Engine; public class Holder { public void M(ref System.Collections.Generic.List<XActor> actors) { } } }");

        Assert.DoesNotContain(
            result.Diagnostics,
            d => d.Code == DiagnosticCodes.ContainerCopySemanticsForbidden);
    }

    [Fact]
    public void ByValueValueTypeContainerParameter_DoesNotEmitXIL2CPP071()
    {
        // List<int> has no GC scaffolding; by-value is not a 071 site here.
        Pass3Result result = Run(
            "namespace M { public class Holder { public void M(System.Collections.Generic.List<int> nums) { } } }");

        Assert.Empty(result.GetAll<ContainerSite>());
        Assert.DoesNotContain(
            result.Diagnostics,
            d => d.Code == DiagnosticCodes.ContainerCopySemanticsForbidden);
    }

    // ---------------------------------------------------------------
    // Non-container declarations are ignored.
    // ---------------------------------------------------------------

    [Fact]
    public void NonContainerType_RecordsNoSite()
    {
        Pass3Result result = Run(
            "namespace M { using Engine; public class Holder { private XActor _a; private int _n; } }");

        Assert.Empty(result.GetAll<ContainerSite>());
        Assert.Empty(result.Diagnostics);
    }

    // ---------------------------------------------------------------
    // Determinism: a second run produces identical sites + diagnostics.
    // ---------------------------------------------------------------

    [Fact]
    public void Analyze_IsDeterministic_AcrossRuns()
    {
        const string src =
            "namespace M { using Engine; public class Holder { "
            + "private System.Collections.Generic.List<XActor> _a; "
            + "private System.Collections.Generic.List<object> _o; "
            + "private System.Collections.Generic.Dictionary<FName, XActor> _d; } }";

        Pass3Result first = Run(src);
        Pass3Result second = Run(src);

        List<string> firstSites = first.GetAll<ContainerSite>()
            .Select(s => $"{s.EmitSignature}@{s.Line}:{s.Column}").ToList();
        List<string> secondSites = second.GetAll<ContainerSite>()
            .Select(s => $"{s.EmitSignature}@{s.Line}:{s.Column}").ToList();
        Assert.Equal(firstSites, secondSites);

        List<string> firstDiags = first.Diagnostics.Select(d => d.Code).ToList();
        List<string> secondDiags = second.Diagnostics.Select(d => d.Code).ToList();
        Assert.Equal(firstDiags, secondDiags);
    }
}
