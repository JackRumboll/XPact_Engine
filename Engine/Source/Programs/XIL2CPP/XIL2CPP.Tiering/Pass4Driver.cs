// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;

namespace Simgenics.XPact.XIL2CPP.Tiering;

/// <summary>
/// XIL2CPP Pass 4 (tier classification, Pass 1 of the two-pass protocol) per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.1 / 3.2 / 3.3 and
/// <c>/Documents/XIL2CPP-Constraints.md</c> Section 2.8. Runs a per-module
/// fixpoint over the call graph that starts every emittable function at
/// Tier 2 and repeatedly demotes any function failing a Tier-2 clause until
/// no change, then produces a deterministic <see cref="TierTable"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The Tier-2 condition (the demotion clauses implemented here).</b> A
/// function <c>f</c> is Tier 2 IFF ALL hold; failing any one demotes it to
/// Tier 1 with the named reason:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>(a) not exported</b> -- a function reachable across the module DLL
///     boundary must wear the ABI-stable shim. A single module is its own DLL,
///     so "exported" is approximated by C#-level effective accessibility:
///     <c>public</c> / <c>protected</c> (incl. <c>protected internal</c>)
///     members of accessible types are part of the module's public surface and
///     treated as exported. <c>internal</c> / <c>private</c> members and the
///     bodies of anonymous functions (local functions / lambdas) are never
///     exported. Reason: <c>"exported"</c>.
///   </description></item>
///   <item><description>
///     <b>(b) not [XFunction(CanThrow = true)] / [CanThrow]</b>. Reason:
///     <c>"[XFunction(CanThrow = true)]"</c> or <c>"[CanThrow]"</c>.
///   </description></item>
///   <item><description>
///     <b>(c) every callee Tier 2</b> -- the fixpoint clause. A same-module
///     callee that is itself Tier 1 demotes the caller; a cross-module callee
///     that is NOT NoThrow-annotated (per the Pass-3
///     <see cref="CrossModuleNoThrowTable"/>) demotes the caller. Reason:
///     <c>"calls Tier-1 callee &lt;display&gt;"</c> or
///     <c>"calls non-NoThrow cross-module callee &lt;display&gt;"</c>.
///   </description></item>
///   <item><description>
///     <b>(d) body has no throw</b> -- a <c>throw</c> statement, a
///     <c>throw</c> expression, or a rethrow (<c>throw;</c>) demotes. Reason:
///     <c>"throws in body"</c>.
///   </description></item>
///   <item><description>
///     <b>(e) no unsafe / extern C++ call without a noexcept proof</b> -- an
///     <c>unsafe</c> context or a call to a P/Invoke (<c>extern</c> /
///     <c>[DllImport]</c>) method, which XIL2CPP cannot prove
///     <c>noexcept</c> at this pass, demotes. Reason:
///     <c>"unsafe context"</c> or <c>"calls extern C++ function &lt;display&gt;"</c>.
///   </description></item>
/// </list>
/// <para>
/// <b>Cross-module callees.</b> Whether a resolved callee is same-module,
/// cross-module-Tier2-eligible, cross-module-conservative-Tier1, or
/// missing-a-reflection-entry is decided by the Pass-3
/// <see cref="CrossModuleNoThrowAnalyzer"/> and recorded in the
/// <see cref="CrossModuleNoThrowTable"/> singleton this driver reads. The
/// driver does not re-run that lookup; it joins the table's per-caller
/// conservative-tier flags back to the functions it classifies by
/// <see cref="StableId"/> (the table's caller display and this pass's stable
/// id share the same <see cref="StableId.StableIdFormat"/>).
/// </para>
/// <para>
/// <b>Pass 4 is purely classificatory.</b> It surfaces no diagnostics. The
/// <c>[XFunction(NoThrow = true)]</c> proof obligation (XIL2CPP030) and the
/// missing-reflection-entry lookup failure (XIL2CPP036) are owned by the
/// Pass-3 <see cref="CrossModuleNoThrowAnalyzer"/>; re-raising them here would
/// double-emit. Pass 4 classifies; it does not validate.
/// </para>
/// <para>
/// <b>Determinism.</b> Functions are enumerated in canonical file order
/// (<see cref="Pass1Result.ParsedFiles"/> ordinal order) then document order,
/// and the resulting <see cref="TierTable"/> is sorted by
/// <see cref="StableId.Value"/> (ordinal). The fixpoint is order-independent
/// (it computes a least fixed point); the enumeration + sort make the output
/// byte-deterministic (gate X-IL2CPP-CSPATH-DET).
/// </para>
/// </remarks>
public static class Pass4Driver
{
    /// <summary>The unqualified C# spelling of the canonical CanThrow attribute.</summary>
    private const string XFunctionAttributeShortName = "XFunction";

