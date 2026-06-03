// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Tiering;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Lowers a C# method-call (<see cref="InvocationExpressionSyntax"/>) to the
/// transpiled free-function call form per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.2 +
/// <c>/Documents/XToolchainContract.html</c> Section 2.3: the callee resolves to
/// its Pass-5 linker symbol and is invoked as a free function whose first
/// argument is the explicit <c>self</c> receiver for an instance method (a
/// static method omits it):
/// <c>&lt;LinkerSymbol&gt;(&lt;self&gt;, &lt;args...&gt;)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Receiver as the first <c>self</c> argument.</b> Section 2.3's
/// free-function ABI threads the instance receiver as the first positional
/// argument. The rule recovers the receiver from the call's
/// <see cref="MemberAccessExpressionSyntax"/> target and lowers it through the
/// parent expression emitter (so a chained / nested receiver lowers
/// recursively); a static call has no receiver argument. A bare
/// (this-relative) instance call has no explicit receiver syntax, so the rule
/// emits <c>this</c> as the self argument.
/// </para>
/// <para>
/// <b>Argument recursion.</b> Every supplied argument is lowered through
/// <see cref="ExpressionEmitter.EmitExpression"/> so each argument's own
/// lowering rule runs, keeping the shared context / writer / rule set threaded.
/// </para>
/// <para>
/// <b>Deferral to <see cref="XObjectNewLoweringRule"/>.</b> The
/// <c>XObject.New&lt;T&gt;</c> factory is also an
/// <see cref="InvocationExpressionSyntax"/> but has no transpiled body (it is a
/// BCL intrinsic mapped to <c>NewObject&lt;T'&gt;</c>). The registry dispatch
/// is by <see cref="Name"/> ordinal, and <c>"InvocationLowering"</c> sorts
/// BEFORE <c>"XObjectNewLowering"</c>, so this rule is the one the registry
/// selects for EVERY invocation; <see cref="CanHandle"/> cannot distinguish the
/// factory (it has no semantic model). The disambiguation therefore happens in
/// <see cref="Emit"/>: it consults <see cref="XObjectNewRecognizer.IsXObjectNew"/>
/// (which DOES have the model) and delegates the factory shape to
/// <see cref="XObjectNewLoweringRule"/>, so the two rules stay byte-identical on
/// the recognition boundary.
/// </para>
/// <para>
/// <b>Missing-mangling fallback.</b> When the callee has no Pass-5 mangling row
/// (e.g. an un-transpiled BCL surface method) the rule emits the deliberate
/// <c>// TODO(6.e)</c> gap marker rather than a wrong symbol, so the gap is
/// visible and never silently incorrect.
/// </para>
/// </remarks>
public sealed class InvocationLoweringRule : IBodyLoweringRule
{
    /// <summary>The C++ <c>self</c> receiver for a bare (this-relative) instance call.</summary>
    public const string ThisSelf = "this";

    /// <inheritdoc/>
    public string Name => "InvocationLowering";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node is InvocationExpressionSyntax;
    }

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        var invocation = (InvocationExpressionSyntax)node;
        SemanticModel model = context.GetSemanticModel(invocation.SyntaxTree);

        // Defer the XObject.New<T> factory to its dedicated rule. (CanHandle
        // already excludes it for the registry dispatch path; this guard also
        // covers a direct Emit call.)
        if (XObjectNewRecognizer.IsXObjectNew(invocation, model))
        {
            new XObjectNewLoweringRule().Emit(node, context, writer, parent);
            return;
        }

        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
        {
            parent.EmitTodo(node);
            return;
        }

        StableId id = StableId.FromSymbol(method);
        ManglingRecord? mangling = context.FindMangling(id);
        if (mangling is null)
        {
            parent.EmitTodo(node);
            return;
        }

        writer.Append(mangling.Value.LinkerSymbol);
        writer.Append("(");

        bool first = true;

        // Instance call: thread the receiver as the first `self` argument.
        if (!method.IsStatic)
        {
            EmitReceiver(invocation, parent);
            first = false;
        }

        ArgumentListSyntax argList = invocation.ArgumentList;
        for (int i = 0; i < argList.Arguments.Count; i++)
        {
            if (!first)
            {
                writer.Append(", ");
            }
            first = false;
            parent.Expressions.EmitExpression(argList.Arguments[i].Expression);
        }

        writer.Append(")");
    }

    private static void EmitReceiver(InvocationExpressionSyntax invocation, StatementEmitter parent)
    {
        // The receiver is the expression to the left of the `.` in a member
        // access (obj.Method()) -- lower it recursively. A bare instance call
        // (Method() inside the type) has no explicit receiver syntax, so the
        // implicit `this` is the self argument.
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            parent.Expressions.EmitExpression(memberAccess.Expression);
        }
        else
        {
            parent.Writer.Append(ThisSelf);
        }
    }
}
