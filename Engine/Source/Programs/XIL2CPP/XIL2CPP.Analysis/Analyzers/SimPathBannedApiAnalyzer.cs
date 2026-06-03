// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using DiagnosticSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Analysis;

/// <summary>
/// Pass-3 semantic analyzer that enforces the sim-path build-time banned-API
/// list per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 7.4 (build-time
/// banned-API enforcement) and Section 7.8 (sim-path determinism mapping).
/// </summary>
/// <remarks>
/// <para>
/// <b>Sim-path gate.</b> The analyzer is a strict no-op unless the module is
/// flagged sim-path (<see cref="Pass1Result.IsSimPath"/>). Cross-arch
/// bit-exact determinism is required only on sim-path TUs, so the banned-API
/// surface is a sim-path concern; a non-sim-path module that calls
/// <c>DateTime.Now</c> / <c>System.Random</c> / <c>Interlocked</c> / LINQ /
/// async is allowed and emits nothing here.
/// </para>
/// <para>
/// <b>What it walks.</b> Each parsed tree is walked once in document order
/// (parents before nested children). For every relevant node the most-specific
/// banned-API rule is applied, so a single syntax node yields at most one
/// diagnostic. The walk is deterministic (Pass-1 parsed-file ordinal order,
/// then document order) and binds against the authoritative Pass-1 semantic
/// model (<see cref="Pass1Result.GetSemanticModel(SyntaxTree)"/>), never on
/// ambient state / <c>DateTime</c> / <c>Random</c> (gate
/// X-IL2CPP-CSPATH-DET, Section 9.9).
/// </para>
/// <para>
/// <b>Codes owned (Section 12).</b> Every hit emits its specific code and
/// records a <see cref="BannedApiHit"/>:
/// <list type="bullet">
///   <item><description><c>XIL2CPP040</c> -- generic banned API (DateTime.Now/UtcNow/Today, Environment.TickCount[64], Stopwatch, Thread.Sleep/SpinWait, Task.Run/Delay, ThreadPool.*, Console.*, File.*, Net.*, Guid.NewGuid, GC.*, Marshal.*, System.Linq.*, object.GetHashCode() on reference types).</description></item>
///   <item><description><c>XIL2CPP041</c> -- foreach over <c>HashSet&lt;T&gt;</c> / <c>Dictionary&lt;K,V&gt;</c> (non-deterministic iteration order; error on sim-path).</description></item>
///   <item><description><c>XIL2CPP042</c> -- string index <c>s[i]</c> (non-deterministic at the UTF-8 boundary).</description></item>
///   <item><description><c>XIL2CPP043</c> -- <c>lock(obj)</c>.</description></item>
///   <item><description><c>XIL2CPP044</c> -- <c>async</c> / <c>await</c>.</description></item>
///   <item><description><c>XIL2CPP048</c> -- <c>Task&lt;T&gt;</c> / <c>ValueTask</c> / <c>IAsyncEnumerable&lt;T&gt;</c> / <c>await foreach</c> / <c>Task.Result</c> / <c>Task.Wait</c>.</description></item>
///   <item><description><c>XIL2CPP049</c> -- <c>System.Random.*</c> / <c>Random.Shared</c>.</description></item>
///   <item><description><c>XIL2CPP055</c> -- <c>System.Threading.Interlocked.*</c>.</description></item>
///   <item><description><c>XIL2CPP057</c> -- <c>[ThreadStatic]</c>.</description></item>
///   <item><description><c>XIL2CPP058</c> -- locale-dependent <c>ToString</c> / <c>Parse</c> / <c>TryParse</c> (no <c>IFormatProvider</c> argument).</description></item>
///   <item><description><c>XIL2CPP059</c> -- <c>FName</c> constructed from a non-literal string (warning).</description></item>
///   <item><description><c>XIL2CPP064</c> -- <c>foreach</c> over an <c>IEnumerable&lt;T&gt;</c> interface.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class SimPathBannedApiAnalyzer : ISemanticAnalyzer
{
    /// <inheritdoc />
    public string Name => "SimPathBannedApiAnalyzer";

    /// <inheritdoc />
    public void Analyze(NormalizedUnit unit, Pass3ResultBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(builder);

        Pass1Result pass1 = unit.Pass1;

        // Sim-path gate: the banned-API surface is a cross-arch determinism
        // concern that applies only to sim-path TUs (Section 7.4 / 7.8). A
        // non-sim-path module emits nothing here.
        if (!pass1.IsSimPath)
        {
            return;
        }

        string module = pass1.ModuleName;

        foreach (ModuleParser.ParsedFile parsed in pass1.ParsedFiles)
        {
            SemanticModel model = pass1.GetSemanticModel(parsed.Tree);
            SyntaxNode root = parsed.Tree.GetRoot();

            foreach (SyntaxNode node in root.DescendantNodesAndSelf())
            {
                Inspect(node, model, builder, module);
            }
        }
    }

    /// <summary>
    /// Apply the most-specific banned-API rule to a single node. Each node
    /// yields at most one diagnostic + <see cref="BannedApiHit"/>.
    /// </summary>
    private static void Inspect(
        SyntaxNode node,
        SemanticModel model,
        Pass3ResultBuilder builder,
        string module)
    {
        switch (node)
        {
            // --------------------------------------------------------------
            // lock(obj) -> XIL2CPP043.
            // --------------------------------------------------------------
            case LockStatementSyntax lockStatement:
                Emit(builder, module, DiagnosticCodes.SimPathLockBanned, DiagnosticSeverity.Error,
                    "lock(obj) is banned on sim-path TUs",
                    "lock", lockStatement.LockKeyword.GetLocation());
                break;

            // --------------------------------------------------------------
            // foreach -> XIL2CPP041 (HashSet/Dictionary) / XIL2CPP064
            // (IEnumerable<T> interface). await foreach -> XIL2CPP048.
            // --------------------------------------------------------------
            case CommonForEachStatementSyntax forEach:
                InspectForEach(forEach, model, builder, module);
                break;

            // --------------------------------------------------------------
            // await expression -> XIL2CPP044 (async/await).
            // --------------------------------------------------------------
            case AwaitExpressionSyntax awaitExpression:
                Emit(builder, module, DiagnosticCodes.SimPathAsyncAwaitBanned, DiagnosticSeverity.Error,
                    "async/await is banned on sim-path TUs",
                    "await", awaitExpression.AwaitKeyword.GetLocation());
                break;

            // --------------------------------------------------------------
            // Method / local-function / lambda / anonymous-method with the
            // 'async' modifier -> XIL2CPP044 (async/await).
            // --------------------------------------------------------------
            case MethodDeclarationSyntax method when HasAsyncModifier(method.Modifiers):
                EmitAsyncDeclaration(builder, module, method.Identifier.GetLocation());
                break;
            case LocalFunctionStatementSyntax localFn when HasAsyncModifier(localFn.Modifiers):
                EmitAsyncDeclaration(builder, module, localFn.Identifier.GetLocation());
                break;
            case AnonymousFunctionExpressionSyntax anon when !anon.AsyncKeyword.IsKind(SyntaxKind.None):
                EmitAsyncDeclaration(builder, module, anon.AsyncKeyword.GetLocation());
                break;

            // --------------------------------------------------------------
            // [ThreadStatic] -> XIL2CPP057.
            // --------------------------------------------------------------
            case AttributeSyntax attribute when IsThreadStaticAttribute(attribute, model):
                Emit(builder, module, DiagnosticCodes.SimPathThreadStaticBanned, DiagnosticSeverity.Error,
                    "[ThreadStatic] banned on sim-path",
                    "[ThreadStatic]", attribute.GetLocation());
                break;

            // --------------------------------------------------------------
            // new FName(non-literal) -> XIL2CPP059. Banned Task-family type
            // construction -> XIL2CPP048. Otherwise no object-creation rule.
            // --------------------------------------------------------------
            case BaseObjectCreationExpressionSyntax creation:
                InspectObjectCreation(creation, model, builder, module);
                break;

            // --------------------------------------------------------------
            // Member access: the symbol-based banned set + Task.Result /
            // Task.Wait (XIL2CPP048). Covers both property accesses
            // (DateTime.Now, Random.Shared) and the callee of an invocation
            // (Interlocked.Increment, double.Parse). Invocation nodes are NOT
            // separately inspected, so a member-bound call is counted once.
            // --------------------------------------------------------------
            case MemberAccessExpressionSyntax memberAccess:
                InspectMemberAccess(memberAccess, model, builder, module);
                break;

            // --------------------------------------------------------------
            // string s; s[i] -> XIL2CPP042 (UTF-8 boundary).
            // --------------------------------------------------------------
            case ElementAccessExpressionSyntax elementAccess:
                InspectStringIndex(elementAccess, model, builder, module);
                break;

            // --------------------------------------------------------------
            // Declared usages of Task<T> / ValueTask / IAsyncEnumerable<T>
            // in field / property / parameter / method-return / local types
            // -> XIL2CPP048.
            // --------------------------------------------------------------
            case FieldDeclarationSyntax field:
                InspectTaskTypeUsage(field.Declaration.Type, model, builder, module);
                break;
            case PropertyDeclarationSyntax property:
                InspectTaskTypeUsage(property.Type, model, builder, module);
                break;
            case ParameterSyntax parameter when parameter.Type is not null:
                InspectTaskTypeUsage(parameter.Type, model, builder, module);
                break;
            case MethodDeclarationSyntax methodReturn:
                InspectTaskTypeUsage(methodReturn.ReturnType, model, builder, module);
                break;
            case LocalDeclarationStatementSyntax local:
                InspectTaskTypeUsage(local.Declaration.Type, model, builder, module);
                break;
        }
    }

    // ------------------------------------------------------------------
    // foreach.
    // ------------------------------------------------------------------

    private static void InspectForEach(
        CommonForEachStatementSyntax forEach,
        SemanticModel model,
        Pass3ResultBuilder builder,
        string module)
    {
        // await foreach -> XIL2CPP048 (covered by the IAsyncEnumerable ban).
        if (!forEach.AwaitKeyword.IsKind(SyntaxKind.None))
        {
            Emit(builder, module, DiagnosticCodes.SimPathTaskBanned, DiagnosticSeverity.Error,
                "Task<T> / ValueTask<T> / IAsyncEnumerable<T> / await foreach / Task.Result / Task.Wait is banned on sim-path TUs (await foreach)",
                "await foreach", forEach.AwaitKeyword.GetLocation());
            return;
        }

        ITypeSymbol? collectionType = model.GetTypeInfo(forEach.Expression).Type;
        if (collectionType is null or IErrorTypeSymbol)
        {
            return;
        }

        // XIL2CPP041: foreach over HashSet<T> / Dictionary<K,V>.
        if (IsHashSetOrDictionary(collectionType))
        {
            Emit(builder, module, DiagnosticCodes.NonDeterministicIterationOrder, DiagnosticSeverity.Error,
                $"foreach over {collectionType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} has non-deterministic iteration order",
                collectionType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                forEach.ForEachKeyword.GetLocation());
            return;
        }

        // XIL2CPP064: foreach where the collection's STATIC type is an
        // IEnumerable<T> interface (the allocation-pressure form). A concrete
        // collection iterated by its concrete type is not flagged here.
        if (collectionType.TypeKind == TypeKind.Interface
            && IsGenericIEnumerableInterface(collectionType))
        {
            Emit(builder, module, DiagnosticCodes.SimPathForeachOverIEnumerableBanned, DiagnosticSeverity.Error,
                $"foreach over IEnumerable<T> interface is banned on sim-path (allocation pressure): {collectionType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}",
                collectionType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                forEach.ForEachKeyword.GetLocation());
        }
    }

    // ------------------------------------------------------------------
    // object creation.
    // ------------------------------------------------------------------

    private static void InspectObjectCreation(
        BaseObjectCreationExpressionSyntax creation,
        SemanticModel model,
        Pass3ResultBuilder builder,
        string module)
    {
        ITypeSymbol? createdType = model.GetTypeInfo(creation).Type;
        if (createdType is null or IErrorTypeSymbol)
        {
            return;
        }

        // XIL2CPP059: FName constructed from a non-literal string. FName is an
        // engine type absent from the test BCL, so match by simple type name.
        if (createdType.Name == "FName")
        {
            ArgumentListSyntax? args = creation.ArgumentList;
            if (args is { Arguments.Count: > 0 }
                && !IsLiteralStringArgument(args.Arguments[0].Expression))
            {
                Emit(builder, module, DiagnosticCodes.SimPathFNameFromNonLiteral, DiagnosticSeverity.Warning,
                    "Sim-path code constructs FName from non-literal string",
                    "FName", creation.GetLocation());
            }
            return;
        }

        // XIL2CPP048: constructing a banned Task-family type.
        if (IsBannedTaskFamilyType(createdType))
        {
            Emit(builder, module, DiagnosticCodes.SimPathTaskBanned, DiagnosticSeverity.Error,
                $"Task<T> / ValueTask<T> / IAsyncEnumerable<T> / await foreach / Task.Result / Task.Wait is banned on sim-path TUs ({createdType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})",
                createdType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                creation.GetLocation());
        }
    }

    // ------------------------------------------------------------------
    // member access (the symbol-based banned set).
    // ------------------------------------------------------------------

    private static void InspectMemberAccess(
        MemberAccessExpressionSyntax memberAccess,
        SemanticModel model,
        Pass3ResultBuilder builder,
        string module)
    {
        ISymbol? symbol = model.GetSymbolInfo(memberAccess).Symbol;
        if (symbol is null)
        {
            return;
        }

        // Collapse member-access chains: skip M when M.Expression is itself a
        // member access whose member belongs to the SAME containing type that
        // is already banned (e.g. Random.Shared.Next -> single hit on
        // Random.Shared). The innermost access carries the hit.
        if (memberAccess.Expression is MemberAccessExpressionSyntax innerAccess
            && model.GetSymbolInfo(innerAccess).Symbol is { } innerSymbol
            && SymbolEqualityComparer.Default.Equals(
                innerSymbol.ContainingType, symbol.ContainingType)
            && IsBannedMemberSymbol(innerSymbol))
        {
            return;
        }

        INamedTypeSymbol? containingType = symbol.ContainingType;
        if (containingType is null)
        {
            return;
        }

        string memberName = symbol.Name;
        Location location = memberAccess.Name.GetLocation();

        // XIL2CPP049: System.Random.* (including Random.Shared).
        if (IsType(containingType, "System.Random"))
        {
            Emit(builder, module, DiagnosticCodes.SimPathRandomBanned, DiagnosticSeverity.Error,
                $"Random.* banned on sim-path TUs (System.Random.{memberName})",
                $"System.Random.{memberName}", location);
            return;
        }

        // XIL2CPP055: System.Threading.Interlocked.*.
        if (IsType(containingType, "System.Threading.Interlocked"))
        {
            Emit(builder, module, DiagnosticCodes.SimPathInterlockedBanned, DiagnosticSeverity.Error,
                $"Interlocked.* banned on sim-path TUs (System.Threading.Interlocked.{memberName})",
                $"System.Threading.Interlocked.{memberName}", location);
            return;
        }

        // XIL2CPP048: Task.Result / Task.Wait on a Task / ValueTask instance.
        if ((memberName is "Result" or "Wait" or "GetAwaiter") && IsBannedTaskFamilyType(containingType))
        {
            Emit(builder, module, DiagnosticCodes.SimPathTaskBanned, DiagnosticSeverity.Error,
                $"Task<T> / ValueTask<T> / IAsyncEnumerable<T> / await foreach / Task.Result / Task.Wait is banned on sim-path TUs ({containingType.Name}.{memberName})",
                $"{containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{memberName}", location);
            return;
        }

        // XIL2CPP058: locale-dependent ToString / Parse / TryParse without an
        // IFormatProvider argument, on a BCL value type whose textual form is
        // culture-sensitive.
        if ((memberName is "ToString" or "Parse" or "TryParse")
            && symbol is IMethodSymbol toStringOrParse
            && IsLocaleSensitiveType(containingType)
            && !HasFormatProviderParameter(toStringOrParse))
        {
            Emit(builder, module, DiagnosticCodes.SimPathLocaleDependentToStringParse, DiagnosticSeverity.Error,
                $"Locale-dependent {memberName} on sim-path TUs ({containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{memberName}); specify CultureInfo.InvariantCulture explicitly",
                $"{containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{memberName}", location);
            return;
        }

        // XIL2CPP040: the generic banned-API set (DateTime.Now, Environment
        // .TickCount, Stopwatch, Thread.Sleep, Task.Run/Delay, ThreadPool,
        // Console, File, Net, Guid.NewGuid, GC, Marshal, System.Linq.*,
        // object.GetHashCode() on reference types).
        if (TryMatchGenericBannedApi(containingType, memberName, symbol, out string fullName))
        {
            Emit(builder, module, DiagnosticCodes.SimPathBannedApiCall, DiagnosticSeverity.Error,
                $"Sim-path TU calls banned API {fullName}",
                fullName, location);
        }
    }

    // ------------------------------------------------------------------
    // string index.
    // ------------------------------------------------------------------

    private static void InspectStringIndex(
        ElementAccessExpressionSyntax elementAccess,
        SemanticModel model,
        Pass3ResultBuilder builder,
        string module)
    {
        ITypeSymbol? indexedType = model.GetTypeInfo(elementAccess.Expression).Type;
        if (indexedType is { SpecialType: SpecialType.System_String })
        {
            Emit(builder, module, DiagnosticCodes.SimPathStringIndexNonDeterministic, DiagnosticSeverity.Error,
                "Sim-path string index s[i] is non-deterministic at the UTF-8 boundary",
                "string-index", elementAccess.GetLocation());
        }
    }

    // ------------------------------------------------------------------
    // Task-family type usage in a declaration.
    // ------------------------------------------------------------------

    private static void InspectTaskTypeUsage(
        TypeSyntax typeSyntax,
        SemanticModel model,
        Pass3ResultBuilder builder,
        string module)
    {
        ITypeSymbol? type = model.GetTypeInfo(typeSyntax).Type;
        if (type is null or IErrorTypeSymbol)
        {
            return;
        }

        if (IsBannedTaskFamilyType(type))
        {
            Emit(builder, module, DiagnosticCodes.SimPathTaskBanned, DiagnosticSeverity.Error,
                $"Task<T> / ValueTask<T> / IAsyncEnumerable<T> / await foreach / Task.Result / Task.Wait is banned on sim-path TUs ({type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})",
                type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                typeSyntax.GetLocation());
        }
    }

    // ==================================================================
    // Classification helpers.
    // ==================================================================

    /// <summary>
    /// True iff <paramref name="modifiers"/> contains the <c>async</c>
    /// keyword.
    /// </summary>
    private static bool HasAsyncModifier(SyntaxTokenList modifiers)
    {
        foreach (SyntaxToken modifier in modifiers)
        {
            if (modifier.IsKind(SyntaxKind.AsyncKeyword))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True iff <paramref name="attribute"/> resolves to
    /// <c>System.ThreadStaticAttribute</c>.
    /// </summary>
    private static bool IsThreadStaticAttribute(AttributeSyntax attribute, SemanticModel model)
    {
        ISymbol? symbol = model.GetSymbolInfo(attribute).Symbol;
        INamedTypeSymbol? attributeType = (symbol as IMethodSymbol)?.ContainingType ?? symbol as INamedTypeSymbol;
        return IsType(attributeType, "System.ThreadStaticAttribute");
    }

    /// <summary>
    /// True iff <paramref name="type"/> is <c>HashSet&lt;T&gt;</c> or
    /// <c>Dictionary&lt;K,V&gt;</c> from <c>System.Collections.Generic</c>.
    /// </summary>
    private static bool IsHashSetOrDictionary(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
        {
            return false;
        }

        string constructedFrom = named.ConstructedFrom.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return constructedFrom == "global::System.Collections.Generic.HashSet<T>"
            || constructedFrom == "global::System.Collections.Generic.Dictionary<TKey, TValue>";
    }

    /// <summary>
    /// True iff <paramref name="type"/> IS the
    /// <c>System.Collections.Generic.IEnumerable&lt;T&gt;</c> interface (the
    /// generic enumerable surface foreach over which is banned).
    /// </summary>
    private static bool IsGenericIEnumerableInterface(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
        {
            return false;
        }

        return named.ConstructedFrom.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            == "global::System.Collections.Generic.IEnumerable<T>";
    }

    /// <summary>
    /// True iff <paramref name="type"/> is one of the banned Task-family
    /// types: <c>System.Threading.Tasks.Task</c> /
    /// <c>Task&lt;T&gt;</c> / <c>ValueTask</c> / <c>ValueTask&lt;T&gt;</c>, or
    /// <c>System.Collections.Generic.IAsyncEnumerable&lt;T&gt;</c>.
    /// </summary>
    private static bool IsBannedTaskFamilyType(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
        {
            return false;
        }

        string constructedFrom = named.ConstructedFrom.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return constructedFrom == "global::System.Threading.Tasks.Task"
            || constructedFrom == "global::System.Threading.Tasks.Task<TResult>"
            || constructedFrom == "global::System.Threading.Tasks.ValueTask"
            || constructedFrom == "global::System.Threading.Tasks.ValueTask<TResult>"
            || constructedFrom == "global::System.Collections.Generic.IAsyncEnumerable<T>";
    }

    /// <summary>
    /// True iff <paramref name="type"/> is a BCL value type whose default
    /// <c>ToString</c> / <c>Parse</c> is culture-sensitive (the numeric +
    /// date / time + currency surface in Section 7.8's mapping table).
    /// </summary>
    private static bool IsLocaleSensitiveType(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
            case SpecialType.System_DateTime:
                return true;
        }

        string fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return fullName == "global::System.DateTimeOffset"
            || fullName == "global::System.TimeSpan";
    }

    /// <summary>
    /// True iff the method has a parameter of an
    /// <c>System.IFormatProvider</c> (e.g. <c>CultureInfo</c>) type -- i.e.
    /// the explicitly-culture-aware overload, which is NOT locale-dependent.
    /// </summary>
    private static bool HasFormatProviderParameter(IMethodSymbol method)
    {
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            if (ImplementsIFormatProvider(parameter.Type))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True iff <paramref name="type"/> is or implements
    /// <c>System.IFormatProvider</c>.
    /// </summary>
    private static bool ImplementsIFormatProvider(ITypeSymbol type)
    {
        if (IsType(type as INamedTypeSymbol, "System.IFormatProvider"))
        {
            return true;
        }

        foreach (INamedTypeSymbol iface in type.AllInterfaces)
        {
            if (IsType(iface, "System.IFormatProvider"))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Match a symbol against the XIL2CPP040 generic banned-API set. Returns
    /// the canonical full name for the diagnostic message when matched.
    /// </summary>
    private static bool TryMatchGenericBannedApi(
        INamedTypeSymbol containingType,
        string memberName,
        ISymbol symbol,
        out string fullName)
    {
        fullName = string.Empty;
        string typeName = containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Whole-type bans (any member). The display name carries the leading
        // "global::"; the canonical message drops it.
        if (typeName == "global::System.Diagnostics.Stopwatch"
            || typeName == "global::System.Threading.ThreadPool"
            || typeName == "global::System.Runtime.InteropServices.Marshal"
            || IsUnderNamespace(containingType, "System.Linq")
            || (IsUnderNamespace(containingType, "System.Net") && typeName != "global::System.Net.Sockets")
            || typeName == "global::System.Console"
            || typeName == "global::System.IO.File"
            || typeName == "global::System.GC")
        {
            fullName = $"{Strip(typeName)}.{memberName}";
            return true;
        }

        // Per-member bans.
        switch (typeName)
        {
            case "global::System.DateTime" when memberName is "Now" or "UtcNow" or "Today":
                fullName = $"System.DateTime.{memberName}";
                return true;
            case "global::System.Environment" when memberName is "TickCount" or "TickCount64":
                fullName = $"System.Environment.{memberName}";
                return true;
            case "global::System.Threading.Thread" when memberName is "Sleep":
                fullName = "System.Threading.Thread.Sleep";
                return true;
            case "global::System.Threading.Tasks.Task" when memberName is "Run" or "Delay":
                fullName = $"System.Threading.Tasks.Task.{memberName}";
                return true;
            case "global::System.Guid" when memberName is "NewGuid":
                fullName = "System.Guid.NewGuid";
                return true;
        }

        // object.GetHashCode() on a reference type (identity-based default
        // hash is unstable across runs). The receiver type drives the
        // reference-vs-value distinction, but the resolved member's containing
        // type is System.Object for the default; match on the member.
        if (memberName == "GetHashCode"
            && symbol is IMethodSymbol { Parameters.Length: 0 } getHashCode
            && getHashCode.ReceiverType is { IsReferenceType: true })
        {
            fullName = $"{Strip(getHashCode.ReceiverType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))}.GetHashCode";
            return true;
        }

        return false;
    }

    /// <summary>
    /// True iff <paramref name="symbol"/> is a member that the symbol-based
    /// banned set covers (used to collapse member-access chains). Mirrors the
    /// banned-type membership tests in <see cref="InspectMemberAccess"/>.
    /// </summary>
    private static bool IsBannedMemberSymbol(ISymbol symbol)
    {
        INamedTypeSymbol? containingType = symbol.ContainingType;
        if (containingType is null)
        {
            return false;
        }

        return IsType(containingType, "System.Random")
            || IsType(containingType, "System.Threading.Interlocked")
            || IsBannedTaskFamilyType(containingType);
    }

    // ==================================================================
    // Small utilities.
    // ==================================================================

    /// <summary>
    /// True iff <paramref name="type"/>'s fully-qualified display name equals
    /// <c>"global::" + <paramref name="metadataFullName"/></c>.
    /// </summary>
    private static bool IsType(INamedTypeSymbol? type, string metadataFullName)
    {
        if (type is null)
        {
            return false;
        }
        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            == "global::" + metadataFullName;
    }

    /// <summary>
    /// True iff <paramref name="type"/> is declared under
    /// <paramref name="namespaceName"/> (or a nested namespace of it).
    /// </summary>
    private static bool IsUnderNamespace(INamedTypeSymbol type, string namespaceName)
    {
        for (INamespaceSymbol? ns = type.ContainingNamespace;
             ns is { IsGlobalNamespace: false };
             ns = ns.ContainingNamespace)
        {
            string display = ns.ToDisplayString();
            if (display == namespaceName || display.StartsWith(namespaceName + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True iff <paramref name="expression"/> is a string literal (or an
    /// interpolated string with only literal text -- conservatively, only the
    /// plain string-literal form counts as a "literal" here).
    /// </summary>
    private static bool IsLiteralStringArgument(ExpressionSyntax expression)
        => expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.StringLiteralExpression);

    /// <summary>Strip a leading <c>global::</c> qualifier from a display name.</summary>
    private static string Strip(string fullyQualified)
        => fullyQualified.StartsWith("global::", StringComparison.Ordinal)
            ? fullyQualified.Substring("global::".Length)
            : fullyQualified;

    /// <summary>
    /// Emit the async/await diagnostic (XIL2CPP044) for an <c>async</c>
    /// declaration site.
    /// </summary>
    private static void EmitAsyncDeclaration(Pass3ResultBuilder builder, string module, Location location)
        => Emit(builder, module, DiagnosticCodes.SimPathAsyncAwaitBanned, DiagnosticSeverity.Error,
            "async/await is banned on sim-path TUs",
            "async", location);

    /// <summary>
    /// Record a banned-API hit: append a <see cref="BannedApiHit"/> and add
    /// the anchored diagnostic. The hit + diagnostic share the same 1-based
    /// span derived from <paramref name="location"/>.
    /// </summary>
    private static void Emit(
        Pass3ResultBuilder builder,
        string module,
        string code,
        DiagnosticSeverity severity,
        string message,
        string fullName,
        Location location)
    {
        FileLinePositionSpan lineSpan = location.GetLineSpan();
        string file = lineSpan.Path;
        int line = lineSpan.StartLinePosition.Line + 1;
        int column = lineSpan.StartLinePosition.Character + 1;

        builder.Add(new BannedApiHit(code, fullName, file, line, column));
        builder.AddDiagnostic(new DiagnosticRecord(
            severity, code, message, file, line, column, module));
    }
}

/// <summary>
/// One recorded sim-path banned-API hit per <c>/Documents/XIL2CPP.html</c>
/// Rev 4 Section 7.4 / 7.8: the <see cref="SimPathBannedApiAnalyzer"/> appends
/// one of these (via <see cref="Pass3ResultBuilder.Add{T}(T)"/>) for every
/// banned construct it flags, paired one-to-one with the emitted diagnostic.
/// </summary>
/// <param name="Code">The <c>XIL2CPP&lt;NNN&gt;</c> code emitted for the hit (a <see cref="DiagnosticCodes"/> constant).</param>
/// <param name="FullName">
/// The canonical banned-API identity (e.g. <c>System.DateTime.Now</c>,
/// <c>System.Threading.Interlocked.Increment</c>), or a short kind discriminant
/// for syntactic constructs (<c>"lock"</c>, <c>"await"</c>, <c>"async"</c>,
/// <c>"string-index"</c>, <c>"await foreach"</c>, <c>"[ThreadStatic]"</c>).
/// </param>
/// <param name="File">Source file path the hit was anchored to.</param>
/// <param name="Line">1-based source line of the hit.</param>
/// <param name="Column">1-based source column of the hit.</param>
public sealed record BannedApiHit(
    string Code,
    string FullName,
    string File,
    int Line,
    int Column);