    /// <summary>The metadata name of the canonical CanThrow attribute.</summary>
    private const string XFunctionAttributeMetadataName = "XFunctionAttribute";

    /// <summary>The named argument carrying the CanThrow claim.</summary>
    private const string CanThrowArgumentName = "CanThrow";

    /// <summary>The unqualified C# spelling of the standalone CanThrow marker.</summary>
    private const string CanThrowAttributeShortName = "CanThrow";

    /// <summary>The metadata name of the standalone CanThrow marker.</summary>
    private const string CanThrowAttributeMetadataName = "CanThrowAttribute";

    /// <summary>
    /// Run Pass 4 over <paramref name="unit"/> using the cross-module NoThrow
    /// status the Pass-3 result carries, returning the per-module tier table.
    /// </summary>
    /// <param name="unit">The Pass-2 normalized unit (wraps the authoritative Pass-1 binding info). Must not be null.</param>
    /// <param name="pass3">The Pass-3 result (its <see cref="CrossModuleNoThrowTable"/> singleton is the cross-module input). Must not be null.</param>
    /// <returns>The deterministic per-module tier table.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="unit"/> or <paramref name="pass3"/> is null.</exception>
    public static TierTable Run(NormalizedUnit unit, Pass3Result pass3)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(pass3);

        Pass1Result pass1 = unit.Pass1;

        // Cross-module conservative-tier flags from Pass 3, keyed by caller
        // display (which equals this pass's StableId.Value for the same
        // symbol). A caller flagged ConservativeTier1 has at least one
        // cross-module callee that is not NoThrow-annotated, so clause (c)
        // demotes it.
        CrossModuleNoThrowTable? crossModule = pass3.GetSingleton<CrossModuleNoThrowTable>();
        Dictionary<string, CrossModuleCallerTierFlag> callerFlags = new(StringComparer.Ordinal);
        if (crossModule is not null)
        {
            foreach (CrossModuleCallerTierFlag flag in crossModule.CallerFlags)
            {
                // The table's caller-flag displays are unique, but guard
                // against a duplicate by keeping the first (deterministic) one.
                callerFlags.TryAdd(flag.CallerDisplay, flag);
            }
        }

        // 1. Enumerate every emittable function deterministically and compute
        //    its static (non-fixpoint) facts + its same-module callee set.
        List<FuncNode> functions = CollectFunctions(unit);

        // Map symbol -> node index so a same-module callee resolves to a node.
        Dictionary<ISymbol, int> indexBySymbol = new(SymbolEqualityComparer.Default);
        for (int i = 0; i < functions.Count; i++)
        {
            // A symbol may declare multiple function nodes only for partial
            // methods (two declarations, one symbol). Keep the first so a
            // same-module callee resolves deterministically.
            indexBySymbol.TryAdd(functions[i].Symbol, i);
        }

        // 2. Seed: every function starts Tier 2 with an empty reason. Apply the
        //    static clauses (a), (b), (d), (e) + the cross-module portion of
        //    clause (c) immediately; record the resolved same-module callees so
        //    the fixpoint can propagate clause (c) for them.
        FunctionTier[] tier = new FunctionTier[functions.Count];
        string[] reason = new string[functions.Count];
        List<int>[] sameModuleCallees = new List<int>[functions.Count];

