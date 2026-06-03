// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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
/// One closed generic instantiation reached by the Pass-3 closed-walk per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.8: a per-instantiation
/// <c>FClass</c> to emit at Pass 6 (e.g. <c>TArray_int</c>). Captured here so
/// the emit phase enumerates exactly the closed tree the walk reached, in a
/// deterministic order.
/// </summary>
/// <param name="ClosedDisplay">
/// The fully-qualified display of the closed instantiation
/// (e.g. <c>global::M.Box&lt;int&gt;</c>); the emit-side mangling key.
/// </param>
/// <param name="OpenDefinitionDisplay">
/// The fully-qualified display of the open generic definition
/// (e.g. <c>global::M.Box&lt;T&gt;</c>) this instantiation closes over.
/// </param>
/// <param name="TypeArgumentDisplays">
/// The closed type arguments, in declaration order, each as a
/// fully-qualified display. Empty for a non-generic reached via the walk's
/// type-argument recursion (never recorded; only generic instantiations are).
/// </param>
/// <param name="Depth">
/// The 1-based recursion depth at which the instantiation was reached
/// (a seed instantiation is depth 1; a generic type-argument of a seed is
/// depth 2, and so on). Bounded at 16 per Section 5.8.
/// </param>
/// <param name="IsPrincipalModule">
/// True iff THIS module is the principal (emit-owning) module for the closed
/// instantiation. Per the Phase 6.b orchestrator decision the pipeline is
/// single-module, so every reached instantiation is principal here; the field
/// exists so the multi-module cross-walk (Section 5.8 "principal module"
/// rule, <c>XIL2CPP129</c>) can flip it without a schema change.
/// </param>
public sealed record ClosedInstantiation(
    string ClosedDisplay,
    string OpenDefinitionDisplay,
    IReadOnlyList<string> TypeArgumentDisplays,
    int Depth,
    bool IsPrincipalModule = true);

/// <summary>
/// The Pass-3 closed-instantiation walk product for one module per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.8: the deterministic,
/// de-duplicated list of <see cref="ClosedInstantiation"/> entries the emit
/// phase (Pass 6) materialises as per-instantiation <c>FClass</c>es.
/// </summary>
/// <param name="Instantiations">
/// Every distinct closed generic instantiation reached by the walk, ordered
/// deterministically by <see cref="ClosedInstantiation.ClosedDisplay"/>
/// (ordinal). Distinct by closed display, so a closed instantiation reached
/// from several sites is recorded once.
/// </param>
public sealed record GenericClosureResult(
    IReadOnlyList<ClosedInstantiation> Instantiations);

/// <summary>
/// Pass-3 generic-instantiation closed-walk analyzer per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.8. Enumerates every generic
/// instantiation reachable in the module (from declared members, signatures,
/// base lists, and bound expressions), then recursively walks the
/// type-argument closure of each, recording the reached
/// <see cref="ClosedInstantiation"/>s for Pass-6 emit and emitting the
/// generic diagnostics:
/// <list type="bullet">
///   <item><description><b>XIL2CPP123</b> -- closure exceeds depth 16 (a node at depth 17).</description></item>
///   <item><description><b>XIL2CPP124</b> -- a cyclic closure (the same <c>(openGeneric, typeArgs)</c> re-encountered on the active path).</description></item>
///   <item><description><b>XIL2CPP125</b> -- a <c>where T : unmanaged</c> type parameter bound to an XObject-derived argument.</description></item>
///   <item><description><b>XIL2CPP129</b> -- a cross-module duplicate-FClass attribution conflict (reserved; single-module Phase 6.b never trips it, but the code is owned here).</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Cycle detection.</b> Per Section 5.8 the walk keeps a
/// <c>HashSet&lt;(OpenGeneric, TypeArgs)&gt;</c> of the active recursion path.
/// A self-referential closed instantiation (the SAME open definition closed
/// over the SAME arguments re-reached while it is still on the stack) is a
/// cycle and stops the recursion with <c>XIL2CPP124</c>. The constraint
/// type's self-reference (<c>where T : IComparable&lt;T&gt;</c>) is NOT walked
/// for cycle detection per Section 5.8.
/// </para>
/// <para>
/// <b>Depth.</b> A seed instantiation is depth 1; recursing into a generic
/// type argument increments depth. Reaching depth 17 (one past the bound of
/// 16) emits <c>XIL2CPP123</c> and stops descending that branch.
/// </para>
/// <para>
/// <b>Determinism.</b> Seeds are gathered in deterministic member /
/// declaration order (<see cref="AnalyzerHelpers"/>), the closure recursion
/// is depth-first in type-argument order, recorded instantiations are
/// de-duplicated by closed display, and the singleton result is sorted by
/// closed display (ordinal). Diagnostics are emitted at first-encounter and
/// de-duplicated by (code, closed display) so a closed instantiation reached
/// from many sites yields one diagnostic. No ambient state / DateTime /
/// Random (gate X-IL2CPP-CSPATH-DET).
/// </para>
/// </remarks>
public sealed class GenericInstantiationAnalyzer : ISemanticAnalyzer
{
    /// <summary>The recursion depth bound per Section 5.8.</summary>
    private const int MaxDepth = 16;

