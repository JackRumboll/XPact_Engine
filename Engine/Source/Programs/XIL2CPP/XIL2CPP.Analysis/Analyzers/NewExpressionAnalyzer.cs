// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using CoreSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Analysis;

/// <summary>
/// Pass-3 semantic analyzer enforcing Locked Commitment 3 (the explicit
/// <c>XObject.New&lt;T&gt;</c> factory) at every construction site per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Sections 2.3, 5.3, 6.4, and the
/// Section 12 diagnostic catalog (XIL2CPP001-003).
/// </summary>
/// <remarks>
/// <para>
/// <b>XIL2CPP001 -- new-expression on XObject-derived type.</b> Every
/// <see cref="ObjectCreationExpressionSyntax"/> and
/// <see cref="ImplicitObjectCreationExpressionSyntax"/> whose resolved type
/// transitively derives from the engine root <c>XObject</c>
/// (<see cref="AnalyzerHelpers.IsXObjectDerived(INamedTypeSymbol)"/>) is a
/// banned construction -- UNLESS the construction is lexically inside a
/// member or type marked <c>[XObjectInternalConstructor]</c> (the
/// <c>XObject.New&lt;T&gt;</c> factory / runtime placement-new path, where
/// the diagnostic is suppressed by name). The attribute is matched by its
/// simple name (with or without the <c>Attribute</c> suffix) because the
/// curated XPact BCL is not referenced (Phase 6.b binds against a locally
/// declared stand-in).
/// </para>
/// <para>
/// <b>XIL2CPP002 -- null Outer.</b> A call to the factory
/// <c>XObject.New&lt;T&gt;(outer, name, flags)</c> / <c>TryNew&lt;T&gt;</c>
/// whose <c>outer</c> argument is a compile-time-constant null (Section 6.4:
/// the runtime asserts on a null Outer; XPact lifts the assert to a
/// compile-time error).
/// </para>
/// <para>
/// <b>XIL2CPP003 -- T must be a non-abstract XObject-derived type.</b> A call
/// to the factory whose closed type argument <c>T</c> is abstract OR is not
/// XObject-derived (Sections 2.3, 5.3). XObject itself, being abstract, is
/// caught by the abstract check.
/// </para>
/// <para>
/// <b>Code ownership.</b> This analyzer owns ONLY XIL2CPP001/002/003. Factory
/// invocations are not new-expressions, so the 002/003 checks never collide
/// with the 001 walk; a single factory call may legitimately surface both 002
/// and 003 when both conditions hold (each is recorded as its own violation).
/// </para>
/// <para>
/// <b>Determinism.</b> Parsed files are visited in canonical (ordinal) order;
/// nodes within each tree in document order
/// (<c>SyntaxNode.DescendantNodes</c> is depth-first source order). No
/// ambient state, hashing, <c>DateTime</c>, or
/// <c>Random</c> is consulted (gates X-IL2CPP-MANGLE-DET /
/// X-IL2CPP-CSPATH-DET, Section 9.9).
/// </para>
/// </remarks>
public sealed class NewExpressionAnalyzer : ISemanticAnalyzer
{
    /// <summary>The metadata name of the engine root reference type's static factory class.</summary>
    private const string FactoryClassName = "XObject";

    /// <summary>The factory method name authors call to construct an XObject-derived instance.</summary>
    private const string FactoryNewMethod = "New";

    /// <summary>The fallible-style factory method name (returns Result&lt;T,...&gt;).</summary>
    private const string FactoryTryNewMethod = "TryNew";

    /// <summary>The simple name of the constructor-suppression attribute, without the <c>Attribute</c> suffix.</summary>
    private const string InternalConstructorAttributeName = "XObjectInternalConstructor";

    /// <inheritdoc/>
    public string Name => "NewExpressionAnalyzer";

