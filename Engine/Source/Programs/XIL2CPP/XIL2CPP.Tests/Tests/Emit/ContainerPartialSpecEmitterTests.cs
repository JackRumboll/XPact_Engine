// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Analysis.Analyzers;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Simgenics.XPact.XIL2CPP.Tiering;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit;

/// <summary>
/// Tests for <see cref="ContainerPartialSpecEmitter"/> (WU-6F) per
/// /Documents/XIL2CPP.html Rev 4 Section 5.7 (container emit) + Section 6.2
/// (container roots: XGCRootSpan emit). Proves the emitter emits exactly one
/// C++ partial specialization per UNIQUE container instantiation (dedup by
/// emit signature), threads the locked
/// <c>::XCore::Reflect::XGC_*RootSpan</c> ABI (register / update / unregister)
/// into the spec, selects the GC root kind (Strong vs Conservative) from the
/// site, emits the move-only (deleted-copy) discipline, and is
/// byte-deterministic in ordinal signature order. The
/// <see cref="Pass6Driver"/> wiring (the synthetic module-level container-spec
/// unit) is exercised end-to-end too.
/// </summary>
public sealed class ContainerPartialSpecEmitterTests
{
    // Engine-type stand-ins (mirroring ContainerAnalyzerTests): the analyzer's
    // AnalyzerHelpers.IsXObjectDerived recognises a class deriving from a type
    // whose metadata name is "XObject" in namespace "XPact.CoreXObject" even
    // without the curated BCL ref.
    private const string EngineStubs = """
        namespace XPact.CoreXObject { public abstract class XObject { } }
        namespace Engine
        {
            public sealed class XActor : XPact.CoreXObject.XObject { }
            public sealed class XPawn : XPact.CoreXObject.XObject { }
            public readonly struct FName { }
        }
        """;

    // ---------------------------------------------------------------
    // Pipeline plumbing: build an EmitContext whose Pass-3 ran the real
    // ContainerAnalyzer (the shared EmitTestHelpers run a different analyzer
    // set, so this test owns its own pipeline mirror that records sites).
    // ---------------------------------------------------------------

    private static EmitContext BuildContext(params string[] containerSources)
    {
        List<string> sources = new(containerSources.Length + 1) { EngineStubs };
        sources.AddRange(containerSources);

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(
            isSimPath: false, sources.ToArray());
        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        Pass3Result pass3 = Pass3Driver.Run(
            unit, new ISemanticAnalyzer[] { new ContainerAnalyzer() });
        TierTable tierTable = Pass4Driver.Run(unit, pass3);
        ManglingTable manglingTable = Pass5Driver.Run(
            unit, tierTable, EmitTestHelpers.ContractVersionTag);

        return new EmitContext(
            unit,
            pass3,
            tierTable,
            manglingTable,
            EmitTestHelpers.ContractVersionTag,
            EmitTestHelpers.GCRootAbi,
            EmitTestHelpers.ExceptionAbi,
            EmitTestHelpers.ManglingSchemeTag);
    }

