// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit;

/// <summary>
/// Tests for <see cref="MethodShadowStackBuilder"/> (XIL2CPP Phase 6.g,
/// WU-6G-CORE): the per-method precise-GC shadow stack + stack-map record.
/// Covers idempotent slot allocation, the EXACT emitted text of the array
/// declaration / slot write / slot clear / stack-map record (including the
/// <c>liveRefOffsets = { 0, 8, 16 }</c> index*8 offsets), determinism, and --
/// through the <see cref="MethodEmitter"/> two-pass integration -- that
/// <c>self</c> roots slot 0 and an XObject-derived parameter roots a slot.
/// </summary>
public sealed class MethodShadowStackBuilderTests
{
    // =================================================================
    // Idempotent allocation.
    // =================================================================

    [Fact]
    public void Allocate_AssignsSequentialIndices_FromZero()
    {
        MethodShadowStackBuilder b = new();

        Assert.Equal(0, b.Allocate("a", "exprA"));
        Assert.Equal(1, b.Allocate("b", "exprB"));
        Assert.Equal(2, b.Allocate("c", "exprC"));
        Assert.Equal(3, b.Count);
    }

    [Fact]
    public void Allocate_IsIdempotentOnKey_ReturnsExistingIndex_WithoutGrowing()
    {
        MethodShadowStackBuilder b = new();

        int first = b.Allocate("k", "expr1");
        int second = b.Allocate("k", "exprIgnored");

        Assert.Equal(first, second);
        Assert.Equal(1, b.Count);
        // The first allocation's expression is the one retained (idempotent).
        Assert.Equal("expr1", b.Slots[0].CppXObjectCastExpr);
    }

    [Fact]
    public void Slots_AreInAllocationOrder()
    {
        MethodShadowStackBuilder b = new();
        b.Allocate("self", "self");
        b.Allocate("p0", "owner");

        Assert.Equal(2, b.Slots.Count);
        Assert.Equal("self", b.Slots[0].SymbolKey);
        Assert.Equal("self", b.Slots[0].CppXObjectCastExpr);
        Assert.Equal("p0", b.Slots[1].SymbolKey);
        Assert.Equal("owner", b.Slots[1].CppXObjectCastExpr);
    }

    [Fact]
    public void TryGetIndex_ReportsAllocatedKeys()
    {
        MethodShadowStackBuilder b = new();
        b.Allocate("present", "e");

        Assert.True(b.TryGetIndex("present", out int idx));
        Assert.Equal(0, idx);
        Assert.False(b.TryGetIndex("absent", out _));
    }

    // =================================================================
    // EmitArrayDecl exact text.
    // =================================================================

    [Fact]
    public void EmitArrayDecl_RendersTemplatedXPtrArray_SizedToCount()
    {
        MethodShadowStackBuilder b = new();
        b.Allocate("self", "self");
        b.Allocate("p0", "owner");

        CppWriter w = new();
        b.EmitArrayDecl(w);

        Assert.Equal(
            "::XCore::Reflect::XPtr<::XCore::Reflect::XObject> _liveRefs[2] = {};\n",
            w.Build());
    }

    [Fact]
    public void EmitArrayDecl_EmptyBuilder_RendersZeroLength()
    {
        // The MethodEmitter elides the array when Count == 0; the builder itself
        // still renders a faithful N=0 declaration if asked.
        MethodShadowStackBuilder b = new();
        CppWriter w = new();
        b.EmitArrayDecl(w);

        Assert.Equal(
            "::XCore::Reflect::XPtr<::XCore::Reflect::XObject> _liveRefs[0] = {};\n",
            w.Build());
    }

    // =================================================================
    // EmitSlotWrite / EmitSlotClear exact text.
    // =================================================================