    /// <inheritdoc/>
    public string Name => "GenericInstantiationAnalyzer";

    /// <inheritdoc/>
    public void Analyze(NormalizedUnit unit, Pass3ResultBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(builder);

        // Recorded closed instantiations, de-duplicated by closed display.
        Dictionary<string, ClosedInstantiation> recorded = new(StringComparer.Ordinal);

        // Diagnostics emitted, de-duplicated by (code + closed display) so a
        // closed instantiation reached from N sites yields one record.
        HashSet<string> emittedDiagnostics = new(StringComparer.Ordinal);
        List<DiagnosticRecord> diagnostics = new();

        // The active recursion-path cycle set: (open-definition, type-args)
        // value-keys per Section 5.8.
        HashSet<ClosureKey> activePath = new();

        Pass1Result pass1 = unit.Pass1;
        string module = pass1.ModuleName;

        // Gather seeds deterministically: every generic instantiation that
        // appears in the module's parsed trees, paired with a span for
        // diagnostics. We iterate trees in canonical order; within a tree,
        // document order; for each TypeSyntax / expression node we ask the
        // authoritative semantic model for the bound symbol.
        foreach (Seed seed in EnumerateSeeds(unit))
        {
            Walk(
                seed.Type,
                depth: 1,
                seed.Span,
                module,
                activePath,
                recorded,
                emittedDiagnostics,
                diagnostics);
        }

        foreach (DiagnosticRecord d in diagnostics)
        {
            builder.AddDiagnostic(d);
        }

        // Deterministic singleton: sort by closed display (ordinal).
        List<ClosedInstantiation> ordered = recorded.Values
            .OrderBy(c => c.ClosedDisplay, StringComparer.Ordinal)
            .ToList();
        builder.SetSingleton(new GenericClosureResult(ordered));
    }

