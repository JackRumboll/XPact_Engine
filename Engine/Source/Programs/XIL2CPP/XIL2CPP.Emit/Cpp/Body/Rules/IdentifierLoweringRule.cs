// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Tiering;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Lowers a bare reference expression -- a <see cref="IdentifierNameSyntax"/>,
/// a <see cref="ThisExpressionSyntax"/> (<c>this</c>), or a
/// <see cref="BaseExpressionSyntax"/> (<c>base</c>) -- to the C++ form per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5 (the expression mapping) +
/// <c>/Documents/XToolchainContract.html</c> Section 2.3 (the free-function
/// <c>self</c> form). Without this rule a method body that reads a local, a
/// parameter, or an (implicit-<c>this</c>) field / property lowers to a
/// <c>// TODO(6.e)</c> marker and cannot compile; this is the rule that makes a
/// real (non-literal) body lower + compile.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><description>
///     <b><c>this</c>.</b> A <see cref="ThisExpressionSyntax"/> lowers to the
///     instance receiver token the <see cref="MethodEmitter"/> threads as the
///     free-function first parameter (<see cref="MethodEmitter.SelfParamName"/>,
///     <c>self</c>).
///   </description></item>
///   <item><description>
///     <b><c>base</c>.</b> A <see cref="BaseExpressionSyntax"/> lowers to a
///     pointer cast of the receiver to the base type:
///     <c>static_cast&lt;&lt;BaseTypeCpp&gt;*&gt;(self)</c>. It is only ever a
///     receiver fragment (a <c>base.Member</c> access / <c>base.Method(...)</c>
///     call lowers the rest through the member-access / invocation rules); this
///     rule emits exactly the cast expression.
///   </description></item>
///   <item><description>
///     <b>Local / parameter.</b> A local variable lowers to its C++ local name
///     and a parameter to its C++ parameter name -- the SAME verbatim names
///     <see cref="LocalDeclarationLoweringRule"/> declares locals under and
///     <see cref="MethodEmitter.BuildParameters"/> emits parameters under, so a
///     read resolves to its declaration.
///   </description></item>
///   <item><description>
///     <b>Instance field (implicit <c>this</c>).</b> A bare instance-field read
///     lowers to <c>self-&gt;&lt;field&gt;</c> -- byte-identical to how
///     <see cref="MemberAccessLoweringRule"/> lowers <c>this.&lt;field&gt;</c>
///     (whose <c>this</c> receiver lowers, through this rule, to <c>self</c>),
///     so <c>x</c> and <c>this.x</c> emit the same C++.
///   </description></item>
///   <item><description>
///     <b>Static field / const / enum member.</b> Lowers to the qualified C++
///     name <c>&lt;DeclaringTypeCpp&gt;::&lt;name&gt;</c> (a static field, a
///     <c>const</c> -- which is implicitly static -- and an enum member all share
///     this qualified form; an enum member's declaring type is the enum).
///   </description></item>
///   <item><description>
///     <b>Instance / static property (read bare).</b> Lowers to the property's
///     <c>get_</c> accessor LinkerSymbol call -- consistent with
///     <see cref="MemberAccessLoweringRule"/> -- with the implicit-<c>this</c>
///     receiver threaded as <c>self</c> for an instance property
///     (<c>&lt;get_LinkerSymbol&gt;(self)</c>) and omitted for a static property
///     (<c>&lt;get_LinkerSymbol&gt;()</c>).
///   </description></item>
///   <item><description>
///     <b>Out of scope (method group / type name / namespace).</b> An identifier
///     that binds to a method group, a type, or a namespace is NOT a value
///     reference; lowering it is owned by the invocation / type-reference waves.
///     This rule emits the deliberate <c>// TODO(6.e)</c> marker rather than
///     guessing a symbol.
///   </description></item>
/// </list>
/// <para>
/// <b>Missing-mangling fallback.</b> When a property's getter has no Pass-5
/// mangling row the rule emits the <c>// TODO(6.e)</c> gap marker rather than a
/// wrong symbol (matching <see cref="MemberAccessLoweringRule"/>).
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> Every emitted fragment is a
/// pure function of the bound symbol + the context: ordinal string operations,
/// no clock / <see cref="Guid"/> / <see cref="Random"/> / culture-sensitive
/// formatting.
/// </para>
/// </remarks>
public sealed class IdentifierLoweringRule : IBodyLoweringRule
{
    /// <summary>The instance receiver token (matches <see cref="MethodEmitter.SelfParamName"/>).</summary>
    public const string SelfToken = "self";

    /// <summary>The C++ arrow member-access operator for the implicit-<c>this</c> instance receiver.</summary>
    public const string ArrowOperator = "->";

    /// <summary>The C++ scope-resolution operator for a qualified static / const / enum name.</summary>
    public const string ScopeOperator = "::";

