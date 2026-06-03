// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Simgenics.XPact.XIL2CPP.Tiering;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit.Body.Rules;

/// <summary>
/// Tests for <see cref="XObjectNewLoweringRule"/>: <c>XObject.New&lt;T&gt;</c>
/// lowers to <c>::XCore::Reflect::NewObject&lt;X&lt;T&gt;&gt;(outer, name, flags)</c>;
/// on a non-sim-path module NO guard precedes it; on a sim-path module the
/// inline <c>XPACT_CHECK_SL(...)</c> guard precedes the call; and the general
/// <see cref="InvocationLoweringRule"/> defers the factory shape to this rule.
/// </summary>
public sealed class XObjectNewLoweringRuleTests
{
    // A minimal XObject + factory surrogate: a static generic New<T>(...) on a
    // type named XObject, matching the symbol shape the recognizer keys on.
    private const string SimSource = """
        namespace XCore
        {
            public class XObject
            {
                public static T New<T>(XObject outer, int name, int flags) where T : XObject { return default!; }
            }
        }
        namespace Game
        {
            public class Pickup : XCore.XObject { }

            public class Spawner : XCore.XObject
            {
                public XCore.XObject Make()
                {
                    return XCore.XObject.New<Pickup>(this, 1, 0);
                }
            }
        }
        """;

    [Fact]
    public void NonSimPath_EmitsNewObject_NoGuard()
    {
        EmitContext ctx = BuildContext(isSimPath: false, SimSource);
        InvocationExpressionSyntax invocation = FirstNewCall(ctx);
        string output = Lower(ctx, invocation);

        Assert.StartsWith("::XCore::Reflect::NewObject<::Game::XPickup>(", output);
        Assert.EndsWith(")", output);
        Assert.DoesNotContain("XPACT_CHECK_SL", output);
    }

    [Fact]
    public void SimPath_EmitsGuard_ThenNewObject()
    {
        EmitContext ctx = BuildContext(isSimPath: true, SimSource);
        InvocationExpressionSyntax invocation = FirstNewCall(ctx);
        string output = Lower(ctx, invocation);

        string guard =
            "XPACT_CHECK_SL(::XCore::HAL::IsSimPathThread() || !::XCore::HAL::IsSimPathTU(), "
            + "\"sim-path NewObject called from non-SimPathSerialExecutor thread\");\n";

        Assert.StartsWith(guard, output);
        Assert.Contains("::XCore::Reflect::NewObject<::Game::XPickup>(", output);
        // The guard is on its own line strictly before the construction call.
        int guardEnd = output.IndexOf('\n') + 1;
        Assert.StartsWith("::XCore::Reflect::NewObject<", output.Substring(guardEnd));
    }

    [Fact]
    public void TypeArgument_RendersXPrefixedFullyQualifiedName()
    {
        EmitContext ctx = BuildContext(isSimPath: false, SimSource);
        InvocationExpressionSyntax invocation = FirstNewCall(ctx);
        string output = Lower(ctx, invocation);

        // Game.Pickup -> ::Game::XPickup (X rename on the leaf, :: qualified).
        Assert.Contains("<::Game::XPickup>", output);
    }

    [Fact]
    public void InvocationRule_DefersFactoryToThisRule()
    {
        // The general InvocationLoweringRule, when handed the factory call,
        // produces the SAME NewObject output (it delegates to this rule), never
        // a mangled-symbol call.
        EmitContext ctx = BuildContext(isSimPath: false, SimSource);
        InvocationExpressionSyntax invocation = FirstNewCall(ctx);

        BodyLoweringRuleRegistry registry =
            new(new IBodyLoweringRule[] { new InvocationLoweringRule() });
        CppWriter writer = new();
        StatementEmitter emitter = new(ctx, writer, registry);
        emitter.Expressions.EmitExpression(invocation);

        Assert.StartsWith("::XCore::Reflect::NewObject<::Game::XPickup>(", writer.Build());
    }

    [Fact]
    public void Registry_DispatchSelectsInvocationRule_WhichDelegates()
    {
        // With BOTH rules discovered the registry picks InvocationLowering (it
        // sorts before XObjectNewLowering ordinally); it delegates the factory.
        EmitContext ctx = BuildContext(isSimPath: false, SimSource);
        InvocationExpressionSyntax invocation = FirstNewCall(ctx);

        BodyLoweringRuleRegistry registry = new(new IBodyLoweringRule[]
        {
            new InvocationLoweringRule(),
            new XObjectNewLoweringRule(),
        });
        CppWriter writer = new();
        StatementEmitter emitter = new(ctx, writer, registry);
        emitter.Expressions.EmitExpression(invocation);

        Assert.StartsWith("::XCore::Reflect::NewObject<::Game::XPickup>(", writer.Build());
    }

    private static string Lower(EmitContext ctx, InvocationExpressionSyntax node)
    {
        BodyLoweringRuleRegistry registry =
            new(new IBodyLoweringRule[] { new XObjectNewLoweringRule() });
        CppWriter writer = new();
        StatementEmitter emitter = new(ctx, writer, registry);
        emitter.Expressions.EmitExpression(node);
        return writer.Build();
    }

    private static InvocationExpressionSyntax FirstNewCall(EmitContext ctx)
        => ctx.Unit.Pass1.ParsedFiles[0].Tree
            .GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(i => i.Expression is MemberAccessExpressionSyntax m
                && m.Name is GenericNameSyntax g
                && g.Identifier.ValueText == "New");

    private static EmitContext BuildContext(bool isSimPath, params string[] sources)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(isSimPath, sources);
        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        Pass3Result pass3 = Pass3Driver.Run(
            unit, new ISemanticAnalyzer[] { new CrossModuleNoThrowAnalyzer() });
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
}
