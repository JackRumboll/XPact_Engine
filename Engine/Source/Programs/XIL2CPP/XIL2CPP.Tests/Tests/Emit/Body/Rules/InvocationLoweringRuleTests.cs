// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Tiering;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit.Body.Rules;

/// <summary>
/// Tests for <see cref="InvocationLoweringRule"/>: a static call lowers to its
/// Pass-5 linker symbol with no self argument; an instance call threads the
/// receiver as the first <c>self</c> argument; supplied arguments recurse
/// through the parent expression emitter; and a callee with no mangling row
/// falls to the deliberate gap marker.
/// </summary>
public sealed class InvocationLoweringRuleTests
{
    [Fact]
    public void StaticCall_EmitsLinkerSymbol_NoSelfArg()
    {
        const string source = """
            namespace Game
            {
                public static class Math2
                {
                    public static int Add(int a, int b) { return a + b; }
                    public static int Caller() { return Add(1, 2); }
                }
            }
            """;

        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        InvocationExpressionSyntax invocation = FirstInvocation(ctx, "Add");
        string output = Lower(ctx, invocation);

        string expected = AddLinkerSymbol(ctx);
        Assert.Equal($"{expected}(", output.Substring(0, expected.Length + 1));
        // No self argument: the first emitted argument is the literal `1`'s
        // (un-lowered) expression, not a receiver. The symbol is followed
        // directly by the argument list.
        Assert.StartsWith(expected + "(", output);
        Assert.EndsWith(")", output);
        Assert.DoesNotContain("this", output);
    }

    [Fact]
    public void InstanceCall_ThreadsReceiverAsFirstSelfArg()
    {
        const string source = """
            namespace Game
            {
                public class Widget
                {
                    public int Compute(int x) { return x; }
                }

                public class Caller
                {
                    public int Go(Widget w) { return w.Compute(5); }
                }
            }
            """;

        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        InvocationExpressionSyntax invocation = FirstInvocation(ctx, "Compute");
        string output = Lower(ctx, invocation);

        string expected = ComputeLinkerSymbol(ctx);
        // The receiver `w` is the first argument; it lowers (no rule for the
        // identifier) to the TODO gap marker, then the lowered argument follows.
        Assert.StartsWith(expected + "(", output);
        Assert.Contains("TODO(6.e):", output); // the un-lowered `w` receiver + `5` arg
        Assert.EndsWith(")", output);
    }

    [Fact]
    public void BareInstanceCall_EmitsThisAsSelf()
    {
        const string source = """
            namespace Game
            {
                public class Widget
                {
                    public int Compute(int x) { return x; }
                    public int Go() { return Compute(7); }
                }
            }
            """;

        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        InvocationExpressionSyntax invocation = FirstInvocation(ctx, "Compute");
        string output = Lower(ctx, invocation);

        string expected = ComputeLinkerSymbol(ctx);
        Assert.StartsWith(expected + "(this", output);
    }

    [Fact]
    public void ArgumentsRecurseThroughExpressionEmitter()
    {
        // With the InvocationLoweringRule as the ONLY discovered-into-test rule,
        // a nested invocation argument lowers through the SAME rule.
        const string source = """
            namespace Game
            {
                public static class Math2
                {
                    public static int Id(int x) { return x; }
                    public static int Caller() { return Id(Id(3)); }
                }
            }
            """;

        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        // The outer Id(Id(3)) invocation.
        InvocationExpressionSyntax outer = ctx.Unit.Pass1.ParsedFiles[0].Tree
            .GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(i => i.ArgumentList.Arguments.Count == 1
                && i.ArgumentList.Arguments[0].Expression is InvocationExpressionSyntax);

        string output = Lower(ctx, outer);

        string idSymbol = LinkerSymbolFor(ctx, "Id");
        // The outer call's symbol then, as its single argument, the inner call's
        // symbol -> the inner invocation recursed through the same rule.
        Assert.StartsWith(idSymbol + "(", output);
        Assert.Contains(idSymbol + "(", output.Substring(idSymbol.Length)); // nested recursion
    }

    [Fact]
    public void NoMangling_EmitsTodoGapMarker()
    {
        // A call to a BCL-surface method that the module never transpiles has no
        // Pass-5 mangling row -> the rule emits the deliberate gap marker.
        const string source = """
            namespace Game
            {
                public class Widget
                {
                    public int Go(string s) { return s.GetHashCode(); }
                }
            }
            """;

        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        InvocationExpressionSyntax invocation = FirstInvocation(ctx, "GetHashCode");
        string output = Lower(ctx, invocation);

        Assert.Contains("TODO(6.e):", output);
        Assert.DoesNotContain("(", output.Replace("TODO(6.e):", string.Empty));
    }

    private static string Lower(EmitContext ctx, SyntaxNode node)
    {
        BodyLoweringRuleRegistry registry = new(new IBodyLoweringRule[] { new InvocationLoweringRule() });
        CppWriter writer = new();
        StatementEmitter emitter = new(ctx, writer, registry);
        emitter.Expressions.EmitExpression(node);
        return writer.Build();
    }

    private static InvocationExpressionSyntax FirstInvocation(EmitContext ctx, string calleeName)
        => ctx.Unit.Pass1.ParsedFiles[0].Tree
            .GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(i => InvocationName(i) == calleeName);

    private static string InvocationName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        IdentifierNameSyntax id => id.Identifier.ValueText,
        _ => string.Empty,
    };

    private static string LinkerSymbolFor(EmitContext ctx, string methodName)
    {
        InvocationExpressionSyntax invocation = FirstInvocation(ctx, methodName);
        SemanticModel model = ctx.GetSemanticModel(invocation.SyntaxTree);
        var method = (IMethodSymbol)model.GetSymbolInfo(invocation).Symbol!;
        ManglingRecord record = ctx.FindMangling(StableId.FromSymbol(method))!.Value;
        return record.LinkerSymbol;
    }

    private static string AddLinkerSymbol(EmitContext ctx) => LinkerSymbolFor(ctx, "Add");

    private static string ComputeLinkerSymbol(EmitContext ctx) => LinkerSymbolFor(ctx, "Compute");
}
