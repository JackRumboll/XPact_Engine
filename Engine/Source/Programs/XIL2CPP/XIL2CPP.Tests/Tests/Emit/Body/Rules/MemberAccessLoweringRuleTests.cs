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
/// Tests for <see cref="MemberAccessLoweringRule"/>: a field on a
/// reference-typed receiver lowers to the C++ arrow form; a field on a
/// value-typed receiver lowers to the C++ dot form; and a property read lowers
/// to its <c>get_</c> accessor linker-symbol call with the receiver threaded as
/// the explicit <c>self</c> argument.
/// </summary>
public sealed class MemberAccessLoweringRuleTests
{
    [Fact]
    public void Field_ReferenceTypedReceiver_EmitsArrow()
    {
        const string source = """
            namespace Game
            {
                public class Node
                {
                    public int Value;
                }

                public class Caller
                {
                    public int Read(Node n) { return n.Value; }
                }
            }
            """;

        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        MemberAccessExpressionSyntax access = FirstMemberAccess(ctx, "Value");
        string output = Lower(ctx, access);

        // The receiver `n` (no rule) lowers to the gap marker, then `->Value`.
        Assert.Contains("->Value", output);
        Assert.DoesNotContain(".Value", output);
    }

    [Fact]
    public void Field_ValueTypedReceiver_EmitsDot()
    {
        const string source = """
            namespace Game
            {
                public struct Vec
                {
                    public int X;
                }

                public class Caller
                {
                    public int Read(Vec v) { return v.X; }
                }
            }
            """;

        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        MemberAccessExpressionSyntax access = FirstMemberAccess(ctx, "X");
        string output = Lower(ctx, access);

        // A value-typed receiver lowers to the dot form (`recv.X`), never arrow.
        Assert.Contains(".X", output);
        Assert.DoesNotContain("->X", output);
    }

    [Fact]
    public void Property_EmitsGetterLinkerSymbolCall_WithReceiverSelf()
    {
        const string source = """
            namespace Game
            {
                public class Widget
                {
                    public int Health { get; set; }
                }

                public class Caller
                {
                    public int Read(Widget w) { return w.Health; }
                }
            }
            """;

        EmitContext ctx = EmitTestHelpers.BuildEmitContext(source);
        MemberAccessExpressionSyntax access = FirstMemberAccess(ctx, "Health");
        string output = Lower(ctx, access);

        string getterSymbol = GetterLinkerSymbol(ctx, access);
        // <get_LinkerSymbol>(<recv>) -- the receiver is the self argument; the
        // un-lowered `w` identifier shows up as the gap marker inside the parens.
        Assert.StartsWith(getterSymbol + "(", output);
        Assert.EndsWith(")", output);
        Assert.Contains("TODO(6.e):", output); // the un-lowered `w` receiver
        Assert.DoesNotContain("->Health", output);
        Assert.DoesNotContain(".Health", output);
    }

    private static string Lower(EmitContext ctx, SyntaxNode node)
    {
        BodyLoweringRuleRegistry registry = new(new IBodyLoweringRule[] { new MemberAccessLoweringRule() });
        CppWriter writer = new();
        StatementEmitter emitter = new(ctx, writer, registry);
        emitter.Expressions.EmitExpression(node);
        return writer.Build();
    }

    private static MemberAccessExpressionSyntax FirstMemberAccess(EmitContext ctx, string memberName)
        => ctx.Unit.Pass1.ParsedFiles[0].Tree
            .GetRoot()
            .DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .First(m => m.Name.Identifier.ValueText == memberName);

    private static string GetterLinkerSymbol(EmitContext ctx, MemberAccessExpressionSyntax access)
    {
        SemanticModel model = ctx.GetSemanticModel(access.SyntaxTree);
        var property = (IPropertySymbol)model.GetSymbolInfo(access).Symbol!;
        ManglingRecord record = ctx.FindMangling(StableId.FromSymbol(property.GetMethod!))!.Value;
        return record.LinkerSymbol;
    }
}
