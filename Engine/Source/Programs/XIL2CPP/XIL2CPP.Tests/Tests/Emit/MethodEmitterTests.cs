// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Tiering;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit;

/// <summary>
/// Tests for <see cref="MethodEmitter"/> (XIL2CPP Phase 6.e, WU-E2): the
/// emitted method SHELL -- the Tier-2 direct vs Tier-1 shim shape, the
/// <c>noexcept</c> placement, the implicit typed <c>self</c> first parameter,
/// the static-method omission of <c>self</c>, the <c>$ctor</c> linker symbol,
/// the safe-point + 6.g GC-hook prologue lines, and byte-determinism. Bodies
/// are lowered through a <see cref="StatementEmitter"/> with ZERO discovered
/// rules, so the body renders as <c>// TODO(6.e)</c> markers -- as expected for
/// this foundation wave; the tests assert the shell, not the body lowering.
/// </summary>
public sealed class MethodEmitterTests
{
    // -----------------------------------------------------------------
    // Tier 2 (direct) shape.
    // -----------------------------------------------------------------

    [Fact]
    public void Tier2_Direct_EmitsExternCNoexceptFreeFunctionWithSafepointAndShadowStack()
    {
        // An internal class + internal non-throwing leaf method classifies Tier 2.
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
        Assert.Equal(FunctionTier.Tier2, ctx.FindTier(StableId.FromSymbol(method)));

        string cpp = EmitMethod(ctx, method);

        ManglingRecord record = ctx.FindMangling(StableId.FromSymbol(method))!.Value;

        // One extern "C" direct function, noexcept, with the Phase 6.g
        // precise-GC shadow stack (self roots slot 0) + the safepoint poll.
        Assert.Contains("extern \"C\" int32_t " + record.LinkerSymbol + "(", cpp);
        Assert.Contains(") noexcept {", cpp);
        // The 6.g shadow-stack prologue REPLACES the old TODO(6.g) hook comment:
        // the rooted instance method declares _liveRefs[1] and roots self at 0.
        Assert.DoesNotContain("// " + MethodEmitter.GcHookComment, cpp);
        Assert.Contains(
            MethodShadowStackBuilder.SlotElementType + " " + MethodShadowStackBuilder.ArrayName + "[1] = {};",
            cpp);
        Assert.Contains(
            MethodShadowStackBuilder.ArrayName + "[0] = " + MethodShadowStackBuilder.SlotElementType
            + "(reinterpret_cast<" + MethodShadowStackBuilder.XObjectPointerType + ">(self));",
            cpp);
        // The registered stack-map record keyed off the function's own symbol.
        Assert.Contains("static const " + MethodShadowStackBuilder.StackMapRecordType, cpp);
        Assert.Contains(MethodShadowStackBuilder.StackMapTableType + "::Register(", cpp);
        Assert.Contains(MethodEmitter.SafepointCheck, cpp);

        // WU-6G-STACKMAP placement fix: the FStackMapRecord + registrar are
        // emitted at FILE scope AFTER the function's closing brace (so the
        // static-init registrar actually runs), NOT inside the body where they
        // would be unreachable dead code after a returning body. The record line
        // therefore follows the function's column-0 closing brace, and the record
        // is wrapped in the per-function stack-map namespace.
        string[] lines = cpp.Split('\n');
        int funcCloseBrace = System.Array.FindIndex(lines, l => l == "}");
        int recordLine = System.Array.FindIndex(
            lines, l => l.Contains("static const " + MethodShadowStackBuilder.StackMapRecordType));
        Assert.True(funcCloseBrace >= 0 && recordLine > funcCloseBrace,
            "The FStackMapRecord must be emitted at file scope after the function's closing brace.");
        Assert.Contains("namespace " + MethodEmitter.StackMapNamespacePrefix, cpp);

        // No Tier-1 shim machinery for a Tier-2 method.
        Assert.DoesNotContain(MethodEmitter.ShimSuffix, cpp);
        Assert.DoesNotContain("XResult", cpp);
        Assert.DoesNotContain("catch", cpp);
    }

    [Fact]
    public void Tier2_InstanceMethod_TakesTypedSelfFirstParameter()
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

