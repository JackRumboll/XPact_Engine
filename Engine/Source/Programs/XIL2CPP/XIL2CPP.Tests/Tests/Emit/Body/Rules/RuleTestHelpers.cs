// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Emit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit.Body.Rules;

/// <summary>
/// Shared helpers for the WU-D5 body-lowering-rule tests: build an
/// <see cref="EmitContext"/> over inline C# source (through the real pipeline),
/// extract the first syntax node of a requested kind from the context's first
/// parsed tree, drive a single rule through a <see cref="StatementEmitter"/>
/// whose <see cref="BodyLoweringRuleRegistry"/> contains ONLY that rule, and
/// return the emitted C++ text.
/// </summary>
internal static class RuleTestHelpers
{
    /// <summary>
    /// Build an <see cref="EmitContext"/> over <paramref name="source"/> and
    /// return it alongside the first node of type <typeparamref name="TNode"/>
    /// found (document order) in the context's first parsed tree.
    /// </summary>
    public static (EmitContext Context, TNode Node) ContextAndNode<TNode>(string source)
        where TNode : SyntaxNode
    {
        EmitContext context = EmitTestHelpers.BuildEmitContext(source);
        SyntaxTree tree = context.Unit.Pass1.ParsedFiles[0].Tree;
        TNode node = tree.GetRoot().DescendantNodes().OfType<TNode>().First();
        return (context, node);
    }

    /// <summary>
    /// Emit <paramref name="node"/> through <paramref name="rule"/> (the only
    /// rule in the registry) against <paramref name="context"/> and return the
    /// built C++ text.
    /// </summary>
    public static string EmitWith(IBodyLoweringRule rule, EmitContext context, SyntaxNode node)
    {
        BodyLoweringRuleRegistry registry = new(new[] { rule });
        CppWriter writer = new();
        StatementEmitter emitter = new(context, writer, registry);
        emitter.Expressions.EmitExpression(node);
        return writer.Build();
    }
}
