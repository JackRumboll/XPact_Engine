// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using DiagnosticSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Analysis;

/// <summary>
/// The kind of reflection-consumption site
/// <see cref="ReflectionConsumptionAnalyzer"/> records for Pass-6 emit per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.11.
/// </summary>
public enum ReflectionSiteKind
{
    /// <summary>
    /// A <c>typeof(SomeClosedType)</c> expression that resolves at compile
    /// time to a target type's <c>StaticClass()</c> (a <c>const FClass*</c>).
    /// Includes <c>typeof(SomeOpenGeneric&lt;&gt;)</c> (the unbound generic
    /// type definition), which resolves via
    /// <c>XReflectionRuntime::FindClass</c>.
    /// </summary>
    TypeOf,

    /// <summary>
    /// An <c>obj.GetType()</c> call that lowers to a virtual dispatch through
    /// XObject's <c>GetClass()</c> function-pointer slot (NOT a managed
    /// reflection API).
    /// </summary>
    GetType,
}

/// <summary>
/// One recorded reflection-consumption site per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.11. A
/// <see cref="ReflectionSiteKind.TypeOf"/> site resolves to a target
/// <c>FClass</c> (the closed type whose <c>StaticClass()</c> the emit calls);
/// a <see cref="ReflectionSiteKind.GetType"/> site lowers to the virtual
/// <c>GetClass()</c> dispatch and so carries no statically-known target type.
/// </summary>
/// <param name="Kind">Whether the site is a <c>typeof</c> or an <c>obj.GetType()</c>.</param>
/// <param name="TargetTypeDisplay">
/// For a <see cref="ReflectionSiteKind.TypeOf"/> site, the fully-qualified
/// display string of the closed target type the site resolves to (the FClass
/// the emit calls <c>StaticClass()</c> on). Null for a
/// <see cref="ReflectionSiteKind.GetType"/> site (resolved dynamically at
/// game time through the virtual dispatch).
/// </param>
/// <param name="Span">The 1-based source span of the originating expression.</param>
public sealed record ReflectionSite(
    ReflectionSiteKind Kind,
    string? TargetTypeDisplay,
    SourceSpan Span);