    /// <inheritdoc/>
    public void Analyze(NormalizedUnit unit, Pass3ResultBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(builder);

        Pass1Result pass1 = unit.Pass1;
        foreach (ModuleParser.ParsedFile parsed in pass1.ParsedFiles)
        {
            SemanticModel model = pass1.GetSemanticModel(parsed.Tree);
            SyntaxNode root = parsed.Tree.GetRoot();

            foreach (SyntaxNode node in root.DescendantNodes())
            {
                switch (node)
                {
                    case ObjectCreationExpressionSyntax explicitCreation:
                        AnalyzeNewExpression(explicitCreation, explicitCreation.Type, model, builder);
                        break;

                    case ImplicitObjectCreationExpressionSyntax implicitCreation:
                        // 'new()' / 'new(args)': the type comes from the target,
                        // not a Type node; anchor the diagnostic on the keyword.
                        AnalyzeNewExpression(implicitCreation, typeSyntax: null, model, builder);
                        break;

                    case InvocationExpressionSyntax invocation:
                        AnalyzeFactoryInvocation(invocation, model, builder);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Emit XIL2CPP001 for a new-expression whose resolved type is
    /// XObject-derived and which is NOT inside an
    /// <c>[XObjectInternalConstructor]</c>-marked member or type.
    /// </summary>
    /// <param name="creation">The new-expression syntax (explicit or implicit).</param>
    /// <param name="typeSyntax">The explicit type syntax to anchor on, or null for an implicit 'new()'.</param>
    /// <param name="model">The authoritative semantic model for the owning tree.</param>
    /// <param name="builder">The shared Pass-3 result builder.</param>
    private static void AnalyzeNewExpression(
        ExpressionSyntax creation,
        TypeSyntax? typeSyntax,
        SemanticModel model,
        Pass3ResultBuilder builder)
    {
        // The constructed type is the static type of the creation expression.
        // This resolves both explicit ('new Foo()') and target-typed implicit
        // ('new()') creations, and is robust to object/collection initializers.
        if (model.GetTypeInfo(creation).Type is not INamedTypeSymbol createdType
            || !AnalyzerHelpers.IsXObjectDerived(createdType))
        {
            return;
        }

        // Locked Commitment 3 suppression: the factory / runtime placement-new
        // path is the only sanctioned construction site for XObject-derived
        // types, identified by the [XObjectInternalConstructor] attribute on
        // an enclosing member or type.
        if (IsInsideInternalConstructor(creation))
        {
            return;
        }

        SourceSpan span = ToSpan(typeSyntax ?? creation);
        string typeName = createdType.Name;
        builder.AddDiagnostic(span.ToDiagnostic(
            CoreSeverity.Error,
            DiagnosticCodes.NewExpressionOnXObjectDerived,
            $"new-expression on XObject-derived type '{typeName}'; use XObject.New<{typeName}>(outer, name, flags) instead."));

        builder.Add(new NewExpressionViolation(
            DiagnosticCodes.NewExpressionOnXObjectDerived,
            NewExpressionRule.NewOnXObjectDerived,
            typeName,
            span));
    }

    /// <summary>
    /// Emit XIL2CPP002 (null Outer) and/or XIL2CPP003 (abstract /
    /// non-XObject T) for a call to the <c>XObject.New&lt;T&gt;</c> /
    /// <c>TryNew&lt;T&gt;</c> factory.
    /// </summary>
    /// <param name="invocation">The candidate invocation syntax.</param>
    /// <param name="model">The authoritative semantic model for the owning tree.</param>
    /// <param name="builder">The shared Pass-3 result builder.</param>
    private static void AnalyzeFactoryInvocation(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        Pass3ResultBuilder builder)
    {
        // Resolve the called method. When the call violates a binder
        // constraint (e.g. the real BCL's 'where T : XObject' is unsatisfied),
        // Roslyn leaves Symbol null but exposes the intended overload as a
        // single candidate; fall back to it so the 003 / 002 checks still fire.
        SymbolInfo symbolInfo = model.GetSymbolInfo(invocation);
        IMethodSymbol? method = symbolInfo.Symbol as IMethodSymbol;
        if (method is null
            && symbolInfo.CandidateSymbols.Length == 1
            && symbolInfo.CandidateSymbols[0] is IMethodSymbol candidate)
        {
            method = candidate;
        }
        if (method is null)
        {
            return;
        }

        // Identify the factory: a method named New / TryNew declared on the
        // engine root static class XPact.CoreXObject.XObject.
        bool isFactory =
            (method.Name == FactoryNewMethod || method.Name == FactoryTryNewMethod)
            && method.ContainingType is { Name: FactoryClassName } containing
            && AnalyzerHelpers.IsXObjectType(containing);
        if (!isFactory)
        {
            return;
        }

        // The closed type argument T. A generic factory always has exactly one
        // type parameter; guard defensively against malformed binding.
        ITypeSymbol? typeArgument = method.TypeArguments.Length == 1
            ? method.TypeArguments[0]
            : null;

        // XIL2CPP003: T must be a non-abstract XObject-derived type. Skip when
        // T is unresolved (error type / open type parameter) -- a malformed
        // tree is a binder diagnostic, not ours.
        if (typeArgument is INamedTypeSymbol namedT)
        {
            bool isDerived = AnalyzerHelpers.IsXObjectDerived(namedT);
            if (namedT.IsAbstract || !isDerived)
            {
                SourceSpan tSpan = ToSpan(invocation.Expression);
                string tName = namedT.Name;
                builder.AddDiagnostic(tSpan.ToDiagnostic(
                    CoreSeverity.Error,
                    DiagnosticCodes.XObjectNewTypeMustBeConcrete,
                    $"XObject.New<{tName}> requires T to be a non-abstract XObject-derived type; please make T concrete or use the abstract base's factory variant."));

                builder.Add(new NewExpressionViolation(
                    DiagnosticCodes.XObjectNewTypeMustBeConcrete,
                    NewExpressionRule.FactoryTypeMustBeConcrete,
                    tName,
                    tSpan));
            }
        }

        // XIL2CPP002: the Outer argument must not be a compile-time-constant
        // null. The Outer is the first parameter of New / TryNew (NewRoot has
        // no Outer and is engine-internal, so it never reaches here as New /
        // TryNew). Bind the outer argument positionally, honouring a named
        // 'outer:' argument.
        ArgumentSyntax? outerArgument = FindOuterArgument(invocation, method);
        if (outerArgument is not null && IsConstantNull(outerArgument.Expression, model))
        {
            SourceSpan outerSpan = ToSpan(outerArgument.Expression);
            string tName = typeArgument?.Name ?? FactoryClassName;
            builder.AddDiagnostic(outerSpan.ToDiagnostic(
                CoreSeverity.Error,
                DiagnosticCodes.XObjectNewNullOuter,
                $"XObject.New<{tName}> called with null Outer; the Outer (ownership parent) must not be null."));

            builder.Add(new NewExpressionViolation(
                DiagnosticCodes.XObjectNewNullOuter,
                NewExpressionRule.FactoryNullOuter,
                tName,
                outerSpan));
        }
    }

    /// <summary>
    /// Find the argument bound to the factory's <c>outer</c> parameter
    /// (parameter index 0 for <c>New</c> / <c>TryNew</c>), honouring a named
    /// <c>outer:</c> argument. Returns null when no such argument is supplied.
    /// </summary>
    private static ArgumentSyntax? FindOuterArgument(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method)
    {
        if (method.Parameters.Length == 0)
        {
            return null;
        }

        string outerParameterName = method.Parameters[0].Name;
        SeparatedSyntaxList<ArgumentSyntax> arguments = invocation.ArgumentList.Arguments;

        // A named argument can appear in any position; prefer an explicit
        // 'outer:' name match.
        foreach (ArgumentSyntax argument in arguments)
        {
            if (argument.NameColon is { } nameColon
                && nameColon.Name.Identifier.ValueText == outerParameterName)
            {
                return argument;
            }
        }

        // Otherwise the first positional argument binds to the first parameter
        // -- but only when no preceding named argument has reordered the list.
        if (arguments.Count > 0 && arguments[0].NameColon is null)
        {
            return arguments[0];
        }

        return null;
    }

    /// <summary>
    /// Return true iff <paramref name="expression"/> is a compile-time
    /// constant null (a bare <c>null</c> literal, a reference-typed
    /// <c>default</c>, or any expression Roslyn folds to a null constant).
    /// </summary>
    private static bool IsConstantNull(ExpressionSyntax expression, SemanticModel model)
    {
        Optional<object?> constant = model.GetConstantValue(expression);
        return constant.HasValue && constant.Value is null;
    }

    /// <summary>
    /// Walk the syntactic ancestor chain from <paramref name="node"/> looking
    /// for an enclosing member (method / constructor / accessor / local
    /// function) or type declaration that carries the
    /// <c>[XObjectInternalConstructor]</c> attribute (matched by simple name).
    /// </summary>
    private static bool IsInsideInternalConstructor(SyntaxNode node)
    {
        for (SyntaxNode? current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is MemberDeclarationSyntax member
                && HasInternalConstructorAttribute(member.AttributeLists))
            {
                return true;
            }

            if (current is LocalFunctionStatementSyntax localFunction
                && HasInternalConstructorAttribute(localFunction.AttributeLists))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Return true iff any attribute in <paramref name="attributeLists"/> has
    /// the simple name <c>XObjectInternalConstructor</c> (with or without the
    /// <c>Attribute</c> suffix). Matched syntactically by name because the
    /// curated BCL declaring the attribute type is not referenced.
    /// </summary>
    private static bool HasInternalConstructorAttribute(SyntaxList<AttributeListSyntax> attributeLists)
    {
        foreach (AttributeListSyntax list in attributeLists)
        {
            foreach (AttributeSyntax attribute in list.Attributes)
            {
                string simpleName = ExtractAttributeSimpleName(attribute.Name);
                if (simpleName == InternalConstructorAttributeName
                    || simpleName == InternalConstructorAttributeName + "Attribute")
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Extract the right-most identifier of an attribute name (stripping any
    /// namespace / containing-type qualification, e.g.
    /// <c>XPact.CoreXObject.XObjectInternalConstructor</c> -&gt;
    /// <c>XObjectInternalConstructor</c>).
    /// </summary>
    private static string ExtractAttributeSimpleName(NameSyntax name) => name switch
    {
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax aliased => aliased.Name.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => name.ToString(),
    };

    /// <summary>
    /// Translate a syntax node's location into a 1-based
    /// <see cref="SourceSpan"/> (Roslyn reports 0-based line / column).
    /// </summary>
    private static SourceSpan ToSpan(SyntaxNode node)
    {
        FileLinePositionSpan lineSpan = node.GetLocation().GetLineSpan();
        LinePosition start = lineSpan.StartLinePosition;
        LinePosition end = lineSpan.EndLinePosition;
        return new SourceSpan(
            file: lineSpan.Path,
            startLine: start.Line + 1,
            startColumn: start.Character + 1,
            endLine: end.Line + 1,
            endColumn: end.Character + 1,
            validate: true);
    }
}

/// <summary>
/// Which Locked-Commitment-3 rule a <see cref="NewExpressionViolation"/>
/// records. Mirrors the XIL2CPP001/002/003 diagnostic codes the
/// <see cref="NewExpressionAnalyzer"/> emits.
/// </summary>
public enum NewExpressionRule
{
    /// <summary>XIL2CPP001: a <c>new</c>-expression on an XObject-derived type.</summary>
    NewOnXObjectDerived,

    /// <summary>XIL2CPP002: an <c>XObject.New&lt;T&gt;</c> call with a null Outer.</summary>
    FactoryNullOuter,

    /// <summary>XIL2CPP003: an <c>XObject.New&lt;T&gt;</c> call whose T is abstract or not XObject-derived.</summary>
    FactoryTypeMustBeConcrete,
}

/// <summary>
/// A recorded Locked-Commitment-3 construction violation the
/// <see cref="NewExpressionAnalyzer"/> appended for emit-metadata + tests:
/// the diagnostic code, the rule it tripped, the offending type's simple
/// name, and the 1-based source span the diagnostic anchored on.
/// </summary>
/// <param name="Code">The XIL2CPP diagnostic code (XIL2CPP001/002/003).</param>
/// <param name="Rule">The rule the site violated.</param>
/// <param name="TypeName">
/// The simple name of the offending type: the constructed XObject-derived
/// type (XIL2CPP001), or the factory's closed type argument T
/// (XIL2CPP002/003).
/// </param>
/// <param name="Span">The 1-based source span the diagnostic anchored on.</param>
public sealed record NewExpressionViolation(
    string Code,
    NewExpressionRule Rule,
    string TypeName,
    SourceSpan Span);
