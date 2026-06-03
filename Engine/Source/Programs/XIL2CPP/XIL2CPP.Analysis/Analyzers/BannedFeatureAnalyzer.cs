// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using RoslynTypeKind = Microsoft.CodeAnalysis.TypeKind;
using XilSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Analysis;

/// <summary>
/// One recorded banned / post-MVP feature site this analyzer flagged, for
/// emit-metadata consumption alongside the diagnostic. The diagnostic is the
/// authoritative build-blocking surface; this record lets later passes (and
/// tests) enumerate the flagged sites by code without re-walking the tree.
/// </summary>
/// <param name="Code">The <see cref="DiagnosticCodes"/> constant emitted for this site.</param>
/// <param name="Detail">A short human-readable detail (the offending type / member / API).</param>
/// <param name="File">Source file path of the site.</param>
/// <param name="Line">1-based source line of the site.</param>
/// <param name="Column">1-based source column of the site.</param>
public sealed record BannedFeatureSite(
    string Code,
    string Detail,
    string File,
    int Line,
    int Column);

/// <summary>
/// Pass-3 analyzer enumerating the general banned / post-MVP feature set per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Sections 4.1 (coverage matrix) +
/// 5.18-5.20 + Section 12. Emits the band of codes NOT owned by the sim-path,
/// container, lambda/stackalloc, or other dedicated analyzers:
/// <list type="bullet">
///   <item><description><c>XIL2CPP005</c> -- ref/out parameter of XObject-reference type.</description></item>
///   <item><description><c>XIL2CPP010</c> -- BCL type not in the mapped subset.</description></item>
///   <item><description><c>XIL2CPP011</c> -- <c>dynamic</c>.</description></item>
///   <item><description><c>XIL2CPP012</c> -- P/Invoke (<c>DllImport</c>).</description></item>
///   <item><description><c>XIL2CPP013</c> -- direct thread creation.</description></item>
///   <item><description><c>XIL2CPP014</c> -- direct I/O.</description></item>
///   <item><description><c>XIL2CPP015</c> -- <c>params ReadOnlySpan&lt;T&gt;</c>.</description></item>
///   <item><description><c>XIL2CPP016</c> -- static abstract interface members.</description></item>
///   <item><description><c>XIL2CPP019</c> -- volatile field with platform-variable alignment.</description></item>
///   <item><description><c>XIL2CPP060</c> -- value-type pattern on <c>object</c> (implicit boxing).</description></item>
///   <item><description><c>XIL2CPP061</c> -- anonymous types.</description></item>
///   <item><description><c>XIL2CPP062</c> -- implicit boxing of a value type to <c>object</c>.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Code ownership.</b> This analyzer deliberately does NOT emit
/// <c>XIL2CPP040-049</c>/<c>064</c> (SimPath), <c>XIL2CPP070-073</c>
/// (Container), or <c>XIL2CPP080-082</c> (Lambda / StackAlloc). The
/// <c>List&lt;object&gt;</c>-stores-value-type case is left to the container
/// analyzer's <c>XIL2CPP073</c>; this analyzer flags only the standalone
/// boxing / pattern / anonymous-type sites.
/// </para>
/// <para>
/// <b>Determinism.</b> Trees are walked in Pass-1 canonical file order then
/// document order via <c>DescendantNodesAndSelf</c>; per-
/// node detections record in source order. Binding is taken from the
/// authoritative Pass-1 semantic model. No ambient state, no
/// <c>DateTime</c> / <c>Random</c>.
/// </para>
/// </remarks>
public sealed class BannedFeatureAnalyzer : ISemanticAnalyzer
{
    /// <summary>The DllImport attribute's fully-qualified metadata name.</summary>
    private const string DllImportAttributeFullName =
        "System.Runtime.InteropServices.DllImportAttribute";

    /// <inheritdoc />
    public string Name => "BannedFeatureAnalyzer";