    /// <summary>
    /// Recursively walk the type-argument closure of one closed generic
    /// instantiation, recording it + descending into generic type arguments,
    /// applying the depth bound, cycle detection, and unmanaged-constraint
    /// check.
    /// </summary>
    private static void Walk(
        INamedTypeSymbol closed,
        int depth,
        SourceSpan span,
        string module,
        HashSet<ClosureKey> activePath,
        Dictionary<string, ClosedInstantiation> recorded,
        HashSet<string> emittedDiagnostics,
        List<DiagnosticRecord> diagnostics)
    {
        // Only closed (constructed, non-unbound) generic instantiations are
        // FClass-bearing nodes in the walk.
        if (!IsClosedGeneric(closed))
        {
            return;
        }

        string closedDisplay = closed.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Depth bound: depth 17 (one past 16) is the violation.
        if (depth > MaxDepth)
        {
            Emit(
                DiagnosticCodes.GenericInstantiationDepthExceeded,
                closedDisplay,
                span,
                module,
                $"Generic instantiation closure exceeds depth {MaxDepth} at '{closedDisplay}'.",
                emittedDiagnostics,
                diagnostics);
            return;
        }

        ClosureKey key = ClosureKey.From(closed);

        // Cycle detection: the SAME (open, args) already on the active path.
        if (activePath.Contains(key))
        {
            Emit(
                DiagnosticCodes.CyclicGenericTypeClosure,
                closedDisplay,
                span,
                module,
                $"Cyclic generic type closure detected at '{closedDisplay}'.",
                emittedDiagnostics,
                diagnostics);
            return;
        }

        // Unmanaged-constraint satisfaction: a type parameter declared
        // 'where T : unmanaged' bound to an XObject-derived argument.
        CheckUnmanagedConstraints(closed, closedDisplay, span, module, emittedDiagnostics, diagnostics);

        // Record the reached instantiation (first encounter wins -- the
        // shallowest depth a given closed display is reached at).
        if (!recorded.ContainsKey(closedDisplay))
        {
            recorded.Add(closedDisplay, new ClosedInstantiation(
                ClosedDisplay: closedDisplay,
                OpenDefinitionDisplay: closed.OriginalDefinition
                    .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                TypeArgumentDisplays: closed.TypeArguments
                    .Select(t => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                    .ToList(),
                Depth: depth,
                IsPrincipalModule: true));
        }

        // Descend into the reachable closed type tree (Section 5.8). The
        // reachable children of a closed instantiation are:
        //   (a) its closed generic type arguments (in declaration order), and
        //   (b) the SUBSTITUTED instance field / property types of the
        //       instantiation (Roslyn substitutes T at construction, so
        //       Node<int>.Self has type Node<int>) -- this is what surfaces a
        //       self-referential closure as a cycle.
        // The constraint type's self-reference (where T : IComparable<T>) is
        // intentionally NOT walked per Section 5.8.
        activePath.Add(key);
        foreach (INamedTypeSymbol child in EnumerateReachableChildren(closed))
        {
            Walk(
                child,
                depth + 1,
                span,
                module,
                activePath,
                recorded,
                emittedDiagnostics,
                diagnostics);
        }
        activePath.Remove(key);
    }

    /// <summary>
    /// Enumerate the closed generic types reachable in one step from
    /// <paramref name="closed"/>, deterministically: first its closed generic
    /// type arguments (declaration order), then the substituted instance
    /// field / property types of the instantiation (member declaration order).
    /// Non-generic and open children are filtered out (only closed generics
    /// bear an FClass and can extend the walk).
    /// </summary>
    private static IEnumerable<INamedTypeSymbol> EnumerateReachableChildren(INamedTypeSymbol closed)
    {
        foreach (ITypeSymbol arg in closed.TypeArguments)
        {
            if (arg is INamedTypeSymbol argNamed && IsClosedGeneric(argNamed))
            {
                yield return argNamed;
            }
        }

        foreach (ISymbol member in closed.GetMembers())
        {
            ITypeSymbol? memberType = member switch
            {
                IFieldSymbol { IsStatic: false } field => field.Type,
                IPropertySymbol { IsStatic: false } property => property.Type,
                _ => null,
            };

            if (memberType is INamedTypeSymbol memberNamed && IsClosedGeneric(memberNamed))
            {
                yield return memberNamed;
            }
        }
    }

    /// <summary>
    /// Emit <c>XIL2CPP125</c> for any <c>where T : unmanaged</c> type
    /// parameter on <paramref name="closed"/> bound to an XObject-derived
    /// type argument.
    /// </summary>
    private static void CheckUnmanagedConstraints(
        INamedTypeSymbol closed,
        string closedDisplay,
        SourceSpan span,
        string module,
        HashSet<string> emittedDiagnostics,
        List<DiagnosticRecord> diagnostics)
    {
        INamedTypeSymbol definition = closed.OriginalDefinition;
        ImmutableArray<ITypeParameterSymbol> parameters = definition.TypeParameters;
        ImmutableArray<ITypeSymbol> arguments = closed.TypeArguments;
        int count = Math.Min(parameters.Length, arguments.Length);
        for (int i = 0; i < count; i++)
        {
            ITypeParameterSymbol tp = parameters[i];
            if (!tp.HasUnmanagedTypeConstraint)
            {
                continue;
            }

            if (arguments[i] is INamedTypeSymbol argNamed
                && AnalyzerHelpers.IsXObjectDerived(argNamed))
            {
                string argDisplay = argNamed.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                Emit(
                    DiagnosticCodes.UnmanagedConstraintViolatedByXObject,
                    closedDisplay + "::" + argDisplay,
                    span,
                    module,
                    $"Generic constraint 'where {tp.Name} : unmanaged' on '{closedDisplay}' "
                        + $"violated by XObject-derived type argument '{argDisplay}'.",
                    emittedDiagnostics,
                    diagnostics);
            }
        }
    }

    /// <summary>
    /// Emit a diagnostic once per (code, dedup-key) pair, preserving
    /// first-encounter order.
    /// </summary>
    private static void Emit(
        string code,
        string dedupKey,
        SourceSpan span,
        string module,
        string message,
        HashSet<string> emittedDiagnostics,
        List<DiagnosticRecord> diagnostics)
    {
        string fullKey = code + "|" + dedupKey;
        if (!emittedDiagnostics.Add(fullKey))
        {
            return;
        }

        diagnostics.Add(span.ToDiagnostic(DiagnosticSeverity.Error, code, message, module));
    }

    /// <summary>
    /// Enumerate the seed generic instantiations across the unit's parsed
    /// trees in a deterministic (tree, document) order, pairing each with a
    /// 1-based source span for diagnostics. A seed is any closed generic
    /// <see cref="INamedTypeSymbol"/> the authoritative semantic model binds a
    /// <see cref="TypeSyntax"/> or expression to.
    /// </summary>
    private static IEnumerable<Seed> EnumerateSeeds(NormalizedUnit unit)
    {
        Pass1Result pass1 = unit.Pass1;
        foreach (ModuleParser.ParsedFile parsed in pass1.ParsedFiles)
        {
            SemanticModel model = pass1.GetSemanticModel(parsed.Tree);
            SyntaxNode root = parsed.Tree.GetRoot();

            foreach (SyntaxNode node in root.DescendantNodesAndSelf())
            {
                INamedTypeSymbol? candidate = TryBindGeneric(node, model);
                if (candidate is not null && IsClosedGeneric(candidate))
                {
                    yield return new Seed(candidate, SpanOf(node, parsed.AbsolutePath));
                }
            }
        }
    }

    /// <summary>
    /// Bind a syntax node to a closed generic <see cref="INamedTypeSymbol"/>
    /// when it denotes one (a generic-name type reference, or an
    /// object-creation / invocation whose bound type / return type is a closed
    /// generic). Returns null otherwise.
    /// </summary>
    private static INamedTypeSymbol? TryBindGeneric(SyntaxNode node, SemanticModel model)
    {
        switch (node)
        {
            // A generic-name type reference: List<int>, Box<Box<int>>, etc.
            // GenericNameSyntax covers both qualified and unqualified uses;
            // the inner generic name of a QualifiedNameSyntax is itself a
            // GenericNameSyntax descendant, so we need not special-case it.
            case GenericNameSyntax generic:
            {
                ISymbol? symbol = model.GetSymbolInfo(generic).Symbol;
                return symbol switch
                {
                    INamedTypeSymbol named => named,
                    IMethodSymbol method => method.ContainingType,
                    _ => null,
                };
            }

            // object-creation: new Box<int>() -- bind the created type.
            case ObjectCreationExpressionSyntax creation:
            {
                return model.GetTypeInfo(creation).Type as INamedTypeSymbol;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// True iff <paramref name="type"/> is a closed (constructed, non-unbound)
    /// generic named type (it has type arguments, none of which is an
    /// unbound-generic placeholder). The open definition
    /// (<c>List&lt;T&gt;</c>) and unbound generics (<c>typeof(List&lt;&gt;)</c>)
    /// are NOT closed instantiations.
    /// </summary>
    private static bool IsClosedGeneric(INamedTypeSymbol type)
    {
        if (!type.IsGenericType || type.IsUnboundGenericType)
        {
            return false;
        }

        if (type.TypeArguments.Length == 0)
        {
            return false;
        }

        // An open definition has its type arguments equal to its type
        // parameters (all are TypeParameter symbols). A node whose argument
        // is still a type parameter is not yet closed for our walk.
        foreach (ITypeSymbol arg in type.TypeArguments)
        {
            if (arg.TypeKind == TypeKind.TypeParameter || arg.TypeKind == TypeKind.Error)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Build a 1-based <see cref="SourceSpan"/> for a node.</summary>
    private static SourceSpan SpanOf(SyntaxNode node, string filePath)
    {
        FileLinePositionSpan lineSpan = node.GetLocation().GetLineSpan();
        LinePosition start = lineSpan.StartLinePosition;
        LinePosition end = lineSpan.EndLinePosition;
        return new SourceSpan(
            file: filePath,
            startLine: start.Line + 1,
            startColumn: start.Character + 1,
            endLine: end.Line + 1,
            endColumn: end.Character + 1,
            validate: false);
    }

    /// <summary>A seed instantiation paired with its diagnostic span.</summary>
    private readonly struct Seed
    {
        public Seed(INamedTypeSymbol type, SourceSpan span)
        {
            Type = type;
            Span = span;
        }

        public INamedTypeSymbol Type { get; }

        public SourceSpan Span { get; }
    }

    /// <summary>
    /// A value-equatable cycle key: the open generic definition plus the
    /// ordered closed type arguments, per the Section 5.8
    /// <c>HashSet&lt;(OpenGeneric, TypeArgs)&gt;</c>. Symbol identity uses
    /// <see cref="SymbolEqualityComparer.Default"/>.
    /// </summary>
    private readonly struct ClosureKey : IEquatable<ClosureKey>
    {
        private readonly INamedTypeSymbol _open;
        private readonly ITypeSymbol[] _args;

        private ClosureKey(INamedTypeSymbol open, ITypeSymbol[] args)
        {
            _open = open;
            _args = args;
        }

        public static ClosureKey From(INamedTypeSymbol closed)
            => new(closed.OriginalDefinition, closed.TypeArguments.ToArray());

        public bool Equals(ClosureKey other)
        {
            if (!SymbolEqualityComparer.Default.Equals(_open, other._open))
            {
                return false;
            }
            if (_args.Length != other._args.Length)
            {
                return false;
            }
            for (int i = 0; i < _args.Length; i++)
            {
                if (!SymbolEqualityComparer.Default.Equals(_args[i], other._args[i]))
                {
                    return false;
                }
            }
            return true;
        }

        public override bool Equals(object? obj) => obj is ClosureKey other && Equals(other);

        public override int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(_open, SymbolEqualityComparer.Default);
            foreach (ITypeSymbol arg in _args)
            {
                hash.Add(arg, SymbolEqualityComparer.Default);
            }
            return hash.ToHashCode();
        }
    }
}
