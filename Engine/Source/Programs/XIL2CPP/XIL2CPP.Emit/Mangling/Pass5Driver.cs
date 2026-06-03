// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tiering;

namespace Simgenics.XPact.XIL2CPP.Emit.Mangling;

/// <summary>
/// XIL2CPP Pass 5 (mangling assignment) per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2: walk every emittable
/// function deterministically (the SAME enumeration Pass 4 uses -- canonical
/// file order then document order), compute each one's contract-versioned
/// length-prefixed Itanium-ABI-style mangled name via <see cref="Mangler"/>,
/// and produce a deterministic <see cref="ManglingTable"/> keyed by
/// <see cref="StableId"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mirrors Pass 4's enumeration.</b> The emittable-function set is exactly
/// the set <c>Pass4Driver.CollectFunctions</c> enumerates (methods,
/// constructors, destructors, operators, conversion operators, property /
/// indexer accessors -- block- and expression-bodied --, local functions,
/// lambdas / anonymous methods) so the Pass-5 <see cref="ManglingTable"/>
/// joins one-to-one to the Pass-4 <see cref="TierTable"/> by
/// <see cref="StableId.Value"/>.
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-MANGLE-DET / Contract Section 2.4).</b>
/// Functions are visited in <see cref="Pass1Result.ParsedFiles"/> ordinal
/// order then document order; the <see cref="Mangler"/> is a pure function;
/// the resulting table is sorted by <see cref="StableId.Value"/> (ordinal).
/// Two runs over the same input produce byte-identical output.
/// </para>
/// </remarks>
public static class Pass5Driver
{
    /// <summary>
    /// Run Pass 5 over <paramref name="unit"/>: mangle every emittable
    /// function and return the per-module table. <paramref name="tierTable"/>
    /// is accepted so the driver can validate the join (every emittable
    /// function it mangles also appears in the tier table) and so callers have
    /// a single Pass-5 entry that takes the Pass-4 output, but the mangle
    /// itself does not depend on the tier verdict.
    /// </summary>
    /// <param name="unit">The Pass-2 normalized unit (wraps the authoritative Pass-1 binding info). Must not be null.</param>
    /// <param name="tierTable">The Pass-4 tier table (its <see cref="StableId"/> set is the join target). Must not be null.</param>
    /// <param name="contractVersionTag">The contract-version short tag (WITHOUT the leading <c>_v</c>). Must not be null / empty / whitespace.</param>
    /// <returns>The deterministic per-module mangling table.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="unit"/> or <paramref name="tierTable"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="contractVersionTag"/> is null / empty / whitespace.</exception>
    public static ManglingTable Run(NormalizedUnit unit, TierTable tierTable, string contractVersionTag)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(tierTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractVersionTag);

