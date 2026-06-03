// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Analysis.Analyzers;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Simgenics.XPact.XIL2CPP.Tiering;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit;

/// <summary>
/// WU-6H write-barrier-emit tests (gate X-IL2CPP-BARRIER-EMIT), per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 6.3: every XObject-derived
/// reference store -- a field / property assignment lowered by
/// <see cref="AssignmentLoweringRule"/> and an XObject property setter emitted
/// by <see cref="PropertyEmitter"/> -- writes through the
/// <c>XPACT_GC_STORE(parent, &amp;(slot), value);</c> barrier INSTEAD of a plain
/// assignment (the macro performs the store, so emitting the plain assignment
/// too would double-write). A value-typed store carries no barrier.
/// </summary>
/// <remarks>
/// <para>
/// <b>The coverage oracle.</b> The Pass-3
/// <see cref="ReferenceStoreAnalyzer"/> enumerates every XObject reference-store
/// ASSIGNMENT site as a <see cref="ReferenceStoreSite"/>. The
/// <see cref="EveryReferenceStoreSite_LowersToExactlyOneBarrier"/> test uses
/// that set as the oracle: it lowers every assignment in the module through the
/// real <see cref="AssignmentLoweringRule"/> and asserts the emitted
/// <c>XPACT_GC_STORE</c> count equals the oracle's site count (1:1, no missing
/// barrier, no spurious barrier, no double-write).
/// </para>
/// </remarks>
public sealed class WriteBarrierEmissionTests
{
    // A locally-declared stand-in for the engine root reference type; the
    // metadata-name + namespace fallback in AnalyzerHelpers.IsXObjectDerived
    // recognises it even though the real curated XObject BCL ref is absent.
    private const string XObjectStub =
        "namespace XPact.CoreXObject { public abstract class XObject { } }";

    // =================================================================
    // Pipeline + lowering helpers.
    // =================================================================