    /// <inheritdoc/>
    public string Name => "Expr.Identifier";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node is IdentifierNameSyntax or ThisExpressionSyntax or BaseExpressionSyntax;
    }

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        switch (node)
        {
            case ThisExpressionSyntax:
                writer.Append(SelfToken);
                return;

            case BaseExpressionSyntax baseExpression:
                EmitBase(baseExpression, context, writer, parent);
                return;

            case IdentifierNameSyntax identifier:
                EmitIdentifier(identifier, context, writer, parent);
                return;

            default:
                parent.EmitTodo(node);
                return;
        }
    }

    /// <summary>
    /// Lower <c>base</c> to <c>static_cast&lt;&lt;BaseTypeCpp&gt;*&gt;(self)</c>.
    /// The cast target is the bound base type; with an unresolved base type the
    /// rule emits the <c>// TODO(6.e)</c> marker rather than a wrong cast.
    /// </summary>
    private static void EmitBase(
        BaseExpressionSyntax baseExpression,
        EmitContext context,
        CppWriter writer,
        StatementEmitter parent)
    {
        SemanticModel model = context.GetSemanticModel(baseExpression.SyntaxTree);
        ITypeSymbol? baseType = model.GetTypeInfo(baseExpression).Type;
        if (baseType is null || baseType.TypeKind == TypeKind.Error)
        {
            parent.EmitTodo(baseExpression);
            return;
        }

        // static_cast<::Ns::Base*>(self) -- `base` is a receiver fragment, so
        // the cast is to a pointer to the base type's C++ name.
        writer.Append("static_cast<");
        writer.Append(CppTypeName.Render(baseType));
        writer.Append("*>(");
        writer.Append(SelfToken);
        writer.Append(")");
    }

    /// <summary>
    /// Lower a bare <see cref="IdentifierNameSyntax"/> per its bound symbol kind
    /// (local / parameter / field / property / out-of-scope), per the rule's
    /// remarks.
    /// </summary>
    private static void EmitIdentifier(
        IdentifierNameSyntax identifier,
        EmitContext context,
        CppWriter writer,
        StatementEmitter parent)
    {
        SemanticModel model = context.GetSemanticModel(identifier.SyntaxTree);
        ISymbol? symbol = model.GetSymbolInfo(identifier).Symbol;

        switch (symbol)
        {
            case ILocalSymbol local:
                // Matches LocalDeclarationLoweringRule's verbatim local name.
                writer.Append(CppName(local.Name));
                return;

            case IParameterSymbol parameter:
                // Matches MethodEmitter.BuildParameters' verbatim parameter name.
                writer.Append(CppName(parameter.Name));
                return;

            case IFieldSymbol field:
                EmitField(field, writer);
                return;

            case IPropertySymbol property:
                EmitProperty(identifier, property, context, writer, parent);
                return;

            default:
                // Method group / type name / namespace / unresolved: not a value
                // reference this rule owns.
                parent.EmitTodo(identifier);
                return;
        }
    }

    /// <summary>
    /// Lower a bare field reference. An instance field (read through the implicit
    /// <c>this</c>) lowers to <c>self-&gt;&lt;field&gt;</c> -- byte-identical to
    /// <see cref="MemberAccessLoweringRule"/>'s <c>this.&lt;field&gt;</c>. A
    /// static field / <c>const</c> / enum member lowers to the qualified
    /// <c>&lt;DeclaringTypeCpp&gt;::&lt;name&gt;</c> form.
    /// </summary>
    private static void EmitField(IFieldSymbol field, CppWriter writer)
    {
        // A const (implicitly static) and an enum member both surface as a
        // static field; their qualified C++ name is the declaring-type-scoped
        // name (the enum member's declaring type is the enum itself).
        if (field.IsStatic || field.IsConst)
        {
            writer.Append(CppTypeName.Render(field.ContainingType));
            writer.Append(ScopeOperator);
            writer.Append(field.Name);
            return;
        }

        // Instance field through the implicit `this`: self-><field>.
        writer.Append(SelfToken);
        writer.Append(ArrowOperator);
        writer.Append(field.Name);
    }

    /// <summary>
    /// Lower a bare property read to its <c>get_</c> accessor LinkerSymbol call
    /// (consistent with <see cref="MemberAccessLoweringRule"/>): the
    /// implicit-<c>this</c> receiver is threaded as <c>self</c> for an instance
    /// property and omitted for a static property. A property with no getter, or
    /// a getter with no Pass-5 mangling row, emits the <c>// TODO(6.e)</c> marker.
    /// </summary>
    private static void EmitProperty(
        IdentifierNameSyntax identifier,
        IPropertySymbol property,
        EmitContext context,
        CppWriter writer,
        StatementEmitter parent)
    {
        IMethodSymbol? getter = property.GetMethod;
        if (getter is null)
        {
            parent.EmitTodo(identifier);
            return;
        }

        StableId id = StableId.FromSymbol(getter);
        ManglingRecord? mangling = context.FindMangling(id);
        if (mangling is null)
        {
            parent.EmitTodo(identifier);
            return;
        }

        // <get_LinkerSymbol>(self) -- the implicit `this` is the explicit self
        // first argument for an instance property; a static property omits it.
        writer.Append(mangling.Value.LinkerSymbol);
        writer.Append("(");
        if (!getter.IsStatic)
        {
            writer.Append(SelfToken);
        }
        writer.Append(")");
    }

    /// <summary>
    /// The C++ identifier for a C# name: emitted verbatim (the C# and C++
    /// identifier grammars agree for the names XIL2CPP accepts), with an empty
    /// name degraded to <c>_</c> to mirror <see cref="MethodEmitter"/>'s
    /// placeholder. A single seam so a future reserved-word escape lands once.
    /// </summary>
    private static string CppName(string name)
        => string.IsNullOrEmpty(name) ? "_" : name;
}
