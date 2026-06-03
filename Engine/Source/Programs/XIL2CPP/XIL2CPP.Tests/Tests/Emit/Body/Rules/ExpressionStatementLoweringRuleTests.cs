// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;
using Simgenics.XPact.XIL2CPP.Frontend;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit.Body.Rules;

/// <summary>
/// Tests for <see cref="ExpressionStatementLoweringRule"/> (FIX 2): a
/// statement-position expression lowers by routing the inner expression through
/// the shared expression emitter, so an assignment-statement reaches
/// <see cref="AssignmentLoweringRule"/> (an XObject field store now emits the
/// Phase-6.h <c>XPACT_GC_STORE</c> barrier from a method BODY) and an
/// invocation-statement reaches <see cref="InvocationLoweringRule"/>. The rule
/// supplies the statement terminator <c>";\n"</c> for a bare-fragment inner
/// expression, and defers to the assignment rule's own terminator for an
/// assignment (no double <c>;</c>).
/// </summary>
public sealed class ExpressionStatementLoweringRuleTests
{
    private const string XObjectStub =
        "namespace XPact.CoreXObject { public abstract class XObject { } }";

    private static string Lower(params string[] sources)
    {
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(sources);
        ExpressionStatementSyntax stmt = FindFirstExpressionStatement(ctx);

        CppWriter writer = new();
        BodyLoweringRuleRegistry registry = new(new IBodyLoweringRule[]
        {
            new ExpressionStatementLoweringRule(),
            new AssignmentLoweringRule(),
            new InvocationLoweringRule(),
            new MemberAccessLoweringRule(),
            new IdentifierLoweringRule(),
        });
        StatementEmitter emitter = new(ctx, writer, registry);
        emitter.EmitStatement(stmt);
        return writer.Build();
    }

    private static ExpressionStatementSyntax FindFirstExpressionStatement(EmitContext ctx)
    {
        foreach (ModuleParser.ParsedFile parsed in ctx.Unit.Pass1.ParsedFiles)
        {
            ExpressionStatementSyntax? found = parsed.Tree.GetRoot()
                .DescendantNodes()
                .OfType<ExpressionStatementSyntax>()
                .FirstOrDefault();
            if (found is not null)
            {
                return found;
            }
        }

        Assert.Fail("no ExpressionStatementSyntax found in the emit context's parsed files");
        return null!;
    }

    [Fact]
    public void CanHandle_OnlyExpressionStatements()
    {
        IBodyLoweringRule rule = new ExpressionStatementLoweringRule();
        SyntaxTree tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            "class C { void M() { N(); return; } void N() { } }");
        SyntaxNode exprStmt = tree.GetRoot().DescendantNodes().OfType<ExpressionStatementSyntax>().First();
        SyntaxNode retStmt = tree.GetRoot().DescendantNodes().OfType<ReturnStatementSyntax>().First();

        Assert.True(rule.CanHandle(exprStmt));
        Assert.False(rule.CanHandle(retStmt));
        Assert.Equal("ExpressionStatement", rule.Name);
    }

    [Fact]
    public void XObjectFieldStoreStatement_FiresWriteBarrierFromBody()
    {
        // FIX 2: a statement-position XObject field store (`Boss = a;`) now routes
        // through the ExpressionStatement rule to the AssignmentLoweringRule, so
        // the Phase-6.h barrier fires from the method body (no plain store, no
        // double-write, no stray second `;`).
        string cpp = Lower(
            XObjectStub,
            """
            namespace Game
            {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Squad : XObject
                {
                    public Actor Boss;
                    public void Assign(Actor a) { Boss = a; }
                }
            }
            """);

        Assert.Equal("XPACT_GC_STORE(self, &(self->Boss), a);\n", cpp);
        Assert.DoesNotContain("TODO(6.e)", cpp);
        // Exactly ONE terminator (no double `;` from the rule re-terminating an
        // already-statement-form assignment).
        Assert.Single(cpp.Where(c => c == ';'));
    }

    [Fact]
    public void ValueTypedFieldStoreStatement_LowersToPlainAssign_NoBarrier_SingleTerminator()
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

        Assert.Equal("self->_count = n;\n", cpp);
        Assert.DoesNotContain("XPACT_GC_STORE", cpp);
        Assert.Single(cpp.Where(c => c == ';'));
    }

    [Fact]
    public void InvocationStatement_RoutesThroughInvocationRule_AndTerminates()
    {
        // An invocation-statement reaches the InvocationLoweringRule (which emits
        // a bare `symbol(self)` fragment with NO terminator), so THIS rule
        // supplies the statement-terminating `;\n`.
        string cpp = Lower(
            """
            namespace Game
            {
                public class Widget
                {
                    public void Run() { Step(); }
                    public void Step() { }
                }
            }
            """);

        Assert.DoesNotContain("TODO(6.e)", cpp);
        // The invocation lowered to a real free-function call, terminated by this
        // rule's `;\n`.
        Assert.EndsWith(");\n", cpp);
        Assert.Single(cpp.Where(c => c == ';'));
    }

    [Fact]
    public void Discovery_PicksUpExpressionStatementRule()
    {
        IReadOnlyList<IBodyLoweringRule> discovered = BodyLoweringRuleRegistry.DiscoverRules();
        Assert.Contains(discovered, r => r is ExpressionStatementLoweringRule);
    }

    [Fact]
    public void Lower_IsByteDeterministic_OnRepeat()
    {
        string[] sources =
        {
            XObjectStub,
            """
            namespace Game
            {
                using XPact.CoreXObject;
                public class Actor : XObject { }
                public class Squad : XObject
                {
                    public Actor Boss;
                    public void Assign(Actor a) { Boss = a; }
                }
            }
            """,
        };

        Assert.Equal(Lower(sources), Lower(sources));
    }
}