/// <summary>
/// Pass-3 reflection-consumption analyzer (WU-22) per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.11 (Q7 resolution) +
/// Section 4.3 (Pass-3 reflection-consumption sub-step).
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-side reflection (recorded, supported in MVP).</b>
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>typeof(SomeClosedType)</c> (or <c>typeof(OpenGeneric&lt;&gt;)</c>,
///     the unbound generic-type definition) resolves at emit time to a
///     <c>const FClass*</c> via the target's <c>StaticClass()</c> /
///     <c>XReflectionRuntime::FindClass</c>. Recorded as a
///     <see cref="ReflectionSite"/> with kind
///     <see cref="ReflectionSiteKind.TypeOf"/>.
///   </description></item>
///   <item><description>
///     <c>typeof(T)</c> at an open-generic site (the operand contains an
///     unsubstituted type parameter, e.g. a bare type parameter <c>T</c> or
///     <c>List&lt;T&gt;</c>) cannot be resolved at compile time: emits
///     <c>XIL2CPP054</c>.
///   </description></item>
///   <item><description>
///     <c>obj.GetType()</c> lowers to a virtual <c>GetClass()</c> dispatch;
///     recorded as a <see cref="ReflectionSite"/> with kind
///     <see cref="ReflectionSiteKind.GetType"/>.
///   </description></item>
/// </list>
/// <para>
/// <b>Write-side / dynamic reflection (banned, diagnosed).</b>
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>Type.GetMethods()</c> / <c>Type.InvokeMember()</c> /
///     <c>MethodInfo.Invoke()</c> &#8594; <c>XIL2CPP050</c>.
///   </description></item>
///   <item><description>
///     <c>System.Activator.CreateInstance(...)</c> &#8594; <c>XIL2CPP051</c>
///     (banned on all TUs in MVP).
///   </description></item>
///   <item><description>
///     Any <c>System.Reflection.Emit</c> usage &#8594; <c>XIL2CPP052</c>
///     (permanently banned; no managed runtime).
///   </description></item>
///   <item><description>
///     <c>Type.MakeGenericType(...)</c> /
///     <c>MethodInfo.MakeGenericMethod(...)</c> &#8594; <c>XIL2CPP053</c>.
///   </description></item>
/// </list>
/// <para>
/// <b>Determinism.</b> Walks the unit's parsed trees in canonical
/// (ordinal) order, then each tree in document (descendant) order, binding
/// against the authoritative Pass-1 semantic model. No ambient state.
/// </para>
/// </remarks>
public sealed class ReflectionConsumptionAnalyzer : ISemanticAnalyzer
{
    private const string SystemNamespace = "System";
    private const string ReflectionNamespace = "System.Reflection";
    private const string ReflectionEmitNamespace = "System.Reflection.Emit";

    /// <inheritdoc/>
    public string Name => "ReflectionConsumptionAnalyzer";

    /// <inheritdoc/>
    public void Analyze(NormalizedUnit unit, Pass3ResultBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(builder);

        Pass1Result pass1 = unit.Pass1;
        foreach (ModuleParser.ParsedFile parsed in pass1.ParsedFiles)
        {
            SyntaxTree tree = parsed.Tree;
            SemanticModel model = pass1.GetSemanticModel(tree);
            SyntaxNode root = tree.GetRoot();

            foreach (SyntaxNode node in root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case TypeOfExpressionSyntax typeOf:
                        AnalyzeTypeOf(typeOf, model, builder, pass1.ModuleName);
                        break;
                    case InvocationExpressionSyntax invocation:
                        AnalyzeInvocation(invocation, model, builder, pass1.ModuleName);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Analyze a <c>typeof(...)</c> expression: record a closed-type
    /// <see cref="ReflectionSite"/>, or emit <c>XIL2CPP054</c> when the
    /// operand contains an unsubstituted type parameter.
    /// </summary>
    private static void AnalyzeTypeOf(
        TypeOfExpressionSyntax typeOf,
        SemanticModel model,
        Pass3ResultBuilder builder,
        string moduleName)
    {
        TypeInfo typeInfo = model.GetTypeInfo(typeOf.Type);
        ITypeSymbol? operand = typeInfo.Type;

        // Unresolved binding (error type / no symbol) is left for Pass-1's
        // binder diagnostics; we do not invent a reflection site for it.
        if (operand is null || operand.TypeKind == TypeKind.Error)
        {
            return;
        }

        if (ContainsTypeParameter(operand))
        {
            // typeof(T) / typeof(List<T>) at an open-generic site -- the
            // target FClass cannot be resolved without type substitution.
            SourceSpan span = SpanOf(typeOf);
            builder.AddDiagnostic(span.ToDiagnostic(
                DiagnosticSeverity.Error,
                DiagnosticCodes.TypeofOpenGenericUnresolvable,
                "typeof at an open-generic site cannot be resolved at compile time: "
                    + $"'{Display(operand)}' contains an unsubstituted type parameter. "
                    + "Use typeof on a closed type, or rely on the closed-instantiation walk.",
                moduleName));
            return;
        }

        // Closed type (including an unbound generic type definition such as
        // typeof(List<>), which resolves via XReflectionRuntime::FindClass).
        builder.Add(new ReflectionSite(
            ReflectionSiteKind.TypeOf,
            Display(operand),
            SpanOf(typeOf)));
    }

    /// <summary>
    /// Analyze an invocation: classify it as <c>obj.GetType()</c> (recorded),
    /// a banned dynamic-reflection call (<c>XIL2CPP050/051/053</c>), or a
    /// <c>System.Reflection.Emit</c> usage (<c>XIL2CPP052</c>).
    /// </summary>
    private static void AnalyzeInvocation(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        Pass3ResultBuilder builder,
        string moduleName)
    {
        SymbolInfo symbolInfo = model.GetSymbolInfo(invocation);
        if (symbolInfo.Symbol is not IMethodSymbol method)
        {
            return;
        }

        INamedTypeSymbol? containing = method.ContainingType;
        if (containing is null)
        {
            return;
        }

        // System.Reflection.Emit: any call on a type in that namespace is a
        // dynamic-code-emission usage (XIL2CPP052, permanently banned).
        if (InNamespace(containing, ReflectionEmitNamespace))
        {
            builder.AddDiagnostic(SpanOf(invocation).ToDiagnostic(
                DiagnosticSeverity.Error,
                DiagnosticCodes.ReflectionEmitNotSupported,
                "System.Reflection.Emit is not supported: XPact has no managed runtime; "
                    + "dynamic code emission cannot be transpiled.",
                moduleName));
            return;
        }

        string methodName = method.Name;

        // System.Object.GetType() -> virtual GetClass() dispatch (recorded).
        if (methodName == "GetType" && IsSystemObject(containing) && method.Parameters.Length == 0)
        {
            builder.Add(new ReflectionSite(
                ReflectionSiteKind.GetType,
                TargetTypeDisplay: null,
                SpanOf(invocation)));
            return;
        }

        // System.Activator.CreateInstance(...) -> XIL2CPP051 (all TUs).
        if (methodName == "CreateInstance" && IsType(containing, SystemNamespace, "Activator"))
        {
            builder.AddDiagnostic(SpanOf(invocation).ToDiagnostic(
                DiagnosticSeverity.Error,
                DiagnosticCodes.ActivatorCreateInstanceBanned,
                "Activator.CreateInstance is banned: XPact's pure-transpile model has no "
                    + "managed runtime to host runtime type metadata. Use XObject.New<T> "
                    + "with a compile-time T.",
                moduleName));
            return;
        }

        // System.Type dynamic-reflection surface.
        if (IsType(containing, SystemNamespace, "Type"))
        {
            switch (methodName)
            {
                case "GetMethods":
                case "InvokeMember":
                    builder.AddDiagnostic(SpanOf(invocation).ToDiagnostic(
                        DiagnosticSeverity.Error,
                        DiagnosticCodes.DynamicReflectionInvocationPostMvp,
                        $"Dynamic reflection invocation (Type.{methodName}) is post-MVP. "
                            + "Use compile-time FClass dispatch (typeof / virtual GetClass()).",
                        moduleName));
                    return;
                case "MakeGenericType":
                    builder.AddDiagnostic(SpanOf(invocation).ToDiagnostic(
                        DiagnosticSeverity.Error,
                        DiagnosticCodes.MakeGenericTypeNotSupported,
                        "Type.MakeGenericType is not supported: runtime generic instantiation "
                            + "requires a managed runtime, which XPact does not host.",
                        moduleName));
                    return;
            }
        }

        // System.Reflection.MethodBase (MethodInfo derives from it):
        // Invoke -> XIL2CPP050; MakeGenericMethod -> XIL2CPP053.
        if (IsMethodBaseDerived(containing))
        {
            switch (methodName)
            {
                case "Invoke":
                    builder.AddDiagnostic(SpanOf(invocation).ToDiagnostic(
                        DiagnosticSeverity.Error,
                        DiagnosticCodes.DynamicReflectionInvocationPostMvp,
                        "Dynamic reflection invocation (MethodInfo.Invoke) is post-MVP. "
                            + "Use compile-time FClass dispatch (typeof / virtual GetClass()).",
                        moduleName));
                    return;
                case "MakeGenericMethod":
                    builder.AddDiagnostic(SpanOf(invocation).ToDiagnostic(
                        DiagnosticSeverity.Error,
                        DiagnosticCodes.MakeGenericTypeNotSupported,
                        "MethodInfo.MakeGenericMethod is not supported: runtime generic "
                            + "instantiation requires a managed runtime, which XPact does not host.",
                        moduleName));
                    return;
            }
        }
    }

    /// <summary>
    /// Return true iff <paramref name="type"/> is, or transitively contains
    /// as a type argument / element / pointed-to type, an unsubstituted type
    /// parameter. An <em>unbound</em> generic type definition (e.g.
    /// <c>List&lt;&gt;</c> from <c>typeof(List&lt;&gt;)</c>) is treated as
    /// resolvable (it resolves to the generic-type-definition FClass), so its
    /// placeholder type parameters do NOT count.
    /// </summary>
    private static bool ContainsTypeParameter(ITypeSymbol type)
    {
        switch (type)
        {
            case ITypeParameterSymbol:
                return true;

            case IArrayTypeSymbol array:
                return ContainsTypeParameter(array.ElementType);

            case IPointerTypeSymbol pointer:
                return ContainsTypeParameter(pointer.PointedAtType);

            case INamedTypeSymbol named:
                // typeof(List<>) -- the unbound generic-type definition is
                // statically resolvable; its placeholder parameters are not
                // an unresolved open-generic site.
                if (named.IsUnboundGenericType)
                {
                    return false;
                }
                foreach (ITypeSymbol arg in named.TypeArguments)
                {
                    if (ContainsTypeParameter(arg))
                    {
                        return true;
                    }
                }
                return false;

            default:
                return false;
        }
    }

    /// <summary>Return true iff <paramref name="type"/> is <c>System.Object</c>.</summary>
    private static bool IsSystemObject(INamedTypeSymbol type)
        => type.SpecialType == SpecialType.System_Object;

    /// <summary>
    /// Return true iff <paramref name="type"/> is the named type
    /// <paramref name="metadataName"/> in namespace
    /// <paramref name="namespaceName"/>.
    /// </summary>
    private static bool IsType(INamedTypeSymbol type, string namespaceName, string metadataName)
        => type.MetadataName == metadataName && InNamespace(type, namespaceName);

    /// <summary>
    /// Return true iff <paramref name="type"/> is, or transitively derives
    /// from, <c>System.Reflection.MethodBase</c> (the base of
    /// <c>MethodInfo</c> / <c>ConstructorInfo</c>, which declares
    /// <c>Invoke</c> / <c>MakeGenericMethod</c>).
    /// </summary>
    private static bool IsMethodBaseDerived(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
        {
            if (t.MetadataName == "MethodBase" && InNamespace(t, ReflectionNamespace))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Return true iff <paramref name="type"/>'s containing namespace renders
    /// (dotted) as <paramref name="namespaceName"/>.
    /// </summary>
    private static bool InNamespace(INamedTypeSymbol type, string namespaceName)
        => type.ContainingNamespace is { IsGlobalNamespace: false } ns
            && ns.ToDisplayString() == namespaceName;

    /// <summary>Fully-qualified display string for a bound type symbol.</summary>
    private static string Display(ITypeSymbol type)
        => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    /// <summary>
    /// Build a 1-based <see cref="SourceSpan"/> for the syntax node from the
    /// Roslyn line span (Roslyn is 0-based; SourceSpan is 1-based).
    /// </summary>
    private static SourceSpan SpanOf(SyntaxNode node)
    {
        FileLinePositionSpan lineSpan = node.GetLocation().GetLineSpan();
        LinePosition start = lineSpan.StartLinePosition;
        LinePosition end = lineSpan.EndLinePosition;
        return new SourceSpan(
            lineSpan.Path,
            start.Line + 1,
            start.Character + 1,
            end.Line + 1,
            end.Character + 1,
            validate: true);
    }
}
