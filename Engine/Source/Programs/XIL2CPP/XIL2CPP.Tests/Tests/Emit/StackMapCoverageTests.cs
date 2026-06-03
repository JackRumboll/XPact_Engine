// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Analysis.Analyzers;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Simgenics.XPact.XIL2CPP.Tiering;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit;

/// <summary>
/// The Phase 6.g precise-GC coverage gates (WU-6G-STACKMAP), per
/// /Documents/XIL2CPP.html Rev 4 Section 5.x (precise rooting) +
/// /Documents/XCoreXObject.html Section 5.2 (the stack-map protocol). Drives a
/// corpus of synthetic methods (each rooting 1-5 managed references via self +
/// XObject parameters) through the FULL pipeline and the real
/// <see cref="MethodEmitter"/>, using the Pass-3 <see cref="LiveLocalAnalyzer"/>
/// output as the coverage ORACLE:
/// <list type="bullet">
///   <item><description>
///     <b>X-IL2CPP-STACKMAP-COV.</b> EVERY method that roots at least one
///     reference emits -- at FILE scope, AFTER the function's closing brace -- an
///     <c>FStackMapRecord</c> whose <c>numLiveRefs</c> equals the oracle's
///     self + parameter root count for that method, whose <c>liveRefOffsets</c>
///     are the packed <c>{ 0, 8, 16, ... }</c> byte offsets, and which is
///     followed by a REACHABLE (file-scope, runs at static-init) registrar that
///     calls <c>XStackMapTable::Register</c>.
///   </description></item>
///   <item><description>
///     <b>X-IL2CPP-SHADOWSTACK-COV.</b> EVERY rooted reference (self + each
///     XObject parameter) the oracle records has a <c>_liveRefs[i]</c> slot
///     write in the emitted body.
///   </description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>The file-scope placement fix (WU-6G-STACKMAP).</b> The stack-map record +
/// registrar were previously emitted INSIDE the function body, after the lowered
/// body -- for any method whose body ends in a return, the function-local static
/// + its lazy-init registrar are UNREACHABLE dead code, so the stack map never
/// registers at runtime. The
/// <see cref="StackMapRecord_And_Registrar_AreEmittedAtFileScope_AfterClosingBrace"/>
/// test is the regression guard: the record + registrar must appear AFTER the
/// function's closing brace (file scope), not before it (in-body).
/// </para>
/// </remarks>
public sealed class StackMapCoverageTests
{
    // A locally-declared stand-in for the engine root reference type; the
    // metadata-name fallback in AnalyzerHelpers recognises it without the BCL ref.
    private const string XObjectStub =
        "namespace XPact.CoreXObject { public abstract class XObject { } }";

    /// <summary>
    /// The ~10-method coverage corpus: each method roots 1-5 references via self
    /// (instance members) + XObject(-derived) by-value parameters, with
    /// value-typed + by-ref parameters interleaved (which must NOT root). A mix
    /// of instance / static / constructor / Tier-1 / Tier-2 shapes.
    /// </summary>
    private const string Corpus = @"namespace M {
        using XPact.CoreXObject;
        public class Actor : XObject { }
        public class Pawn : XObject { }

        // Tier-1 (public) instance: self only (1 root).
        public class Solo : XObject {
            public void OnlySelf(int n) { }
        }

        // Tier-1 instance: self + 1 XObject param (2 roots); value param skipped.
        public class Pair : XObject {
            public void SelfPlusOne(Actor a, int n) { }
        }

        // Tier-1 instance: self + 2 XObject params (3 roots).
        public class Trio : XObject {
            public void SelfPlusTwo(Actor a, Pawn p, float f) { }
        }

        // Tier-1 instance: self + 3 XObject params (4 roots).
        public class Quad : XObject {
            public void SelfPlusThree(Actor a, Pawn p, Actor b, int n) { }
        }

        // Tier-2 (internal non-throwing leaf) instance: self + 1 XObject (2 roots).
        internal class Leaf : XObject {
            internal int Compute(Actor a, int x) { return x; }
        }

        // Static method: 2 XObject params, NO self (2 roots).
        public class StaticHolder : XObject {
            public static void TwoParams(Actor a, Pawn p) { }
        }

        // Static method: 5 XObject params (5 roots), the max in the corpus.
        public class FiveParams : XObject {
            public static void Five(Actor a, Pawn b, Actor c, Pawn d, Actor e) { }
        }

        // Constructor: self + 1 XObject param (2 roots).
        public class Built : XObject {
            public Built(Actor a, int seed) { }
        }