    /// <summary>
    /// Run the pipeline with the <see cref="ReferenceStoreAnalyzer"/> wired in
    /// (so the Pass-3 result carries the <see cref="ReferenceStoreSite"/> oracle)
    /// and build a fully-populated <see cref="EmitContext"/>.
    /// </summary>
    private static EmitContext BuildContextWithOracle(params string[] sources)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(isSimPath: false, sources);
        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        Pass3Result pass3 = Pass3Driver.Run(
            unit,
            new ISemanticAnalyzer[]
            {
                new ReferenceStoreAnalyzer(),
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

    /// <summary>
    /// Lower a single assignment through a registry containing the assignment
    /// rule plus the member-access / identifier rules (so the operands lower to
    /// real C++).
    /// </summary>
    private static string LowerAssignment(EmitContext ctx, AssignmentExpressionSyntax assignment)
    {
        CppWriter writer = new();
        BodyLoweringRuleRegistry registry = new(new IBodyLoweringRule[]
        {
            new AssignmentLoweringRule(),
            new MemberAccessLoweringRule(),
            new IdentifierLoweringRule(),
        });
        StatementEmitter emitter = new(ctx, writer, registry);
        emitter.Expressions.EmitExpression(assignment);
        return writer.Build();
    }

    private static IReadOnlyList<AssignmentExpressionSyntax> AllAssignments(EmitContext ctx)
    {
        List<AssignmentExpressionSyntax> all = new();
        foreach (ModuleParser.ParsedFile parsed in ctx.Unit.Pass1.ParsedFiles)
        {
            all.AddRange(parsed.Tree.GetRoot()
                .DescendantNodes()
                .OfType<AssignmentExpressionSyntax>());
        }
        return all;
    }

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

    private static IPropertySymbol FindProperty(EmitContext ctx, string name)
    {
        foreach (ModuleParser.ParsedFile parsed in ctx.Unit.Pass1.ParsedFiles)
        {
            SemanticModel model = ctx.GetSemanticModel(parsed.Tree);
            foreach (PropertyDeclarationSyntax decl in parsed.Tree.GetRoot()
                .DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is IPropertySymbol prop
                    && prop.Name == name)
                {
                    return prop;
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"Property '{name}' not found.");
    }

    // =================================================================
    // Assignment-site barrier: XObject field store.
    // =================================================================

    [Fact]
    public void XObjectFieldAssign_EmitsBarrier_AndSuppressesPlainAssign()
    {
        EmitContext ctx = BuildContextWithOracle(
            XObjectStub,
            @"namespace M {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Holder : XObject {
                    public Actor Slot;
                    public void Set(Holder h, Actor a) { h.Slot = a; }
                }
            }");

        AssignmentExpressionSyntax assignment = Assert.Single(AllAssignments(ctx));
        string cpp = LowerAssignment(ctx, assignment);

        // The barrier carries the lowered parent (h), &(slot) (h->Slot), value (a)
        // and REPLACES the plain assignment (no double-write).
        Assert.Equal("XPACT_GC_STORE(h, &(h->Slot), a);\n", cpp);
        Assert.DoesNotContain(" = ", cpp);
    }

    [Fact]
    public void ImplicitThisXObjectFieldAssign_UsesSelfParent()
    {
        EmitContext ctx = BuildContextWithOracle(
            XObjectStub,
            @"namespace M {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Holder : XObject {
                    public Actor Slot;
                    public void Set(Actor a) { this.Slot = a; }
                }
            }");

        AssignmentExpressionSyntax assignment = Assert.Single(AllAssignments(ctx));
        string cpp = LowerAssignment(ctx, assignment);

        // Implicit-this slot: parent = self, slot = self->Slot.
        Assert.Equal("XPACT_GC_STORE(self, &(self->Slot), a);\n", cpp);
    }

    // =================================================================
    // Assignment-site: NON-XObject store -> plain assign, no barrier.
    // =================================================================

    [Fact]
    public void ValueTypedFieldAssign_EmitsPlainAssign_NoBarrier()
    {
        EmitContext ctx = BuildContextWithOracle(
            @"namespace M {
                public class Widget {
                    private int _count;
                    public void Set(int n) { this._count = n; }
                }
            }");

        AssignmentExpressionSyntax assignment = Assert.Single(AllAssignments(ctx));
        string cpp = LowerAssignment(ctx, assignment);

        // A value-typed (int) field store carries no GC obligation: plain assign.
        Assert.DoesNotContain("XPACT_GC_STORE", cpp);
        Assert.Equal("self->_count = n;\n", cpp);
    }

    [Fact]
    public void ValueTypedFieldAssign_IsNotInTheReferenceStoreOracle()
    {
        EmitContext ctx = BuildContextWithOracle(
            @"namespace M {
                public class Widget {
                    private int _count;
                    public void Set(int n) { this._count = n; }
                }
            }");

        // A value-typed store is not a reference store -> oracle is empty.
        Assert.Empty(ctx.Pass3.GetAll<ReferenceStoreSite>());
    }

    // =================================================================
    // Property-setter barrier (PropertyEmitter).
    // =================================================================

    [Fact]
    public void XObjectPropertySetter_WritesBackingFieldThroughBarrier()
    {
        EmitContext ctx = BuildContextWithOracle(
            XObjectStub,
            @"namespace Game {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Widget : XObject { public Actor Target { get; set; } }
            }");

        IPropertySymbol prop = FindProperty(ctx, "Target");

        CppWriter writer = new();
        new PropertyEmitter().EmitSetter(prop, prop.SetMethod!, ctx, writer);
        string output = writer.Build();

        // The setter writes the backing field through the barrier (no plain
        // backing-field store -> no double-write).
        Assert.Contains(
            "XPACT_GC_STORE(self, &(self->__BackingField_Target), value);",
            output);
        Assert.DoesNotContain("__BackingField_Target = value", output);
    }

    [Fact]
    public void ValueTypedPropertySetter_WritesBackingFieldDirectly_NoBarrier()
    {
        EmitContext ctx = BuildContextWithOracle(
            "namespace Game { public class Widget { public int Health { get; set; } } }");

        IPropertySymbol prop = FindProperty(ctx, "Health");

        CppWriter writer = new();
        new PropertyEmitter().EmitSetter(prop, prop.SetMethod!, ctx, writer);
        string output = writer.Build();

        Assert.Contains("self->__BackingField_Health = value;", output);
        Assert.DoesNotContain("XPACT_GC_STORE", output);
    }

    // =================================================================
    // THE coverage gate: ReferenceStoreSite oracle vs emitted barriers.
    // =================================================================

    [Fact]
    public void EveryReferenceStoreSite_LowersToExactlyOneBarrier()
    {
        // A module that mixes XObject field stores, an XObject property store,
        // and value-typed stores -- across several methods -- so the oracle has
        // a non-trivial, mixed-kind site set.
        EmitContext ctx = BuildContextWithOracle(
            XObjectStub,
            @"namespace M {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Holder : XObject {
                    public Actor First;
                    public Actor Second;
                    public Actor Prop { get; set; }
                    private int _count;
                    public void A(Holder h, Actor a) { h.First = a; }
                    public void B(Actor a) { this.Second = a; }
                    public void C(Actor a) { Prop = a; }
                    public void D(int n) { this._count = n; }
                }
            }");

        IReadOnlyList<ReferenceStoreSite> oracle = ctx.Pass3.GetAll<ReferenceStoreSite>();

        // Oracle sanity: three XObject reference stores (First field, Second
        // field, Prop property); the value-typed _count store is NOT a site.
        Assert.Equal(3, oracle.Count);
        Assert.Equal(2, oracle.Count(s => s.SlotKind == ReferenceSlotKind.Field));
        Assert.Equal(1, oracle.Count(s => s.SlotKind == ReferenceSlotKind.Property));

        // Lower every assignment in the module and count the emitted barriers.
        int emittedBarriers = 0;
        foreach (AssignmentExpressionSyntax assignment in AllAssignments(ctx))
        {
            string cpp = LowerAssignment(ctx, assignment);
            int barriers = CountOccurrences(cpp, AssignmentLoweringRule.WriteBarrierMacro);
            emittedBarriers += barriers;

            // A barriered assignment never also emits a plain `lhs = rhs;` (the
            // macro performs the store -- a plain assignment too would
            // double-write).
            if (barriers > 0)
            {
                Assert.DoesNotContain(" = ", cpp);
            }
        }

        // 1:1 count match: every recorded reference store -> exactly one barrier.
        Assert.Equal(oracle.Count, emittedBarriers);
    }

    // =================================================================
    // Determinism.
    // =================================================================

    [Fact]
    public void Barrier_LowersByteIdentically_OnRepeat()
    {
        EmitContext ctx = BuildContextWithOracle(
            XObjectStub,
            @"namespace M {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Holder : XObject {
                    public Actor Slot;
                    public void Set(Holder h, Actor a) { h.Slot = a; }
                }
            }");

        AssignmentExpressionSyntax assignment = Assert.Single(AllAssignments(ctx));
        Assert.Equal(LowerAssignment(ctx, assignment), LowerAssignment(ctx, assignment));
    }
}