        for (int i = 0; i < functions.Count; i++)
        {
            FuncNode fn = functions[i];
            tier[i] = FunctionTier.Tier2;
            reason[i] = string.Empty;
            sameModuleCallees[i] = new List<int>();

            // Static demotion clauses, in priority order (the first failing
            // clause names the reason). Exported is the most operationally
            // significant (it forces the shim regardless of body), so it wins.
            if (fn.IsExported)
            {
                Demote(tier, reason, i, "exported");
            }
            else if (fn.CanThrowReason is { Length: > 0 })
            {
                Demote(tier, reason, i, fn.CanThrowReason);
            }
            else if (fn.UnsafeReason is { Length: > 0 })
            {
                Demote(tier, reason, i, fn.UnsafeReason);
            }
            else if (fn.ThrowsInBody)
            {
                Demote(tier, reason, i, "throws in body");
            }

            // Clause (c) for cross-module callees: a caller the Pass-3 table
            // flagged conservative Tier 1 calls at least one non-NoThrow
            // cross-module callee. Only record this when the function is still
            // Tier 2 (so the static reason wins when both apply).
            if (tier[i] == FunctionTier.Tier2
                && callerFlags.TryGetValue(fn.Id.Value, out CrossModuleCallerTierFlag? caller)
                && caller.ConservativeTier1)
            {
                string callee = caller.ForcingCallees.Count > 0
                    ? caller.ForcingCallees[0]
                    : "(unknown)";
                Demote(tier, reason, i,
                    $"calls non-NoThrow cross-module callee {callee}");
            }

            // Resolve same-module managed callees for the fixpoint (clause (c)).
            foreach (ISymbol calleeSymbol in fn.SameModuleCalleeSymbols)
            {
                if (indexBySymbol.TryGetValue(calleeSymbol, out int calleeIndex))
                {
                    sameModuleCallees[i].Add(calleeIndex);
                }
            }
        }