        // The first parameter is the typed self pointer.
        Assert.Contains("(::Game::Worker* self, int32_t x)", cpp);
    }

    // -----------------------------------------------------------------
    // Tier 1 (shim) shape.
    // -----------------------------------------------------------------

    [Fact]
    public void Tier1_Shim_EmitsStaticBodyHelperPlusExternCShimWithXResultOutParam()
    {
        // A public class + public method classifies Tier 1 (exported).
        const string source = """
            namespace Game
            {
                public class Surface
                {
                    public int Add(int a, int b) => a + b;
                }
            }
            """;
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        IMethodSymbol method = FindMethod(ctx, "Add");
        Assert.Equal(FunctionTier.Tier1, ctx.FindTier(StableId.FromSymbol(method)));

        string cpp = EmitMethod(ctx, method);
        ManglingRecord record = ctx.FindMangling(StableId.FromSymbol(method))!.Value;

        // Private static _Body helper carries the real body + the Phase 6.g
        // shadow stack + safepoint; the stack map is keyed off the _Body symbol.
        Assert.Contains("static int32_t " + record.LinkerSymbol + MethodEmitter.BodySuffix + "(", cpp);
        Assert.DoesNotContain("// " + MethodEmitter.GcHookComment, cpp);
        Assert.Contains(
            MethodShadowStackBuilder.SlotElementType + " " + MethodShadowStackBuilder.ArrayName + "[1] = {};",
            cpp);
        Assert.Contains(
            MethodShadowStackBuilder.StackMapTableType + "::Register(reinterpret_cast<unsigned long long>(&"
            + record.LinkerSymbol + MethodEmitter.BodySuffix + ")",
            cpp);
        Assert.Contains(MethodEmitter.SafepointCheck, cpp);

        // WU-6G-STACKMAP placement fix: the Tier-1 stack-map record is emitted at
        // FILE scope after the _Body helper's closing brace (reachable at
        // static-init), NOT inside the _Body block after its return.
        string[] tier1Lines = cpp.Split('\n');
        int bodyCloseBrace = System.Array.FindIndex(tier1Lines, l => l == "}");
        int tier1RecordLine = System.Array.FindIndex(
            tier1Lines, l => l.Contains("static const " + MethodShadowStackBuilder.StackMapRecordType));
        Assert.True(bodyCloseBrace >= 0 && tier1RecordLine > bodyCloseBrace,
            "The Tier-1 FStackMapRecord must be emitted at file scope after the _Body closing brace.");

        // Exported extern "C" void _Shim with the trailing XResult* out-param.
        Assert.Contains(
            "extern \"C\" void " + record.LinkerSymbol + MethodEmitter.ShimSuffix + "(",
            cpp);
        Assert.Contains(MethodEmitter.XResultType + "* " + MethodEmitter.OutResultName, cpp);

        // try / catch boundary converting a C# exception into the discriminator.
        Assert.Contains("try {", cpp);
        Assert.Contains("catch (const " + MethodEmitter.XCSharpExceptionType + "& ex)", cpp);
        Assert.Contains(
            MethodEmitter.OutResultName + "->discriminator = " + MethodEmitter.SuccessDiscriminator + ";",
            cpp);
        Assert.Contains(
            MethodEmitter.OutResultName + "->discriminator = " + MethodEmitter.ErrorDiscriminator + ";",
            cpp);
    }

    [Fact]
    public void Tier1_Shim_IsNotNoexcept_TheBoundaryConvertsExceptions()
    {
        const string source = """
            namespace Game
            {
                public class Surface
                {
                    public void Touch() { }
                }
            }
            """;
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        IMethodSymbol method = FindMethod(ctx, "Touch");

        string cpp = EmitMethod(ctx, method);
        ManglingRecord record = ctx.FindMangling(StableId.FromSymbol(method))!.Value;

        // The shim function line itself is not noexcept.
        string shimLine = cpp.Split('\n').Single(
            l => l.Contains(record.LinkerSymbol + MethodEmitter.ShimSuffix + "("));
        Assert.DoesNotContain("noexcept", shimLine);
    }

    [Fact]
    public void Tier1_Shim_NonVoidReturn_CapturesBodyResult()
    {
        const string source = """
            namespace Game
            {
                public class Surface
                {
                    public int Add(int a, int b) => a + b;
                }
            }
            """;
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        IMethodSymbol method = FindMethod(ctx, "Add");

        string cpp = EmitMethod(ctx, method);
        ManglingRecord record = ctx.FindMangling(StableId.FromSymbol(method))!.Value;

        Assert.Contains(
            "int32_t result = " + record.LinkerSymbol + MethodEmitter.BodySuffix + "(",
            cpp);
    }

    // -----------------------------------------------------------------
    // Static methods omit self; constructors use $ctor + take self.
    // -----------------------------------------------------------------

    [Fact]
    public void StaticMethod_OmitsSelfParameter()
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
        Assert.True(method.IsStatic);

        string cpp = EmitMethod(ctx, method);

        // No self parameter; only the declared int parameter.
        Assert.DoesNotContain(MethodEmitter.SelfParamName, cpp);
        Assert.Contains("(int32_t x)", cpp);
    }

    [Fact]
    public void Constructor_UsesDollarCtorLinkerSymbol_AndTakesSelf()
    {
        const string source = """
            namespace Game
            {
                internal class Thing
                {
                    internal Thing(int seed) { }
                }
            }
            """;
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        IMethodSymbol ctor = FindConstructor(ctx, "Thing");

        ManglingRecord record = ctx.FindMangling(StableId.FromSymbol(ctor))!.Value;
        Assert.True(record.IsConstructor);
        // The $ctor token survives into the linker symbol (mangled via the
        // Mangler's ConstructorToken).
        Assert.Contains("ctor", record.LinkerSymbol);

        string cpp = EmitMethod(ctx, ctor);

        // A constructor returns void in C++ and takes the freshly-allocated self.
        Assert.Contains(record.LinkerSymbol, cpp);
        Assert.Contains("::Game::Thing* self", cpp);
    }

    // -----------------------------------------------------------------
    // Body lowering seam: zero rules -> the body is TODO(6.e) markers.
    // -----------------------------------------------------------------

    [Fact]
    public void Body_WithZeroRules_LowersToTodoMarkers()
    {
        const string source = """
            namespace Game
            {
                internal class Worker
                {
                    internal void Step() { int y = 1; }
                }
            }
            """;
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        IMethodSymbol method = FindMethod(ctx, "Step");

        string cpp = EmitMethod(ctx, method);

        Assert.Contains("TODO(6.e):", cpp);
    }

    // -----------------------------------------------------------------
    // Determinism.
    // -----------------------------------------------------------------

    [Fact]
    public void EmitMethod_IsByteDeterministic()
    {
        const string source = """
            namespace Game
            {
                public class Surface
                {
                    public int Add(int a, int b) => a + b;
                }
            }
            """;
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        IMethodSymbol method = FindMethod(ctx, "Add");

        Assert.Equal(EmitMethod(ctx, method), EmitMethod(ctx, method));
    }

    // -----------------------------------------------------------------
    // BuildParameters / HasSelfParameter unit coverage.
    // -----------------------------------------------------------------

    [Fact]
    public void HasSelfParameter_TrueForInstance_FalseForStatic_TrueForCtor()
    {
        const string source = """
            namespace Game
            {
                internal class Mix
                {
                    internal Mix() { }
                    internal int Inst() => 1;
                    internal static int Stat() => 2;
                }
            }
            """;
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);

        Assert.True(MethodEmitter.HasSelfParameter(FindMethod(ctx, "Inst")));
        Assert.False(MethodEmitter.HasSelfParameter(FindMethod(ctx, "Stat")));
        Assert.True(MethodEmitter.HasSelfParameter(FindConstructor(ctx, "Mix")));
    }

    [Fact]
    public void RenderParamList_And_RenderArgList_RoundTripNamesAndTypes()
    {
        IReadOnlyList<CppParam> parameters = new[]
        {
            new CppParam("::Game::Worker* ", "self"),
            new CppParam("int32_t", "x"),
        };
        // Note: the first type carries a trailing space deliberately to prove
        // RenderParamList joins type + " " + name without collapsing.
        Assert.Equal("::Game::Worker*  self, int32_t x", MethodEmitter.RenderParamList(parameters));
        Assert.Equal("self, x", MethodEmitter.RenderArgList(parameters));
    }

    // -----------------------------------------------------------------
    // Helpers.
    // -----------------------------------------------------------------

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

    private static IMethodSymbol FindConstructor(EmitContext ctx, string typeName)
        => Pass5Driver.EnumerateEmittableFunctions(ctx.Unit)
            .First(m => m.MethodKind == MethodKind.Constructor && m.ContainingType.Name == typeName);
}