    private static string Emit(params string[] containerSources)
    {
        EmitContext ctx = BuildContext(containerSources);
        CppWriter w = new();
        new ContainerPartialSpecEmitter().Emit(ctx, w);
        return w.Build();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    // ---------------------------------------------------------------
    // Strong TArray<XPtr<T>>.
    // ---------------------------------------------------------------

    [Fact]
    public void ListOfXActor_EmitsStrongTArrayPartialSpec()
    {
        string cpp = Emit(
            "namespace M { using Engine; public class Holder { private System.Collections.Generic.List<XActor> _actors; } }");

        // The explicit specialization of the closed TArray template, emitted
        // in-namespace with the UNqualified template name + the `struct`
        // class-key matching the primary template (FIX 1: a leading global
        // qualifier `struct ::XCore::Container::TArray<...>` is rejected by g++).
        Assert.Contains("template <>", cpp);
        Assert.Contains("namespace XCore {", cpp);
        Assert.Contains("namespace Container {", cpp);
        Assert.Contains(
            "struct TArray<XPtr<XActor>>",
            cpp);
        // No leading-global-qualifier specialization name (the old, ill-formed
        // shape) survives.
        Assert.DoesNotContain("::XCore::Container::TArray<XPtr<XActor>>", cpp);

        // The XGCRootSpan member.
        Assert.Contains("::XCore::Reflect::XGCRootSpan _rootSpan;", cpp);

        // The constructor registers the span as a Strong root over the element
        // stride (sizeof of the element slot, not the C# element type).
        Assert.Contains(
            "::XCore::Reflect::XGC_RegisterRootSpan(&_rootSpan, GetData(), Num(), sizeof(XPtr<XActor>), ::XCore::Reflect::XGCRootKind::Strong);",
            cpp);

        // The resize hook updates the span; the destructor unregisters it.
        Assert.Contains(
            "void _OnRootSpanResize(void* base, size_t n)",
            cpp);
        Assert.Contains(
            "::XCore::Reflect::XGC_UpdateRootSpan(&_rootSpan, base, n, sizeof(XPtr<XActor>));",
            cpp);
        Assert.Contains(
            "::XCore::Reflect::XGC_UnregisterRootSpan(&_rootSpan);",
            cpp);

        // The container = true marker comment.
        Assert.Contains("// container = true", cpp);

        // Not a conservative site -> no XIL2CPP070 marker.
        Assert.DoesNotContain("XIL2CPP070", cpp);
        Assert.DoesNotContain("XGCRootKind::Conservative", cpp);
    }

    [Fact]
    public void DictionaryOfFNameXActor_EmitsStrongTMapPartialSpec_OverValueColumn()
    {
        string cpp = Emit(
            "namespace M { using Engine; public class Holder { private System.Collections.Generic.Dictionary<FName, XActor> _byName; } }");

        Assert.Contains(
            "struct TMap<FName, XPtr<XActor>>",
            cpp);
        // The GC-rooted stride is the VALUE column (the last template argument),
        // not the key.
        Assert.Contains(
            "sizeof(XPtr<XActor>), ::XCore::Reflect::XGCRootKind::Strong);",
            cpp);
    }

    [Fact]
    public void HashSetOfXActor_EmitsStrongTSetPartialSpec()
    {
        string cpp = Emit(
            "namespace M { using Engine; public class Holder { private System.Collections.Generic.HashSet<XActor> _set; } }");

        Assert.Contains(
            "struct TSet<XPtr<XActor>>",
            cpp);
        Assert.Contains(
            "sizeof(XPtr<XActor>), ::XCore::Reflect::XGCRootKind::Strong);",
            cpp);
    }

    // ---------------------------------------------------------------
    // Conservative kind selection (XIL2CPP070).
    // ---------------------------------------------------------------

    [Fact]
    public void ListOfObject_EmitsConservativeKind_WithXIL2CPP070Marker()
    {
        string cpp = Emit(
            "namespace M { public class Holder { private System.Collections.Generic.List<object> _things; } }");

        Assert.Contains(
            "struct TArray<void*>",
            cpp);
        // Conservative kind selected.
        Assert.Contains(
            "sizeof(void*), ::XCore::Reflect::XGCRootKind::Conservative);",
            cpp);
        Assert.DoesNotContain("XGCRootKind::Strong", cpp);
        // The conservative marker comment.
        Assert.Contains("// XIL2CPP070 conservative", cpp);
    }

    // ---------------------------------------------------------------
    // Move-only discipline (deleted copy, defaulted move).
    // ---------------------------------------------------------------

    [Fact]
    public void EmittedSpec_IsMoveOnly_DeletedCopy_DefaultedMove()
    {
        string cpp = Emit(
            "namespace M { using Engine; public class Holder { private System.Collections.Generic.List<XActor> _actors; } }");

        Assert.Contains("TArray(const TArray&) = delete;", cpp);
        Assert.Contains("TArray& operator=(const TArray&) = delete;", cpp);
        Assert.Contains("TArray(TArray&&) = default;", cpp);
        Assert.Contains("TArray& operator=(TArray&&) = default;", cpp);
    }

    // ---------------------------------------------------------------
    // Dedup: two same-signature sites -> ONE spec.
    // ---------------------------------------------------------------

    [Fact]
    public void TwoSitesSameSignature_OneSpec_TwoSitesDifferentSignature_TwoSpecs()
    {
        // Same-signature dedup: two List<XActor> sites -> one TArray<XPtr<XActor>> spec.
        string same = Emit(
            "namespace M { using Engine; public class Holder { "
            + "private System.Collections.Generic.List<XActor> _a; "
            + "private System.Collections.Generic.List<XActor> _b; } }");
        Assert.Equal(1, CountOccurrences(same, "template <>"));
        Assert.Equal(
            1,
            CountOccurrences(same, "struct TArray<XPtr<XActor>>"));

        // Distinct signatures: List<XActor> + List<XPawn> -> two specs.
        string distinct = Emit(
            "namespace M { using Engine; public class Holder { "
            + "private System.Collections.Generic.List<XActor> _a; "
            + "private System.Collections.Generic.List<XPawn> _b; } }");
        Assert.Equal(2, CountOccurrences(distinct, "template <>"));
        Assert.Contains("struct TArray<XPtr<XActor>>", distinct);
        Assert.Contains("struct TArray<XPtr<XPawn>>", distinct);
    }

    // ---------------------------------------------------------------
    // Ordinal determinism.
    // ---------------------------------------------------------------

    [Fact]
    public void MultipleSpecs_AreEmittedInOrdinalSignatureOrder()
    {
        // XActor < XPawn ordinally, so the TArray<XPtr<XActor>> spec precedes
        // the TArray<XPtr<XPawn>> spec regardless of declaration order.
        string cpp = Emit(
            "namespace M { using Engine; public class Holder { "
            + "private System.Collections.Generic.List<XPawn> _b; "
            + "private System.Collections.Generic.List<XActor> _a; } }");

        int actorIndex = cpp.IndexOf(
            "struct TArray<XPtr<XActor>>", System.StringComparison.Ordinal);
        int pawnIndex = cpp.IndexOf(
            "struct TArray<XPtr<XPawn>>", System.StringComparison.Ordinal);

        Assert.True(actorIndex >= 0);
        Assert.True(pawnIndex >= 0);
        Assert.True(actorIndex < pawnIndex, "Specs must emit in ordinal signature order.");
    }

    [Fact]
    public void Emit_IsByteIdentical_AcrossTwoRuns()
    {
        string[] sources =
        {
            "namespace M { using Engine; public class Holder { "
            + "private System.Collections.Generic.List<XPawn> _b; "
            + "private System.Collections.Generic.Dictionary<FName, XActor> _m; "
            + "private System.Collections.Generic.HashSet<XActor> _s; } }",
        };

        Assert.Equal(Emit(sources), Emit(sources));
    }

    // ---------------------------------------------------------------
    // Empty: no container sites -> no output.
    // ---------------------------------------------------------------

    [Fact]
    public void NoContainerSites_EmitsNothing()
    {
        // List<int> is a pure-value container: the analyzer records NO site, so
        // the emitter writes nothing.
        string cpp = Emit(
            "namespace M { public class Holder { private System.Collections.Generic.List<int> _nums; } }");

        Assert.Equal(string.Empty, cpp);
    }

    // ---------------------------------------------------------------
    // Pass6Driver wiring: the synthetic module-level container-spec unit.
    // ---------------------------------------------------------------

    [Fact]
    public void Pass6Driver_AppendsContainerSpecUnit_WhenSitesExist()
    {
        EmitContext ctx = BuildContext(
            "namespace M { using Engine; public class Holder { private System.Collections.Generic.List<XActor> _actors; } }");

        IReadOnlyList<EmitResult> results = new Pass6Driver().Run(ctx);

        EmitResult? specs = null;
        foreach (EmitResult r in results)
        {
            if (r.SourceRelativePath.EndsWith(Pass6Driver.ContainerSpecsStem, System.StringComparison.Ordinal))
            {
                specs = r;
                break;
            }
        }

        Assert.NotNull(specs);
        Assert.Equal(string.Empty, specs!.HeaderContent);
        Assert.Contains(
            "struct TArray<XPtr<XActor>>",
            specs.SourceContent);
        Assert.StartsWith(FileEmitter.CopyrightBanner, specs.SourceContent);
    }

    [Fact]
    public void Pass6Driver_OmitsContainerSpecUnit_WhenNoSites()
    {
        // A module with only a pure-value container records no site -> no
        // synthetic container-spec unit is appended.
        EmitContext ctx = BuildContext(
            "namespace M { public class Holder { private System.Collections.Generic.List<int> _nums; } }");

        IReadOnlyList<EmitResult> results = new Pass6Driver().Run(ctx);

        foreach (EmitResult r in results)
        {
            Assert.False(
                r.SourceRelativePath.EndsWith(Pass6Driver.ContainerSpecsStem, System.StringComparison.Ordinal),
                "No container-spec unit should be emitted for a container-free module.");
        }
    }
}
