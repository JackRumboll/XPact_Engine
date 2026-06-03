// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;
using Simgenics.XPact.XIL2CPP.Frontend;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit.Body.Rules;

/// <summary>
/// Tests for <see cref="AssignmentLoweringRule"/> (WU-D1 / WU-6H): a
/// non-reference LHS lowers to the plain <c>lhs = rhs;</c>; a reference
/// (XObject-derived field / property) LHS lowers to the Phase 6.h write barrier
/// <c>XPACT_GC_STORE(&lt;parent&gt;, &amp;(&lt;slot&gt;), &lt;value&gt;);</c>
/// INSTEAD of the plain assignment (the macro performs the store, so no
/// double-write). Exercised through a real <see cref="StatementEmitter"/> over a
/// registry containing this rule plus the member-access / identifier rules so
/// the operands lower to real C++.
/// </summary>
public sealed class AssignmentLoweringRuleTests
{
    // A locally-declared stand-in for the engine root reference type; the
    // metadata-name + namespace fallback in AnalyzerHelpers.IsXObjectDerived
    // recognises it even though the real curated XObject BCL ref is absent.
    private const string XObjectStub =
        "namespace XPact.CoreXObject { public abstract class XObject { } }";

    private static string Lower(params string[] sources)
    {
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(sources);
        AssignmentExpressionSyntax assignment = FindFirstAssignment(ctx);

        CppWriter writer = new();
        BodyLoweringRuleRegistry registry = new(new IBodyLoweringRule[]
        {
            new AssignmentLoweringRule(),
            new Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules.MemberAccessLoweringRule(),
            new Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules.IdentifierLoweringRule(),
        });
        StatementEmitter emitter = new(ctx, writer, registry);

        // The assignment node is an expression; the foundation seam dispatches
        // expressions through the same registry (a rule may handle either).
        emitter.Expressions.EmitExpression(assignment);
        return writer.Build();
    }

    private static AssignmentExpressionSyntax FindFirstAssignment(EmitContext ctx)
    {
        foreach (ModuleParser.ParsedFile parsed in ctx.Unit.Pass1.ParsedFiles)
        {
            AssignmentExpressionSyntax? found = parsed.Tree.GetRoot()
                .DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .FirstOrDefault();
            if (found is not null)
            {
                return found;
            }
        }

        Assert.Fail("no AssignmentExpressionSyntax found in the emit context's parsed files");
        return null!;
    }

    [Fact]
    public void NonReferenceFieldAssign_LowersToPlainAssign_NoHook()
    {
        // A value-typed (int) field store is NOT a reference store -> no hook.
        string cpp = Lower(
            """
            namespace Game
            {
                public class Widget
                {
                    private int _count;
                    public void Set(int n) { _count = n; }
                }
            }
            """);

        Assert.DoesNotContain("XPACT_GC_STORE", cpp);
        Assert.DoesNotContain("TODO(6.h)", cpp);
        Assert.Contains(" = ", cpp);
        Assert.EndsWith(";\n", cpp);
    }

    [Fact]
    public void NonReferenceAssign_EndsWithSemicolonNewline()
    {
        string cpp = Lower(
            """
            namespace Game
            {
                public class Widget
                {
                    private int _count;
                    public void Set(int n) { _count = n; }
                }
            }
            """);

        Assert.EndsWith(";\n", cpp);
        Assert.Contains(" = ", cpp);
        Assert.DoesNotContain("TODO(6.h)", cpp);
    }

    [Fact]
    public void ReferenceFieldAssign_EmitsWriteBarrierInsteadOfPlainAssign()
    {
        string cpp = Lower(
            XObjectStub,
            """
            namespace M
            {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Holder : XObject
                {
                    public Actor Slot;
                    public void Set(Actor a) { this.Slot = a; }
                }
            }
            """);

        // The implicit-`this` field store lowers to the barrier: parent = self,
        // slot = self->Slot, value = a. The plain `self->Slot = a;` assignment
        // is SUPPRESSED (the macro performs the store) -- no double-write.
        Assert.Equal("XPACT_GC_STORE(self, &(self->Slot), a);\n", cpp);
        Assert.DoesNotContain(" = ", cpp);
        Assert.DoesNotContain("TODO(6.h)", cpp);
    }

    [Fact]
    public void ReferencePropertyAssign_EmitsWriteBarrier()
    {
        string cpp = Lower(
            XObjectStub,
            """
            namespace M
            {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Holder : XObject
                {
                    public Actor Slot { get; set; }
                    public void Set(Actor a) { Slot = a; }
                }
            }
            """);

        // A bare-identifier (implicit-this) reference store still emits the
        // barrier with `self` as the parent and `a` as the new value; the plain
        // assignment is suppressed (no double-write).
        Assert.Contains("XPACT_GC_STORE(self, &(", cpp);
        Assert.EndsWith("), a);\n", cpp);
        Assert.DoesNotContain("TODO(6.h)", cpp);
    }

    [Fact]
    public void ReferenceFieldAssign_BarrierCarriesParentSlotAndValue()
    {
        string cpp = Lower(
            XObjectStub,
            """
            namespace M
            {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Holder : XObject
                {
                    public Actor Slot;
                    public void Set(Holder h, Actor a) { h.Slot = a; }
                }
            }
            """);

        // parent = lowered receiver (h), slot = lowered LHS (h->Slot, arrow for
        // the reference-typed receiver), value = lowered RHS (a). No plain
        // assignment (no double-write).
        Assert.Equal("XPACT_GC_STORE(h, &(h->Slot), a);\n", cpp);
        Assert.DoesNotContain(" = ", cpp);
    }
}