    /// <inheritdoc />
    public void Analyze(NormalizedUnit unit, Pass3ResultBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(builder);

        Pass1Result pass1 = unit.Pass1;
        foreach (ModuleParser.ParsedFile parsed in pass1.ParsedFiles)
        {
            SemanticModel model = pass1.GetSemanticModel(parsed.Tree);
            SyntaxNode root = parsed.Tree.GetRoot();

            foreach (SyntaxNode node in root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case ParameterSyntax parameter:
                        AnalyzeParameter(parameter, model, pass1, builder);
                        break;

                    case AnonymousObjectCreationExpressionSyntax anon:
                        AnalyzeAnonymousType(anon, pass1, builder);
                        break;

                    case ObjectCreationExpressionSyntax creation:
                        AnalyzeObjectCreation(creation, model, pass1, builder);
                        break;

                    case FieldDeclarationSyntax field:
                        AnalyzeVolatileField(field, model, pass1, builder);
                        break;

                    case MethodDeclarationSyntax method:
                        AnalyzeMethodDeclaration(method, model, pass1, builder);
                        break;

                    case OperatorDeclarationSyntax op:
                        AnalyzeStaticAbstractMember(
                            op, op.OperatorToken, model, pass1, builder);
                        break;

                    case PropertyDeclarationSyntax property:
                        AnalyzeStaticAbstractMember(
                            property, property.Identifier, model, pass1, builder);
                        break;

                    case ConstantPatternSyntax:
                    case DeclarationPatternSyntax:
                    case RecursivePatternSyntax:
                    case TypePatternSyntax:
                        AnalyzeValueTypePatternOnObject((PatternSyntax)node, model, pass1, builder);
                        break;
                }

                // dynamic + I/O + unmapped-BCL type references are anchored on
                // type-name syntax; boxing on the conversion at the expression.
                AnalyzeTypeReference(node, model, pass1, builder);
                AnalyzeImplicitBoxing(node, model, pass1, builder);
                AnalyzeBannedInvocation(node, model, pass1, builder);
            }
        }
    }

    // -----------------------------------------------------------------
    // XIL2CPP005 -- ref/out XObject-reference parameter.
    // XIL2CPP015 -- params ReadOnlySpan<T>.
    // -----------------------------------------------------------------

    private void AnalyzeParameter(
        ParameterSyntax parameter,
        SemanticModel model,
        Pass1Result pass1,
        Pass3ResultBuilder builder)
    {
        if (model.GetDeclaredSymbol(parameter) is not IParameterSymbol symbol)
        {
            return;
        }

        // XIL2CPP005: a ref / out parameter whose element type derives from
        // (or is) the engine XObject reference type is BANNED in MVP.
        if (symbol.RefKind is RefKind.Ref or RefKind.Out
            && symbol.Type is INamedTypeSymbol named
            && (AnalyzerHelpers.IsXObjectType(named) || AnalyzerHelpers.IsXObjectDerived(named)))
        {
            Emit(
                builder,
                pass1,
                parameter,
                XilSeverity.Error,
                DiagnosticCodes.RefOutXObjectParameterUnsupported,
                $"ref/out parameter '{symbol.Name}' of XObject-reference type '{named.Name}' is not supported in MVP; use Result<T, E> or a nullable return.",
                named.Name);
        }

        // XIL2CPP015: params ReadOnlySpan<T> is post-MVP.
        if (symbol.IsParams
            && symbol.Type is INamedTypeSymbol paramsType
            && IsReadOnlySpan(paramsType))
        {
            Emit(
                builder,
                pass1,
                parameter,
                XilSeverity.Error,
                DiagnosticCodes.ParamsReadOnlySpanPostMvp,
                $"params ReadOnlySpan<T> parameter '{symbol.Name}' is post-MVP.",
                symbol.Name);
        }
    }

    // -----------------------------------------------------------------
    // XIL2CPP016 -- static abstract interface members.
    // XIL2CPP012 -- DllImport / P/Invoke.
    // -----------------------------------------------------------------

    private void AnalyzeMethodDeclaration(
        MethodDeclarationSyntax method,
        SemanticModel model,
        Pass1Result pass1,
        Pass3ResultBuilder builder)
    {
        if (model.GetDeclaredSymbol(method) is not IMethodSymbol symbol)
        {
            return;
        }

        // XIL2CPP016: a static abstract (or static virtual) member declared
        // on an interface is post-MVP.
        ReportStaticAbstractInterfaceMember(symbol, method.Identifier, pass1, builder);

        // XIL2CPP012: a method carrying [DllImport] is P/Invoke and banned.
        if (HasDllImport(symbol))
        {
            Emit(
                builder,
                pass1,
                method.Identifier,
                XilSeverity.Error,
                DiagnosticCodes.PInvokeNotSupported,
                $"P/Invoke (DllImport) on '{symbol.Name}' is not supported.",
                symbol.Name);
        }
    }

    // -----------------------------------------------------------------
    // XIL2CPP016 -- static abstract operators / properties on interfaces are
    // not MethodDeclarationSyntax. Operators are OperatorDeclarationSyntax;
    // static abstract properties are PropertyDeclarationSyntax. Both resolve
    // to a symbol whose IsStatic + IsAbstract on an interface flags them.
    // -----------------------------------------------------------------

    private void AnalyzeStaticAbstractMember(
        MemberDeclarationSyntax member,
        SyntaxToken anchor,
        SemanticModel model,
        Pass1Result pass1,
        Pass3ResultBuilder builder)
    {
        if (model.GetDeclaredSymbol(member) is ISymbol symbol)
        {
            ReportStaticAbstractInterfaceMember(symbol, anchor, pass1, builder);
        }
    }

    private void ReportStaticAbstractInterfaceMember(
        ISymbol symbol,
        SyntaxToken anchor,
        Pass1Result pass1,
        Pass3ResultBuilder builder)
    {
        if (symbol.ContainingType is { TypeKind: RoslynTypeKind.Interface }
            && symbol.IsStatic
            && (symbol.IsAbstract || symbol.IsVirtual))
        {
            Emit(
                builder,
                pass1,
                anchor,
                XilSeverity.Error,
                DiagnosticCodes.StaticAbstractInterfaceMembersPostMvp,
                $"static abstract interface member '{symbol.Name}' is post-MVP.",
                symbol.Name);
        }
    }

    // -----------------------------------------------------------------
    // XIL2CPP061 -- anonymous types.
    // -----------------------------------------------------------------

    private void AnalyzeAnonymousType(
        AnonymousObjectCreationExpressionSyntax anon,
        Pass1Result pass1,
        Pass3ResultBuilder builder)
    {
        Emit(
            builder,
            pass1,
            anon,
            XilSeverity.Error,
            DiagnosticCodes.SimPathAnonymousTypesBanned,
            "anonymous types are banned (implicit boxing of value types; no reflectable layout).",
            "anonymous type");
    }

    // -----------------------------------------------------------------
    // XIL2CPP013 -- direct thread creation via 'new Thread(...)'.
    // -----------------------------------------------------------------

    private void AnalyzeObjectCreation(
        ObjectCreationExpressionSyntax creation,
        SemanticModel model,
        Pass1Result pass1,
        Pass3ResultBuilder builder)
    {
        if (model.GetSymbolInfo(creation).Symbol is not IMethodSymbol ctor)
        {
            return;
        }

        INamedTypeSymbol created = ctor.ContainingType;
        string full = created.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        if (full == "global::System.Threading.Thread")
        {
            Emit(
                builder,
                pass1,
                creation,
                XilSeverity.Error,
                DiagnosticCodes.DirectThreadCreationBanned,
                "direct thread creation (new System.Threading.Thread) is banned; use the XPact task system (XTaskGraph).",
                "System.Threading.Thread");
        }
    }

    // -----------------------------------------------------------------
    // XIL2CPP013 -- Thread.Start / ThreadPool.QueueUserWorkItem / Task.Run.
    // -----------------------------------------------------------------

    private void AnalyzeBannedInvocation(
        SyntaxNode node,
        SemanticModel model,
        Pass1Result pass1,
        Pass3ResultBuilder builder)
    {
        if (node is not InvocationExpressionSyntax invocation)
        {
            return;
        }

        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
        {
            return;
        }

        INamedTypeSymbol container = method.ContainingType;
        string containerFull = container.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        bool isThreadCreation =
            (containerFull == "global::System.Threading.Thread" && method.Name == "Start")
            || (containerFull == "global::System.Threading.ThreadPool"
                && method.Name == "QueueUserWorkItem")
            || (containerFull == "global::System.Threading.Tasks.Task" && method.Name == "Run")
            || (containerFull == "global::System.Threading.Tasks.TaskFactory"
                && method.Name == "StartNew");

        if (isThreadCreation)
        {
            Emit(
                builder,
                pass1,
                invocation,
                XilSeverity.Error,
                DiagnosticCodes.DirectThreadCreationBanned,
                $"direct thread creation ({container.Name}.{method.Name}) is banned; use the XPact task system (XTaskGraph).",
                $"{container.Name}.{method.Name}");
        }
    }

    // -----------------------------------------------------------------
    // XIL2CPP019 -- volatile field whose type has platform-variable
    // alignment (pointer-width IntPtr/UIntPtr/nint/nuint), which cannot
    // guarantee a portable std::atomic alignment across the target matrix.
    // -----------------------------------------------------------------

    private void AnalyzeVolatileField(
        FieldDeclarationSyntax field,
        SemanticModel model,
        Pass1Result pass1,
        Pass3ResultBuilder builder)
    {
        bool isVolatile = false;
        foreach (SyntaxToken modifier in field.Modifiers)
        {
            if (modifier.IsKind(SyntaxKind.VolatileKeyword))
            {
                isVolatile = true;
                break;
            }
        }
        if (!isVolatile)
        {
            return;
        }

        foreach (VariableDeclaratorSyntax declarator in field.Declaration.Variables)
        {
            if (model.GetDeclaredSymbol(declarator) is not IFieldSymbol symbol)
            {
                continue;
            }

            if (IsPlatformVariableAlignmentType(symbol.Type))
            {
                Emit(
                    builder,
                    pass1,
                    declarator,
                    XilSeverity.Error,
                    DiagnosticCodes.VolatileFieldAlignmentIncompatible,
                    $"volatile field '{symbol.Name}' of pointer-width type '{symbol.Type.Name}' has incompatible alignment for std::atomic on the target platform.",
                    symbol.Name);
            }
        }
    }

    // -----------------------------------------------------------------
    // XIL2CPP011 -- 'dynamic'.
    // XIL2CPP014 -- direct I/O (System.IO.* file / stream, System.Net.Sockets).
    // XIL2CPP010 -- BCL type not in the mapped subset.
    // All anchored on type-name syntax (the place the type is written).
    // -----------------------------------------------------------------

    private void AnalyzeTypeReference(
        SyntaxNode node,
        SemanticModel model,
        Pass1Result pass1,
        Pass3ResultBuilder builder)
    {
        // 'dynamic' surfaces as an IdentifierNameSyntax 'dynamic' bound to the
        // dynamic type symbol. Detect on the type-syntax position only so an
        // identifier named after a local does not false-positive.
        if (node is IdentifierNameSyntax { Identifier.ValueText: "dynamic" } dynId
            && model.GetTypeInfo(dynId).Type is { TypeKind: RoslynTypeKind.Dynamic })
        {
            Emit(
                builder,
                pass1,
                dynId,
                XilSeverity.Error,
                DiagnosticCodes.DynamicNotSupported,
                "'dynamic' is not supported; XIL2CPP is a static transpiler.",
                "dynamic");
            return;
        }

        // For named-type references (qualified or simple), classify the bound
        // type. Only the OUTERMOST name node is considered (a qualified name's
        // left sub-name binds to a namespace / containing type and would
        // double-count), so restrict to nodes whose parent is not itself a
        // qualifying name carrying the same type.
        if (node is not (IdentifierNameSyntax or QualifiedNameSyntax or GenericNameSyntax))
        {
            return;
        }

        // Skip the left side of a qualified name and member-binding subtrees:
        // only flag the type at the position it is *used* as a type.
        if (node.Parent is QualifiedNameSyntax parentQualified
            && ReferenceEquals(parentQualified.Left, node))
        {
            return;
        }
        if (node.Parent is MemberAccessExpressionSyntax)
        {
            // The whole member-access is classified via its bound symbol's
            // containing type below only when it is the type portion; member
            // accesses on instances are handled by the symbol's type, not here.
        }

        ISymbol? bound = model.GetSymbolInfo(node).Symbol;
        INamedTypeSymbol? typeSymbol = bound switch
        {
            INamedTypeSymbol nts => nts,
            _ => null,
        };
        if (typeSymbol is null)
        {
            return;
        }

        // Only classify nodes that are actually written as a TYPE (parameter
        // type, field type, variable type, base type, typeof, object-creation
        // type, generic argument). A bare identifier that resolves to a type
        // used as a value (e.g. 'File.ReadAllText') has a MemberAccess parent
        // whose Name is the member, not the type -- those are handled by the
        // member-access type classification path below.
        if (!IsTypeUsagePosition(node))
        {
            return;
        }

        ClassifyBclType(typeSymbol, node, pass1, builder);
    }

    // -----------------------------------------------------------------
    // XIL2CPP062 -- implicit boxing of a value type to object.
    // -----------------------------------------------------------------

    private void AnalyzeImplicitBoxing(
        SyntaxNode node,
        SemanticModel model,
        Pass1Result pass1,
        Pass3ResultBuilder builder)
    {
        if (node is not ExpressionSyntax expression)
        {
            return;
        }

        // An expression node that itself is a parent's type-syntax should not
        // be probed for conversion (it is not a value position).
        if (expression is TypeSyntax)
        {
            return;
        }

        // Boxing of a value type into an object-typed CONTAINER (a
        // collection / object initializer element, e.g.
        // new List<object> { 1, 2, 3 }) is the container analyzer's
        // XIL2CPP073 site, not this analyzer's XIL2CPP062 site. Leave it to
        // the owning analyzer to avoid double-flagging the same construct.
        if (expression.Parent is InitializerExpressionSyntax)
        {
            return;
        }

        Conversion conversion = model.GetConversion(expression);
        if (!conversion.IsBoxing)
        {
            return;
        }

        TypeInfo info = model.GetTypeInfo(expression);
        if (info.Type is not { IsValueType: true } source)
        {
            return;
        }
        if (info.ConvertedType is not { } target)
        {
            return;
        }

        // Only the boxing-to-object family is XIL2CPP062. Boxing to a concrete
        // interface (e.g. IComparable) is a distinct emit concern; the MVP ban
        // per Section 5.6 / coverage matrix is the value-type -> object case.
        if (target.SpecialType != SpecialType.System_Object)
        {
            return;
        }

        Emit(
            builder,
            pass1,
            expression,
            XilSeverity.Error,
            DiagnosticCodes.ImplicitBoxingNotSupported,
            $"implicit boxing of value type '{source.Name}' to object is not supported in MVP; use an explicit Int32Box / FloatBox wrapper.",
            source.Name);
    }

    // -----------------------------------------------------------------
    // XIL2CPP060 -- value-type pattern matched against an object operand
    // (the match requires boxing).
    // -----------------------------------------------------------------

    private void AnalyzeValueTypePatternOnObject(
        PatternSyntax pattern,
        SemanticModel model,
        Pass1Result pass1,
        Pass3ResultBuilder builder)
    {
        // The pattern's input type is the static type of the operand being
        // matched. A value-type test pattern against an object operand boxes.
        ITypeSymbol? patternType = ExtractPatternTestType(pattern, model);
        if (patternType is not { IsValueType: true })
        {
            return;
        }

        ITypeSymbol? inputType = GetPatternInputType(pattern, model);
        if (inputType is not { SpecialType: SpecialType.System_Object })
        {
            return;
        }

        Emit(
            builder,
            pass1,
            pattern,
            XilSeverity.Error,
            DiagnosticCodes.PatternMatchValueTypeRequiresBoxing,
            $"pattern match on object with value-type pattern '{patternType.Name}' requires boxing; use a typed discriminant union.",
            patternType.Name);
    }

    // =================================================================
    // Helpers.
    // =================================================================

    private static bool IsReadOnlySpan(INamedTypeSymbol type)
        => type.IsGenericType
            && type.Name == "ReadOnlySpan"
            && type.ContainingNamespace is { IsGlobalNamespace: false } ns
            && ns.ToDisplayString() == "System";

    private static bool HasDllImport(IMethodSymbol method)
    {
        foreach (AttributeData attribute in method.GetAttributes())
        {
            if (attribute.AttributeClass is { } cls
                && cls.ToDisplayString() == DllImportAttributeFullName)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsPlatformVariableAlignmentType(ITypeSymbol type)
        => type.SpecialType is SpecialType.System_IntPtr or SpecialType.System_UIntPtr;

    /// <summary>
    /// Classify a BCL (System.*) type at a type-usage position. Emits
    /// XIL2CPP014 for the direct-I/O family and XIL2CPP010 for a curated set
    /// of clearly-unmapped BCL types. Types this analyzer does not own (e.g.
    /// the sim-path / container / async bands) are left to their owners.
    /// </summary>
    private void ClassifyBclType(
        INamedTypeSymbol type,
        SyntaxNode anchor,
        Pass1Result pass1,
        Pass3ResultBuilder builder)
    {
        string full = type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // XIL2CPP014: direct I/O surfaces (file + socket). The XPact asset /
        // network APIs replace these.
        if (IsDirectIoType(full))
        {
            Emit(
                builder,
                pass1,
                anchor,
                XilSeverity.Error,
                DiagnosticCodes.DirectIoNotInBclSurface,
                $"direct I/O type '{Strip(full)}' is not in the BCL surface; use XPact's asset/network APIs.",
                Strip(full));
            return;
        }

        // XIL2CPP010: a BCL type with no XPact mapping. Restricted to a
        // curated denylist so the rule stays deterministic and never collides
        // with a more-specific code another analyzer owns.
        if (IsUnmappedBclType(full))
        {
            Emit(
                builder,
                pass1,
                anchor,
                XilSeverity.Error,
                DiagnosticCodes.BclTypeNotInMappedSubset,
                $"BCL type '{Strip(full)}' is not in the mapped subset.",
                Strip(full));
        }
    }

    private static bool IsDirectIoType(string fullyQualified) => fullyQualified switch
    {
        "global::System.IO.File" => true,
        "global::System.IO.FileStream" => true,
        "global::System.IO.StreamReader" => true,
        "global::System.IO.StreamWriter" => true,
        "global::System.IO.FileInfo" => true,
        "global::System.IO.Directory" => true,
        "global::System.IO.DirectoryInfo" => true,
        "global::System.Net.Sockets.Socket" => true,
        "global::System.Net.Sockets.TcpClient" => true,
        "global::System.Net.Sockets.TcpListener" => true,
        "global::System.Net.Sockets.UdpClient" => true,
        _ => false,
    };

    private static bool IsUnmappedBclType(string fullyQualified) => fullyQualified switch
    {
        "global::System.Text.RegularExpressions.Regex" => true,
        "global::System.Text.RegularExpressions.Match" => true,
        "global::System.Text.RegularExpressions.MatchCollection" => true,
        "global::System.Xml.XmlDocument" => true,
        "global::System.Xml.XmlReader" => true,
        "global::System.Xml.XmlWriter" => true,
        "global::System.Xml.Linq.XDocument" => true,
        "global::System.Data.DataSet" => true,
        "global::System.Data.DataTable" => true,
        _ => false,
    };

    private static string Strip(string fullyQualified)
        => fullyQualified.StartsWith("global::", StringComparison.Ordinal)
            ? fullyQualified["global::".Length..]
            : fullyQualified;

    /// <summary>
    /// True when <paramref name="node"/> is written at a position where it
    /// denotes a TYPE (not a value). A name used as a value (e.g.
    /// <c>File.ReadAllText</c>'s <c>File</c>) is the qualifier of a member
    /// access and is excluded so the type classification fires only where the
    /// type itself is named as a type.
    /// </summary>
    private static bool IsTypeUsagePosition(SyntaxNode node)
    {
        SyntaxNode? parent = node.Parent;
        return parent switch
        {
            ParameterSyntax p => ReferenceEquals(p.Type, node),
            VariableDeclarationSyntax v => ReferenceEquals(v.Type, node),
            BaseTypeSyntax b => ReferenceEquals(b.Type, node),
            TypeOfExpressionSyntax t => ReferenceEquals(t.Type, node),
            ObjectCreationExpressionSyntax o => ReferenceEquals(o.Type, node),
            CastExpressionSyntax c => ReferenceEquals(c.Type, node),
            TypeArgumentListSyntax => true,
            QualifiedNameSyntax q => ReferenceEquals(q.Right, node),
            MethodDeclarationSyntax m => ReferenceEquals(m.ReturnType, node),
            PropertyDeclarationSyntax pr => ReferenceEquals(pr.Type, node),
            _ => false,
        };
    }

    /// <summary>
    /// The static type the pattern's TEST targets (the type the operand is
    /// matched against). Returns the type symbol for declaration / type /
    /// recursive patterns; null for non-type-testing patterns.
    /// </summary>
    private static ITypeSymbol? ExtractPatternTestType(PatternSyntax pattern, SemanticModel model)
    {
        TypeSyntax? typeSyntax = pattern switch
        {
            DeclarationPatternSyntax decl => decl.Type,
            TypePatternSyntax tp => tp.Type,
            RecursivePatternSyntax rp => rp.Type,
            ConstantPatternSyntax cp => null, // classified via the operand value type
            _ => null,
        };

        if (typeSyntax is not null)
        {
            return model.GetTypeInfo(typeSyntax).Type;
        }

        // For a constant pattern (e.g. 'case 5:'), the tested type is the
        // constant's type.
        if (pattern is ConstantPatternSyntax constant)
        {
            return model.GetTypeInfo(constant.Expression).Type;
        }

        return null;
    }

    /// <summary>
    /// The static input type the pattern is matched against (the operand of
    /// the enclosing <c>is</c> / switch). Uses Roslyn's pattern type-info.
    /// </summary>
    private static ITypeSymbol? GetPatternInputType(PatternSyntax pattern, SemanticModel model)
    {
        // The pattern's own GetTypeInfo reports the input (.Type) and the
        // narrowed (.ConvertedType) types for the pattern operand.
        TypeInfo info = model.GetTypeInfo(pattern);
        return info.Type;
    }

    /// <summary>
    /// Record a diagnostic + an emit-metadata site for one banned-feature
    /// detection, anchored on a syntax node.
    /// </summary>
    private void Emit(
        Pass3ResultBuilder builder,
        Pass1Result pass1,
        SyntaxNode anchor,
        XilSeverity severity,
        string code,
        string message,
        string detail)
        => EmitAt(builder, pass1, anchor.GetLocation(), severity, code, message, detail);

    /// <summary>
    /// Record a diagnostic + an emit-metadata site anchored on a single token.
    /// </summary>
    private void Emit(
        Pass3ResultBuilder builder,
        Pass1Result pass1,
        SyntaxToken anchor,
        XilSeverity severity,
        string code,
        string message,
        string detail)
        => EmitAt(builder, pass1, anchor.GetLocation(), severity, code, message, detail);

    private void EmitAt(
        Pass3ResultBuilder builder,
        Pass1Result pass1,
        Location location,
        XilSeverity severity,
        string code,
        string message,
        string detail)
    {
        FileLinePositionSpan lineSpan = location.GetLineSpan();
        string file = lineSpan.Path;
        int line = lineSpan.StartLinePosition.Line + 1;
        int column = lineSpan.StartLinePosition.Character + 1;

        builder.AddDiagnostic(new DiagnosticRecord(
            severity,
            code,
            message,
            File: file,
            Line: line,
            Column: column,
            Module: pass1.ModuleName));

        builder.Add(new BannedFeatureSite(code, detail, file, line, column));
    }
}