        // 3. Fixpoint over clause (c) for same-module callees: repeatedly
        //    demote any still-Tier-2 caller that calls a Tier-1 same-module
        //    callee, until no change. Converges in at most N passes.
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < functions.Count; i++)
            {
                if (tier[i] == FunctionTier.Tier1)
                {
                    continue;
                }
                foreach (int calleeIndex in sameModuleCallees[i])
                {
                    if (tier[calleeIndex] == FunctionTier.Tier1)
                    {
                        Demote(tier, reason, i,
                            $"calls Tier-1 callee {functions[calleeIndex].Id.Value}");
                        changed = true;
                        break;
                    }
                }
            }
        }

        // 4. Build the classification list (the TierTable ctor sorts it).
        List<TierClassification> classifications = new(functions.Count);
        for (int i = 0; i < functions.Count; i++)
        {
            classifications.Add(new TierClassification(
                functions[i].Id,
                functions[i].Display,
                tier[i],
                tier[i] == FunctionTier.Tier2 ? string.Empty : reason[i]));
        }

        return new TierTable(pass1.ModuleName, classifications);
    }

    /// <summary>
    /// Demote function <paramref name="i"/> to Tier 1, recording
    /// <paramref name="why"/> only if it is not already demoted (so the FIRST
    /// reason -- the highest-priority clause -- is preserved).
    /// </summary>
    private static void Demote(FunctionTier[] tier, string[] reason, int i, string why)
    {
        if (tier[i] == FunctionTier.Tier2)
        {
            tier[i] = FunctionTier.Tier1;
            reason[i] = why;
        }
    }

    /// <summary>
    /// Enumerate every emittable function in the unit (methods, constructors,
    /// destructors, operators, conversion operators, property / indexer /
    /// event accessors -- block-bodied and expression-bodied --, local
    /// functions, and lambdas / anonymous methods) in canonical file order
    /// then document order, computing each one's static facts + same-module
    /// callee symbols.
    /// </summary>
    private static List<FuncNode> CollectFunctions(NormalizedUnit unit)
    {
        Pass1Result pass1 = unit.Pass1;
        List<FuncNode> functions = new();

        foreach (ModuleParser.ParsedFile parsed in pass1.ParsedFiles)
        {
            SemanticModel model = pass1.GetSemanticModel(parsed.Tree);
            SyntaxNode root = parsed.Tree.GetRoot();

            foreach (SyntaxNode node in root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    // Methods, constructors, destructors, operators, conversion
                    // operators. (BaseMethodDeclarationSyntax covers them all.)
                    case BaseMethodDeclarationSyntax method:
                        TryAddFunction(unit, model, method, method, functions);
                        break;

                    // Property / indexer / event accessors (get / set / init /
                    // add / remove), block- or expression-bodied.
                    case AccessorDeclarationSyntax accessor:
                        TryAddFunction(unit, model, accessor, accessor, functions);
                        break;

                    // Expression-bodied property / indexer (the arrow clause is
                    // the getter body; its declared symbol is the get accessor).
                    case ArrowExpressionClauseSyntax arrow
                        when arrow.Parent is PropertyDeclarationSyntax or IndexerDeclarationSyntax:
                        TryAddExpressionBodiedAccessor(unit, model, arrow, functions);
                        break;

                    // Local functions.
                    case LocalFunctionStatementSyntax local:
                        TryAddFunction(unit, model, local, local, functions);
                        break;

                    // Lambdas + anonymous methods (delegate { }).
                    case AnonymousFunctionExpressionSyntax lambda:
                        TryAddAnonymousFunction(unit, model, lambda, functions);
                        break;
                }
            }
        }

        return functions;
    }

    /// <summary>
    /// Resolve <paramref name="declaration"/>'s declared symbol and, if it is
    /// a method symbol, add a node whose body for throw / callee scanning is
    /// <paramref name="bodyOwner"/>.
    /// </summary>
    private static void TryAddFunction(
        NormalizedUnit unit,
        SemanticModel model,
        SyntaxNode declaration,
        SyntaxNode bodyOwner,
        List<FuncNode> functions)
    {
        if (model.GetDeclaredSymbol(declaration) is not IMethodSymbol symbol)
        {
            return;
        }
        functions.Add(BuildNode(unit, model, symbol, bodyOwner, isAnonymous: false));
    }

    /// <summary>
    /// Add a node for an expression-bodied property / indexer: the get
    /// accessor symbol with the arrow expression as its body.
    /// </summary>
    private static void TryAddExpressionBodiedAccessor(
        NormalizedUnit unit,
        SemanticModel model,
        ArrowExpressionClauseSyntax arrow,
        List<FuncNode> functions)
    {
        // The declared symbol of the property / indexer is the property symbol;
        // its GetMethod is the emittable accessor.
        ISymbol? owner = model.GetDeclaredSymbol(arrow.Parent!);
        IMethodSymbol? getter = owner switch
        {
            IPropertySymbol p => p.GetMethod,
            _ => null,
        };
        if (getter is null)
        {
            return;
        }
        functions.Add(BuildNode(unit, model, getter, arrow, isAnonymous: false));
    }

    /// <summary>
    /// Add a node for a lambda / anonymous method: its converted method symbol
    /// with the lambda expression as its body.
    /// </summary>
    private static void TryAddAnonymousFunction(
        NormalizedUnit unit,
        SemanticModel model,
        AnonymousFunctionExpressionSyntax lambda,
        List<FuncNode> functions)
    {
        if (model.GetSymbolInfo(lambda).Symbol is not IMethodSymbol symbol)
        {
            return;
        }
        functions.Add(BuildNode(unit, model, symbol, lambda, isAnonymous: true));
    }

    /// <summary>
    /// Build a node: compute the stable id, the static demotion facts
    /// (exported, CanThrow attribute, unsafe / extern call, body throw), and
    /// the same-module callee symbols, scanning only the function's OWN body
    /// (nested local functions / lambdas are their own nodes and are excluded
    /// from the parent's scan).
    /// </summary>
    private static FuncNode BuildNode(
        NormalizedUnit unit,
        SemanticModel model,
        IMethodSymbol symbol,
        SyntaxNode bodyOwner,
        bool isAnonymous)
    {
        string display = symbol.ToDisplayString(StableId.StableIdFormat);
        StableId id = new(display);

        // Clause (a): exported. Anonymous functions are never exported (they
        // are emitted inside their owning module's closure / free function).
        bool exported = !isAnonymous && IsExported(symbol);

        // Clause (b): [XFunction(CanThrow = true)] / [CanThrow].
        string canThrowReason = CanThrowReasonFor(symbol);

        // Scan the function's own body once for clauses (d) + (e) + the
        // same-module callee set, excluding nested function bodies.
        bool throwsInBody = false;
        string unsafeReason = string.Empty;
        List<ISymbol> sameModuleCallees = new();

        IAssemblySymbol sourceAssembly = unit.Pass1.Compilation.Assembly;

        foreach (SyntaxNode node in EnumerateOwnBody(bodyOwner))
        {
            switch (node)
            {
                case ThrowStatementSyntax:
                case ThrowExpressionSyntax:
                    throwsInBody = true;
                    break;

                // Clause (e): an unsafe statement context the pass cannot
                // prove noexcept.
                case UnsafeStatementSyntax when unsafeReason.Length == 0:
                    unsafeReason = "unsafe context";
                    break;

                case InvocationExpressionSyntax invocation:
                    if (model.GetSymbolInfo(invocation).Symbol is IMethodSymbol callee)
                    {
                        ClassifyCallee(callee, sourceAssembly, sameModuleCallees, ref unsafeReason);
                    }
                    break;

                case ObjectCreationExpressionSyntax creation:
                    if (model.GetSymbolInfo(creation).Symbol is IMethodSymbol ctor)
                    {
                        ClassifyCallee(ctor, sourceAssembly, sameModuleCallees, ref unsafeReason);
                    }
                    break;

                case ImplicitObjectCreationExpressionSyntax implicitCreation:
                    if (model.GetSymbolInfo(implicitCreation).Symbol is IMethodSymbol implicitCtor)
                    {
                        ClassifyCallee(implicitCtor, sourceAssembly, sameModuleCallees, ref unsafeReason);
                    }
                    break;
            }
        }

        // A method declared `unsafe` (modifier) is itself an unsafe context.
        if (unsafeReason.Length == 0 && DeclaresUnsafeModifier(symbol))
        {
            unsafeReason = "unsafe context";
        }

        return new FuncNode(
            symbol,
            id,
            display,
            exported,
            canThrowReason,
            unsafeReason,
            throwsInBody,
            sameModuleCallees);
    }

    /// <summary>
    /// Classify a resolved callee: an <c>extern</c> / P-Invoke method is a
    /// clause-(e) extern C++ call (records the unsafe reason); a same-assembly
    /// managed callee is added to the same-module callee set for the fixpoint;
    /// a cross-module callee is left to the Pass-3 conservative-tier flag
    /// (already folded in the caller).
    /// </summary>
    private static void ClassifyCallee(
        IMethodSymbol callee,
        IAssemblySymbol sourceAssembly,
        List<ISymbol> sameModuleCallees,
        ref string unsafeReason)
    {
        // Clause (e): extern / DllImport callee -> a C++/native function the
        // pass cannot prove noexcept.
        if (callee.IsExtern && unsafeReason.Length == 0)
        {
            unsafeReason = $"calls extern C++ function {callee.ToDisplayString(StableId.StableIdFormat)}";
            return;
        }

        // Same-module managed callee participates in the clause-(c) fixpoint.
        // Reduce to the original definition so a constructed generic /
        // reduced-extension symbol matches the declared node's symbol.
        IMethodSymbol definition = callee.OriginalDefinition;
        IAssemblySymbol? calleeAssembly = definition.ContainingAssembly;
        if (calleeAssembly is not null
            && SymbolEqualityComparer.Default.Equals(calleeAssembly, sourceAssembly))
        {
            sameModuleCallees.Add(definition);
        }
        // Cross-module managed callees: the Pass-3 CrossModuleNoThrowTable
        // already captured whether they force conservative Tier 1; nothing to
        // do here (the caller folds that flag in seed step 2).
    }

    /// <summary>
    /// Enumerate the descendant nodes that belong to <paramref name="bodyOwner"/>'s
    /// OWN body, stopping the walk at any nested anonymous function or local
    /// function (those are classified as their own nodes, so their throws /
    /// calls must not be attributed to the enclosing function).
    /// </summary>
    private static IEnumerable<SyntaxNode> EnumerateOwnBody(SyntaxNode bodyOwner)
    {
        // DescendantNodes with a descend predicate that refuses to enter a
        // nested function body. The bodyOwner itself is excluded (it is the
        // declaration, not a body statement); for a local function / method we
        // want its body's statements, which are descendants.
        return bodyOwner.DescendantNodes(descendIntoChildren: child =>
        {
            // Always descend into the body owner's own subtree, but do not
            // descend INTO a nested function that is not the owner.
            if (ReferenceEquals(child, bodyOwner))
            {
                return true;
            }
            return child is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);
        }).Where(n => !IsInsideNestedFunction(n, bodyOwner));
    }

    /// <summary>
    /// True iff <paramref name="node"/> sits inside a nested local function /
    /// anonymous function whose nearest enclosing function is NOT
    /// <paramref name="bodyOwner"/>. A defensive complement to the descend
    /// predicate (which already prunes the subtree); together they guarantee a
    /// node is attributed to exactly one function.
    /// </summary>
    private static bool IsInsideNestedFunction(SyntaxNode node, SyntaxNode bodyOwner)
    {
        for (SyntaxNode? cursor = node.Parent;
             cursor is not null && !ReferenceEquals(cursor, bodyOwner);
             cursor = cursor.Parent)
        {
            if (cursor is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Approximate "exported from the module DLL" for a single module by the
    /// symbol's effective C#-level accessibility: a <c>public</c> /
    /// <c>protected</c> (incl. <c>protected internal</c>) member of an
    /// accessible-from-outside type is part of the module's public surface, so
    /// it must wear the ABI-stable shim. <c>internal</c> / <c>private</c>
    /// members never cross the DLL boundary.
    /// </summary>
    private static bool IsExported(IMethodSymbol symbol)
    {
        // Walk the containing-type chain: a member is externally reachable
        // only if every enclosing type is also externally reachable.
        for (INamedTypeSymbol? type = symbol.ContainingType; type is not null; type = type.ContainingType)
        {
            if (!IsExternallyVisible(type.DeclaredAccessibility))
            {
                return false;
            }
        }
        return IsExternallyVisible(symbol.DeclaredAccessibility);
    }

    /// <summary>
    /// True iff an accessibility lets a symbol be referenced from OUTSIDE the
    /// declaring assembly (DLL): <c>public</c>, <c>protected</c>, and
    /// <c>protected internal</c> are externally visible; <c>internal</c>,
    /// <c>private protected</c>, <c>private</c>, and the not-applicable case
    /// are not.
    /// </summary>
    private static bool IsExternallyVisible(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => true,
        Accessibility.Protected => true,
        Accessibility.ProtectedOrInternal => true, // protected internal
        _ => false,
    };

    /// <summary>
    /// Return the clause-(b) demotion reason if <paramref name="symbol"/>
    /// carries <c>[XFunction(CanThrow = true)]</c> or a standalone
    /// <c>[CanThrow]</c>, else the empty string.
    /// </summary>
    private static string CanThrowReasonFor(IMethodSymbol symbol)
    {
        foreach (AttributeData attr in symbol.GetAttributes())
        {
            INamedTypeSymbol? cls = attr.AttributeClass;
            if (cls is null)
            {
                continue;
            }

            // [XFunction(CanThrow = true)].
            if (cls.MetadataName == XFunctionAttributeMetadataName
                || cls.Name == XFunctionAttributeShortName
                || cls.Name == XFunctionAttributeMetadataName)
            {
                foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
                {
                    if (named.Key == CanThrowArgumentName
                        && named.Value.Value is bool b
                        && b)
                    {
                        return "[XFunction(CanThrow = true)]";
                    }
                }
            }

            // Standalone [CanThrow].
            if (cls.MetadataName == CanThrowAttributeMetadataName
                || cls.Name == CanThrowAttributeShortName
                || cls.Name == CanThrowAttributeMetadataName)
            {
                return "[CanThrow]";
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// True iff <paramref name="symbol"/> is declared with the <c>unsafe</c>
    /// modifier on any of its declaring syntax references.
    /// </summary>
    private static bool DeclaresUnsafeModifier(IMethodSymbol symbol)
    {
        foreach (SyntaxReference syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            SyntaxNode node = syntaxRef.GetSyntax();
            SyntaxTokenList modifiers = node switch
            {
                BaseMethodDeclarationSyntax m => m.Modifiers,
                LocalFunctionStatementSyntax l => l.Modifiers,
                AccessorDeclarationSyntax a => a.Modifiers,
                _ => default,
            };
            foreach (SyntaxToken modifier in modifiers)
            {
                if (modifier.IsKind(SyntaxKind.UnsafeKeyword))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// One emittable function's classification inputs: its symbol, stable id,
    /// display, the static demotion facts, and the same-module callee symbols
    /// the clause-(c) fixpoint chains over.
    /// </summary>
    private sealed class FuncNode
    {
        public FuncNode(
            IMethodSymbol symbol,
            StableId id,
            string display,
            bool isExported,
            string canThrowReason,
            string unsafeReason,
            bool throwsInBody,
            IReadOnlyList<ISymbol> sameModuleCalleeSymbols)
        {
            Symbol = symbol;
            Id = id;
            Display = display;
            IsExported = isExported;
            CanThrowReason = canThrowReason;
            UnsafeReason = unsafeReason;
            ThrowsInBody = throwsInBody;
            SameModuleCalleeSymbols = sameModuleCalleeSymbols;
        }

        public IMethodSymbol Symbol { get; }

        public StableId Id { get; }

        public string Display { get; }

        public bool IsExported { get; }

        /// <summary>Clause (b) reason, or empty if not CanThrow-attributed.</summary>
        public string CanThrowReason { get; }

        /// <summary>Clause (e) reason, or empty if no unsafe / extern call.</summary>
        public string UnsafeReason { get; }

        public bool ThrowsInBody { get; }

        public IReadOnlyList<ISymbol> SameModuleCalleeSymbols { get; }
    }
}