        return Run(unit, tierTable, contractVersionTag, diagnostics: null);
    }

    /// <summary>
    /// Run Pass 5, collecting the forward-commit mangle diagnostics
    /// (<c>XIL2CPP179</c> / <c>XIL2CPP123</c>) into
    /// <paramref name="diagnostics"/> when supplied. The driver does not touch
    /// the process-global logger; the caller decides whether to forward.
    /// </summary>
    /// <param name="unit">The Pass-2 normalized unit. Must not be null.</param>
    /// <param name="tierTable">The Pass-4 tier table. Must not be null.</param>
    /// <param name="contractVersionTag">The contract-version short tag (WITHOUT the leading <c>_v</c>). Must not be null / empty / whitespace.</param>
    /// <param name="diagnostics">Optional sink the per-mangle forward-commit diagnostics are appended to (in deterministic visit order).</param>
    /// <returns>The deterministic per-module mangling table.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="unit"/> or <paramref name="tierTable"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="contractVersionTag"/> is null / empty / whitespace.</exception>
    public static ManglingTable Run(
        NormalizedUnit unit,
        TierTable tierTable,
        string contractVersionTag,
        ICollection<DiagnosticRecord>? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(tierTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractVersionTag);

        Pass1Result pass1 = unit.Pass1;
        List<ManglingRecord> records = new();

        // De-dup by StableId so partial methods (two declarations, one symbol)
        // produce exactly one row, matching the tier table's one-per-symbol
        // shape (TierTable de-dups via its constructor sort, but Pass 4 keeps
        // the first; Pass 5 mirrors that by skipping a stable id already seen).
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (IMethodSymbol symbol in EnumerateEmittableFunctions(unit))
        {
            StableId id = StableId.FromSymbol(symbol);
            if (!seen.Add(id.Value))
            {
                continue;
            }

            MangledName mangled = Mangler.MangleMethod(symbol, contractVersionTag);
            if (diagnostics is not null)
            {
                foreach (DiagnosticRecord d in mangled.Diagnostics)
                {
                    diagnostics.Add(d with { Module = pass1.ModuleName });
                }
            }

            records.Add(new ManglingRecord(
                id,
                mangled.CanonicalForm,
                mangled.LinkerSymbol,
                IsStaticMethod: symbol.IsStatic
                    && symbol.MethodKind is not (MethodKind.Constructor or MethodKind.StaticConstructor),
                IsConstructor: symbol.MethodKind == MethodKind.Constructor,
                IsDestructor: symbol.MethodKind == MethodKind.Destructor,
                IsPropertyGetter: symbol.MethodKind == MethodKind.PropertyGet,
                IsPropertySetter: symbol.MethodKind == MethodKind.PropertySet,
                IsOperator: symbol.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion));
        }

        return new ManglingTable(pass1.ModuleName, contractVersionTag, records);
    }

    /// <summary>
    /// Enumerate every emittable function symbol in the unit in canonical file
    /// order then document order. Mirrors <c>Pass4Driver.CollectFunctions</c>
    /// (the SAME emittable-function set Pass 4 classifies), so the Pass-5 table
    /// and the Pass-4 table share their <see cref="StableId"/> key set. Public
    /// so the next-wave emitters + tests can drive the same enumeration.
    /// </summary>
    /// <param name="unit">The Pass-2 normalized unit. Must not be null.</param>
    /// <returns>The emittable function symbols, in deterministic visit order (may contain a symbol more than once for partial methods).</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="unit"/> is null.</exception>
    public static IEnumerable<IMethodSymbol> EnumerateEmittableFunctions(NormalizedUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);

        Pass1Result pass1 = unit.Pass1;
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
                        if (model.GetDeclaredSymbol(method) is IMethodSymbol methodSymbol)
                        {
                            yield return methodSymbol;
                        }
                        break;

                    // Property / indexer / event accessors (get / set / init /
                    // add / remove), block- or expression-bodied.
                    case AccessorDeclarationSyntax accessor:
                        if (model.GetDeclaredSymbol(accessor) is IMethodSymbol accessorSymbol)
                        {
                            yield return accessorSymbol;
                        }
                        break;

                    // Expression-bodied property / indexer (the arrow clause is
                    // the getter body; its declared symbol is the get accessor).
                    case ArrowExpressionClauseSyntax arrow
                        when arrow.Parent is PropertyDeclarationSyntax or IndexerDeclarationSyntax:
                    {
                        if (model.GetDeclaredSymbol(arrow.Parent!) is IPropertySymbol property
                            && property.GetMethod is IMethodSymbol getter)
                        {
                            yield return getter;
                        }
                        break;
                    }

                    // Local functions.
                    case LocalFunctionStatementSyntax local:
                        if (model.GetDeclaredSymbol(local) is IMethodSymbol localSymbol)
                        {
                            yield return localSymbol;
                        }
                        break;

                    // Lambdas + anonymous methods (delegate { }).
                    case AnonymousFunctionExpressionSyntax lambda:
                        if (model.GetSymbolInfo(lambda).Symbol is IMethodSymbol lambdaSymbol)
                        {
                            yield return lambdaSymbol;
                        }
                        break;
                }
            }
        }
    }
}
