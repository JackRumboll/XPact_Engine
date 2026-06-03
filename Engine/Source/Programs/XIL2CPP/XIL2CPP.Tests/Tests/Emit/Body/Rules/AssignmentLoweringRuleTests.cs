// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;
using Simgenics.XPact.XIL2CPP.Frontend;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit.Body.Rules;

/// <summary>
/// Tests for <see cref="AssignmentLoweringRule"/> (WU-D1): a non-reference LHS
/// lowers to the plain <c>lhs = rhs;</c>; a reference (XObject-derived field /
/// property) LHS FIRST emits the Phase 6.h write-barrier hook comment
/// <c>// TODO(6.h): XPACT_GC_STORE(&lt;parent&gt;, &amp;&lt;slot&gt;, &lt;value&gt;)</c>
/// and THEN the plain assignment. Exercised through a real
/// <see cref="StatementEmitter"/> over a registry containing JUST this rule.
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
    public void ReferenceFieldAssign_EmitsWriteBarrierHookThenAssign()
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

        // The hook comment MUST precede the plain assignment.
        int hookIndex = cpp.IndexOf("TODO(6.h): XPACT_GC_STORE", System.StringComparison.Ordinal);
        int assignIndex = cpp.IndexOf(" = ", System.StringComparison.Ordinal);

        Assert.True(hookIndex >= 0, $"expected write-barrier hook comment; got:\n{cpp}");
        Assert.True(assignIndex > hookIndex, $"expected the assignment AFTER the hook; got:\n{cpp}");
        Assert.Contains("&this.Slot", cpp);
        Assert.EndsWith(";\n", cpp);
    }

    [Fact]
    public void ReferencePropertyAssign_EmitsWriteBarrierHook()
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

        Assert.Contains("TODO(6.h): XPACT_GC_STORE", cpp);
        // A bare-identifier (implicit-this) slot uses "this" as the parent.
        Assert.Contains("XPACT_GC_STORE(this, &Slot, a)", cpp);
    }

    [Fact]
    public void ReferenceFieldAssign_HookCarriesParentSlotAndValue()
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

        // parent = receiver (h), slot = the LHS text (h.Slot), value = RHS (a).
        Assert.Contains("XPACT_GC_STORE(h, &h.Slot, a)", cpp);
    }
}