        // Tier-2 leaf instance: self + 1 XObject param + value param (2 roots).
        internal class Mixer : XObject {
            internal int Blend(Actor a, int x, float y) { return x; }
        }

        // A method that roots NOTHING (static, all value params): NO stack map.
        public class Bare {
            public static int NoRoots(int a, int b) { return a + b; }
        }
    }";

    // =================================================================
    // Pipeline + emit helpers.
    // =================================================================

    /// <summary>
    /// Run the full pipeline with the <see cref="LiveLocalAnalyzer"/> wired in
    /// (so the Pass-3 result carries the coverage oracle) and build a populated
    /// <see cref="EmitContext"/>.
    /// </summary>
    private static EmitContext BuildContextWithOracle(params string[] sources)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(isSimPath: false, sources);
        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        Pass3Result pass3 = Pass3Driver.Run(
            unit,
            new ISemanticAnalyzer[]
            {
                new LiveLocalAnalyzer(),
                new CrossModuleNoThrowAnalyzer(),
            });
        TierTable tierTable = Pass4Driver.Run(unit, pass3);
        ManglingTable manglingTable = Pass5Driver.Run(unit, tierTable, EmitTestHelpers.ContractVersionTag);

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

    /// <summary>Emit one method's C++ shell through the real MethodEmitter (empty body-rule set).</summary>
    private static string EmitMethod(EmitContext ctx, IMethodSymbol method)
    {
        CppWriter writer = new();
        BodyLoweringRuleRegistry registry = new(new List<IBodyLoweringRule>());
        StatementEmitter bodyEmitter = new(ctx, writer, registry);
        new MethodEmitter().EmitMethod(method, ctx, writer, bodyEmitter);
        return writer.Build();
    }

    /// <summary>The display string the LiveLocalAnalyzer records for a method (same SymbolDisplayFormat).</summary>
    private static readonly SymbolDisplayFormat s_methodFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>
    /// The oracle's prologue-rooted (self + parameter) record count for
    /// <paramref name="method"/> -- i.e. the count the emitter's
    /// <c>FStackMapRecord.numLiveRefs</c> must equal when the body roots no
    /// further locals (the empty-rule-set emit). Body-local records are excluded.
    /// </summary>
    private static int OraclePrologueRootCount(EmitContext ctx, IMethodSymbol method)
    {
        string display = method.ToDisplayString(s_methodFormat);
        return ctx.Pass3.GetAll<LiveLocalRecord>()
            .Count(r => r.ContainingMethodDisplay == display
                && r.Kind != LiveLocalKind.Local);
    }

    /// <summary>The oracle records (self + params + locals) for a method, in allocation order.</summary>
    private static IReadOnlyList<LiveLocalRecord> OracleRoots(EmitContext ctx, IMethodSymbol method)
    {
        string display = method.ToDisplayString(s_methodFormat);
        return ctx.Pass3.GetAll<LiveLocalRecord>()
            .Where(r => r.ContainingMethodDisplay == display)
            .OrderBy(r => r.DeclarationIndex)
            .ToList();
    }

    private static IReadOnlyList<IMethodSymbol> AllEmittableMethods(EmitContext ctx)
        => Pass5Driver.EnumerateEmittableFunctions(ctx.Unit)
            .Where(m => m.MethodKind is MethodKind.Ordinary or MethodKind.Constructor)
            .ToList();

    private static IMethodSymbol FindMethod(EmitContext ctx, string name)
        => Pass5Driver.EnumerateEmittableFunctions(ctx.Unit)
            .First(m => m.Name == name && m.MethodKind == MethodKind.Ordinary);

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    /// <summary>The expected packed liveRefOffsets body for <paramref name="n"/> slots: "0, 8, 16, ...".</summary>
    private static string ExpectedOffsets(int n)
        => string.Join(", ", Enumerable.Range(0, n)
            .Select(i => (i * MethodShadowStackBuilder.SlotStrideBytes)
                .ToString(CultureInfo.InvariantCulture)));

    // =================================================================
    // X-IL2CPP-STACKMAP-COV: every rooting method emits a file-scope record.
    // =================================================================

    [Fact]
    public void EveryRootingMethod_EmitsFileScopeStackMap_NumLiveRefsMatchesOracle()
    {
        EmitContext ctx = BuildContextWithOracle(XObjectStub, Corpus);

        int rootingMethodsChecked = 0;
        foreach (IMethodSymbol method in AllEmittableMethods(ctx))
        {
            int oracleCount = OraclePrologueRootCount(ctx, method);
            string cpp = EmitMethod(ctx, method);

            if (oracleCount == 0)
            {
                // A method rooting nothing emits NO stack map (a zero-length root
                // array is ill-formed; no live refs means no precise-GC map).
                Assert.DoesNotContain(MethodShadowStackBuilder.StackMapRecordType, cpp);
                Assert.DoesNotContain(MethodShadowStackBuilder.StackMapTableType + "::Register(", cpp);
                continue;
            }

            rootingMethodsChecked++;

            // Exactly one record + one registrar for the method.
            Assert.Equal(1, CountOccurrences(
                cpp, "static const " + MethodShadowStackBuilder.StackMapRecordType));
            Assert.Equal(1, CountOccurrences(
                cpp, MethodShadowStackBuilder.StackMapTableType + "::Register("));

            // numLiveRefs == the oracle's self + parameter root count.
            Assert.Contains(
                ".numLiveRefs = " + oracleCount.ToString(CultureInfo.InvariantCulture) + ",",
                cpp);

            // liveRefOffsets == the packed { 0, 8, 16, ... } byte offsets.
            Assert.Contains(".liveRefOffsets = { " + ExpectedOffsets(oracleCount) + " },", cpp);

            // One full PC range.
            Assert.Contains(".pcRangeBegin = 0,", cpp);
            Assert.Contains(".pcRangeEnd = " + MethodShadowStackBuilder.FullPcRangeEnd + ",", cpp);
        }

        // The corpus must contain rooting methods (sanity on the gate itself).
        Assert.True(rootingMethodsChecked >= 9,
            $"Expected at least 9 rooting methods in the corpus; checked {rootingMethodsChecked}.");
    }

    [Fact]
    public void StackMapRecord_And_Registrar_AreEmittedAtFileScope_AfterClosingBrace()
    {
        // THE placement-fix regression guard: a rooting Tier-2 method whose body
        // ends in a return. The record + registrar must appear AFTER the
        // function's closing brace (file scope, reachable at static-init), NOT
        // inside the body (where they would be dead code after the return).
        EmitContext ctx = BuildContextWithOracle(XObjectStub, Corpus);
        IMethodSymbol method = FindMethod(ctx, "Compute"); // Tier-2 leaf, returns x.
        Assert.Equal(FunctionTier.Tier2, ctx.FindTier(StableId.FromSymbol(method)));

        string cpp = EmitMethod(ctx, method);
        string[] lines = cpp.Split('\n');

        int recordLine = System.Array.FindIndex(
            lines, l => l.Contains("static const " + MethodShadowStackBuilder.StackMapRecordType));
        int registrarLine = System.Array.FindIndex(
            lines, l => l.Contains(MethodShadowStackBuilder.StackMapTableType + "::Register("));
        Assert.True(recordLine >= 0, "No FStackMapRecord emitted.");
        Assert.True(registrarLine >= 0, "No registrar emitted.");

        // The function body's closing brace is the FIRST line that is exactly "}"
        // at column 0 (the extern "C" function closes at file-scope indent 0).
        int closeBraceLine = System.Array.FindIndex(lines, l => l == "}");
        Assert.True(closeBraceLine >= 0, "No file-scope function closing brace found.");

        // Record + registrar are AFTER the function's closing brace (file scope).
        Assert.True(recordLine > closeBraceLine,
            $"FStackMapRecord (line {recordLine}) must be AFTER the function close brace (line {closeBraceLine}).");
        Assert.True(registrarLine > closeBraceLine,
            $"Registrar (line {registrarLine}) must be AFTER the function close brace (line {closeBraceLine}).");

        // The record + registrar are wrapped in the per-function namespace (so
        // the fixed _stackMap / _stackMapReg names never collide at file scope).
        Assert.Contains("namespace " + MethodEmitter.StackMapNamespacePrefix, cpp);

        // The registrar is a FILE-scope static (runs at static-init) -- NOT a
        // body-local static after the body's return. The lambda form is retained.
        Assert.Contains("[[maybe_unused]] static const bool", cpp);
        Assert.Contains("return true;", cpp);
    }

    [Fact]
    public void Tier1_StackMap_IsKeyedOffBodySymbol_AtFileScope()
    {
        // A Tier-1 (public, rooting) method: the stack map is keyed off the _Body
        // symbol (the function that carries the rooted body) and emitted at file
        // scope after the _Body helper's closing brace.
        EmitContext ctx = BuildContextWithOracle(XObjectStub, Corpus);
        IMethodSymbol method = FindMethod(ctx, "SelfPlusOne");
        Assert.Equal(FunctionTier.Tier1, ctx.FindTier(StableId.FromSymbol(method)));

        string cpp = EmitMethod(ctx, method);
        ManglingRecord record = ctx.FindMangling(StableId.FromSymbol(method))!.Value;

        // The registrar takes the address of the _Body symbol.
        Assert.Contains(
            MethodShadowStackBuilder.StackMapTableType + "::Register(reinterpret_cast<unsigned long long>(&"
            + record.LinkerSymbol + MethodEmitter.BodySuffix + ")",
            cpp);

        // The record + registrar are at file scope: after a closing brace, before
        // the exported _Shim boundary is fine -- but they must be OUTSIDE the
        // _Body block. The _Body block close brace precedes the record line.
        string[] lines = cpp.Split('\n');
        int bodyCloseBrace = System.Array.FindIndex(lines, l => l == "}");
        int recordLine = System.Array.FindIndex(
            lines, l => l.Contains("static const " + MethodShadowStackBuilder.StackMapRecordType));
        Assert.True(recordLine > bodyCloseBrace,
            "The Tier-1 stack-map record must be emitted at file scope after the _Body closing brace.");
    }

    // =================================================================
    // X-IL2CPP-SHADOWSTACK-COV: every rooted ref has a slot write.
    // =================================================================

    [Fact]
    public void EveryRootedReference_HasAShadowStackSlotWrite()
    {
        EmitContext ctx = BuildContextWithOracle(XObjectStub, Corpus);

        foreach (IMethodSymbol method in AllEmittableMethods(ctx))
        {
            int oracleCount = OraclePrologueRootCount(ctx, method);
            if (oracleCount == 0)
            {
                continue;
            }

            string cpp = EmitMethod(ctx, method);

            // The shadow-stack array is declared with exactly N = oracleCount slots
            // (with the empty body-rule set no further locals are rooted, so the
            // array length is the prologue root count).
            Assert.Contains(
                MethodShadowStackBuilder.SlotElementType + " " + MethodShadowStackBuilder.ArrayName
                + "[" + oracleCount.ToString(CultureInfo.InvariantCulture) + "] = {};",
                cpp);

            // Every prologue-rooted reference (self + each XObject param) has a
            // _liveRefs[i] slot write whose expression is the symbol it roots.
            foreach (LiveLocalRecord root in OracleRoots(ctx, method)
                .Where(r => r.Kind != LiveLocalKind.Local))
            {
                string expr = root.Kind == LiveLocalKind.SelfReceiver
                    ? MethodEmitter.SelfParamName
                    : root.SymbolDisplay;

                string expectedWrite =
                    MethodShadowStackBuilder.ArrayName + "["
                    + root.DeclarationIndex.ToString(CultureInfo.InvariantCulture) + "] = "
                    + MethodShadowStackBuilder.SlotElementType + "(reinterpret_cast<"
                    + MethodShadowStackBuilder.XObjectPointerType + ">(" + expr + "));";

                Assert.Contains(expectedWrite, cpp);
            }

            // The slot-write count equals the prologue root count (no missing /
            // spurious slot writes).
            Assert.Equal(oracleCount, CountOccurrences(
                cpp,
                MethodShadowStackBuilder.ArrayName + "[")
                // Subtract the array-declaration occurrence "_liveRefs[N]".
                - 1);
        }
    }

    [Fact]
    public void SelfRoot_IsAlwaysSlotZero()
    {
        EmitContext ctx = BuildContextWithOracle(XObjectStub, Corpus);
        IMethodSymbol method = FindMethod(ctx, "SelfPlusTwo"); // self + 2 params = 3.

        string cpp = EmitMethod(ctx, method);

        Assert.Contains(
            MethodShadowStackBuilder.ArrayName + "[0] = " + MethodShadowStackBuilder.SlotElementType
            + "(reinterpret_cast<" + MethodShadowStackBuilder.XObjectPointerType + ">("
            + MethodEmitter.SelfParamName + "));",
            cpp);
    }

    // =================================================================
    // Determinism.
    // =================================================================

    [Fact]
    public void Emit_IsByteDeterministic_AcrossRuns()
    {
        EmitContext ctx = BuildContextWithOracle(XObjectStub, Corpus);
        IMethodSymbol method = FindMethod(ctx, "Five");

        Assert.Equal(EmitMethod(ctx, method), EmitMethod(ctx, method));
    }
}