    [Fact]
    public void EmitSlotWrite_WrapsReinterpretCastInTemplatedXPtr()
    {
        MethodShadowStackBuilder b = new();
        b.Allocate("self", "self");

        CppWriter w = new();
        b.EmitSlotWrite(0, "self", w);

        Assert.Equal(
            "_liveRefs[0] = ::XCore::Reflect::XPtr<::XCore::Reflect::XObject>("
            + "reinterpret_cast<::XCore::Reflect::XObject*>(self));\n",
            w.Build());
    }

    [Fact]
    public void EmitSlotClear_AssignsNullptr()
    {
        MethodShadowStackBuilder b = new();
        b.Allocate("self", "self");
        b.Allocate("p0", "owner");

        CppWriter w = new();
        b.EmitSlotClear(1, w);

        Assert.Equal("_liveRefs[1] = nullptr;\n", w.Build());
    }

    [Fact]
    public void EmitSlotWrite_OutOfRange_Throws()
    {
        MethodShadowStackBuilder b = new();
        b.Allocate("self", "self");

        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => b.EmitSlotWrite(1, "x", new CppWriter()));
    }

    // =================================================================
    // EmitStackMapRecord exact text + offsets {0, 8, 16}.
    // =================================================================

    [Fact]
    public void EmitStackMapRecord_RendersRecord_WithIndexTimesEightOffsets()
    {
        MethodShadowStackBuilder b = new();
        b.Allocate("self", "self");
        b.Allocate("p0", "a");
        b.Allocate("p1", "bb");

        CppWriter w = new();
        b.EmitStackMapRecord("MyFunc", w);
        string cpp = w.Build();

        // The static const record in .rodata.
        Assert.Contains("static const ::XCore::Reflect::FStackMapRecord _stackMap = {", cpp);
        Assert.Contains(".pcRangeBegin = 0,", cpp);
        Assert.Contains(".pcRangeEnd = 0xFFFFFFFFu,", cpp);
        Assert.Contains(".numLiveRefs = 3,", cpp);
        Assert.Contains("._pad = 0,", cpp);
        // The crux: liveRefOffsets are index*8 -> { 0, 8, 16 }.
        Assert.Contains(".liveRefOffsets = { 0, 8, 16 },", cpp);

        // The [[maybe_unused]] lazy-init registrar keyed off &MyFunc.
        Assert.Contains("[[maybe_unused]] static const bool _stackMapReg = []() {", cpp);
        Assert.Contains(
            "::XCore::Reflect::XStackMapTable::Register(reinterpret_cast<unsigned long long>(&MyFunc), "
            + "0 /*funcSize sentinel*/, &_stackMap);",
            cpp);
    }

    [Fact]
    public void EmitStackMapRecord_SingleSlot_OffsetsAreJustZero()
    {
        MethodShadowStackBuilder b = new();
        b.Allocate("self", "self");

        CppWriter w = new();
        b.EmitStackMapRecord("F", w);

        Assert.Contains(".numLiveRefs = 1,", w.Build());
        Assert.Contains(".liveRefOffsets = { 0 },", w.Build());
    }

    // =================================================================
    // Determinism.
    // =================================================================

    [Fact]
    public void EmittedFragments_AreByteDeterministic()
    {
        static string Emit()
        {
            MethodShadowStackBuilder b = new();
            b.Allocate("self", "self");
            b.Allocate("p0", "owner");
            CppWriter w = new();
            b.EmitArrayDecl(w);
            b.EmitSlotWrite(0, "self", w);
            b.EmitSlotWrite(1, "owner", w);
            b.EmitStackMapRecord("Sym", w);
            return w.Build();
        }

        Assert.Equal(Emit(), Emit());
    }

    // =================================================================
    // MethodEmitter two-pass integration: self at index 0; XObject param slot.
    // =================================================================

    // Engine-type stand-in so AnalyzerHelpers.IsXObjectDerived recognises an
    // XObject-derived parameter without the curated engine BCL ref.
    private const string EngineStubs = """
        namespace XPact.CoreXObject { public abstract class XObject { } }
        namespace Engine { public sealed class XActor : XPact.CoreXObject.XObject { } }
        """;

    [Fact]
    public void MethodEmitter_InstanceMethod_RootsSelfAtIndexZero()
    {
        const string source = """
            namespace Game
            {
                internal class Worker
                {
                    internal int Compute(int x) => x + 1;
                }
            }
            """;
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        IMethodSymbol method = FindMethod(ctx, "Compute");

        string cpp = EmitMethod(ctx, method);

        // self roots slot 0; the value-typed int param does NOT root a slot, so N = 1.
        Assert.Contains(
            MethodShadowStackBuilder.SlotElementType + " " + MethodShadowStackBuilder.ArrayName + "[1] = {};",
            cpp);
        Assert.Contains(
            "_liveRefs[0] = " + MethodShadowStackBuilder.SlotElementType
            + "(reinterpret_cast<" + MethodShadowStackBuilder.XObjectPointerType + ">(self));",
            cpp);
    }

    [Fact]
    public void MethodEmitter_XObjectParameter_RootsASlot_AfterSelf()
    {
        // An instance method whose parameter is an XObject-derived reference:
        // self roots slot 0, the XObject param roots slot 1 (N = 2). The int
        // param roots nothing.
        const string source = """
            namespace Game
            {
                using Engine;
                internal class Worker
                {
                    internal void Take(XActor other, int n) { }
                }
            }
            """;
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(EngineStubs, source);
        IMethodSymbol method = FindMethod(ctx, "Take");

        string cpp = EmitMethod(ctx, method);

        // N = 2: self + the XObject param (the int param is not rooted).
        Assert.Contains(
            MethodShadowStackBuilder.SlotElementType + " " + MethodShadowStackBuilder.ArrayName + "[2] = {};",
            cpp);
        Assert.Contains(
            "_liveRefs[0] = " + MethodShadowStackBuilder.SlotElementType
            + "(reinterpret_cast<" + MethodShadowStackBuilder.XObjectPointerType + ">(self));",
            cpp);
        Assert.Contains(
            "_liveRefs[1] = " + MethodShadowStackBuilder.SlotElementType
            + "(reinterpret_cast<" + MethodShadowStackBuilder.XObjectPointerType + ">(other));",
            cpp);
        // Two slots -> offsets { 0, 8 }.
        Assert.Contains(".liveRefOffsets = { 0, 8 },", cpp);
    }

    [Fact]
    public void MethodEmitter_StaticMethod_NoXObjectArgs_ElidesShadowStackEntirely()
    {
        const string source = """
            namespace Game
            {
                internal class Util
                {
                    internal static int Twice(int x) => x + x;
                }
            }
            """;
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        IMethodSymbol method = FindMethod(ctx, "Twice");

        string cpp = EmitMethod(ctx, method);

        // No roots -> no array, no slot writes, no stack-map record.
        Assert.DoesNotContain(MethodShadowStackBuilder.ArrayName, cpp);
        Assert.DoesNotContain(MethodShadowStackBuilder.StackMapRecordType, cpp);
        // The safepoint poll still emits.
        Assert.Contains(MethodEmitter.SafepointCheck, cpp);
    }

    // =================================================================
    // Helpers.
    // =================================================================

    private static string EmitMethod(EmitContext ctx, IMethodSymbol method)
    {
        CppWriter writer = new();
        BodyLoweringRuleRegistry registry = new(new List<IBodyLoweringRule>());
        StatementEmitter bodyEmitter = new(ctx, writer, registry);
        new MethodEmitter().EmitMethod(method, ctx, writer, bodyEmitter);
        return writer.Build();
    }

    private static IMethodSymbol FindMethod(EmitContext ctx, string name)
        => Pass5Driver.EnumerateEmittableFunctions(ctx.Unit)
            .First(m => m.Name == name && m.MethodKind == MethodKind.Ordinary);
}
