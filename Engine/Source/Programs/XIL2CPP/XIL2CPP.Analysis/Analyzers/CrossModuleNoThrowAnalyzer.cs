// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using DiagnosticSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Analysis;

/// <summary>
/// The result of looking up the cross-module NoThrow status of one resolved
/// callee, per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.3 (two-pass
/// tier classification protocol, Pass 1 mode). Each kind corresponds to a
/// distinct branch of the Pass-1 cross-module NoThrow lookup.
/// </summary>
public enum CrossModuleNoThrowStatus
{
    /// <summary>
    /// The callee is same-module (declared in this module's source); its tier
    /// is decided by the same-module fixpoint, not by a cross-module lookup.
    /// Not subject to <c>XIL2CPP031</c> / <c>XIL2CPP036</c>.
    /// </summary>
    SameModule,

    /// <summary>
    /// The cross-module callee carries <c>[XFunction(NoThrow = true)]</c> in
    /// its declaring module's (synthetic, Phase 6.b) reflection metadata, so
    /// the call is potentially Tier 2 (Tier-2 eligible).
    /// </summary>
    Tier2Eligible,

    /// <summary>
    /// The cross-module callee resolved + has a reflection entry but is NOT
    /// NoThrow-annotated, so the call forces the caller conservatively to
    /// Tier 1 (<c>XIL2CPP031</c>).
    /// </summary>
    ConservativeTier1,

    /// <summary>
    /// The cross-module callee has NO XHT-emitted reflection entry, so the
    /// NoThrow lookup failed outright (<c>XIL2CPP036</c>).
    /// </summary>
    MissingReflectionEntry,
}

/// <summary>
/// One per-callee NoThrow lookup result recorded for the
/// <see cref="CrossModuleNoThrowTable"/>: the resolved callee's stable
/// display id, the call-site span, and the <see cref="CrossModuleNoThrowStatus"/>
/// the Pass-1 lookup assigned.
/// </summary>
/// <param name="CalleeDisplay">The resolved callee's fully-qualified display string (stable id surrogate).</param>
/// <param name="CallerDisplay">The enclosing caller method's fully-qualified display string.</param>
/// <param name="Status">The cross-module NoThrow lookup outcome.</param>
/// <param name="File">Call-site source file path.</param>
/// <param name="Line">Call-site source line (1-based).</param>
/// <param name="Column">Call-site source column (1-based).</param>
public sealed record CrossModuleNoThrowCalleeResult(
    string CalleeDisplay,
    string CallerDisplay,
    CrossModuleNoThrowStatus Status,
    string File,
    int Line,
    int Column);

/// <summary>
/// One per-caller conservative-tier flag recorded for the
/// <see cref="CrossModuleNoThrowTable"/>: a caller method that the Pass-1
/// lookup forced to conservative Tier 1 because at least one of its
/// cross-module callees was unresolved / not NoThrow-annotated.
/// </summary>
/// <param name="CallerDisplay">The caller method's fully-qualified display string.</param>
/// <param name="ConservativeTier1">True iff the caller was forced conservatively to Tier 1.</param>
/// <param name="ForcingCallees">The cross-module callee display ids that forced the demotion, sorted ordinally.</param>
public sealed record CrossModuleCallerTierFlag(
    string CallerDisplay,
    bool ConservativeTier1,
    IReadOnlyList<string> ForcingCallees);

/// <summary>
/// The singleton Pass-3 emit-metadata table the
/// <see cref="CrossModuleNoThrowAnalyzer"/> produces per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2 / 3.3: the per-callee
/// NoThrow lookup results and the per-caller conservative-tier flags, both in
/// deterministic order. Pass 4 (tier classification) consumes this table.
/// </summary>
/// <param name="CalleeResults">Per-callee NoThrow lookup results, in deterministic walk order.</param>
/// <param name="CallerFlags">Per-caller conservative-tier flags, sorted by caller display (ordinal).</param>
public sealed record CrossModuleNoThrowTable(
    IReadOnlyList<CrossModuleNoThrowCalleeResult> CalleeResults,
    IReadOnlyList<CrossModuleCallerTierFlag> CallerFlags);

/// <summary>
/// WU-25 cross-module NoThrow analyzer per <c>/Documents/XIL2CPP.html</c>
/// Rev 4 Section 3.2 / 3.3: for every method call, resolves the target and
/// classifies its cross-module NoThrow status (Pass-1 lookup mode), then
/// folds the per-caller verdict so Pass 4 can do tier classification.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cross-module model (Phase 6.b).</b> Real cross-module XHT reflection
/// metadata is not yet wired (single module). Per the unit brief the lookup
/// is therefore built from same-module info and degrades gracefully, driven
/// by synthetic NoThrow annotations the source declares:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Cross-module callee.</b> A resolved callee is treated as
///     cross-module when EITHER its containing assembly is a genuine non-BCL
///     metadata module (a real cross-module callee) OR it carries the
///     Phase-6.b synthetic marker attribute <c>[XExternalModule]</c> (which
///     lets a single-module fixture stand in a callee that lives in another
///     module). BCL / system-assembly callees are part of the curated BCL
///     surface, not XPact cross-module callees, so they are NOT subject to
///     the NoThrow lookup (avoids spurious <c>XIL2CPP036</c> on every
///     <c>ToString</c> call).
///   </description></item>
///   <item><description>
///     <b>NoThrow proof.</b> A cross-module callee whose (synthetic)
///     reflection metadata carries <c>[XFunction(NoThrow = true)]</c> is
///     Tier-2 eligible; otherwise the call forces the caller conservatively
///     to Tier 1.
///   </description></item>
///   <item><description>
///     <b>Reflection entry presence.</b> A synthetic cross-module callee may
///     declare <c>[XExternalModule(HasReflectionEntry = false)]</c> to model
///     a callee whose declaring module emitted no XHT reflection entry; that
///     drives the <c>XIL2CPP036</c> lookup-failure path.
///   </description></item>
/// </list>
/// <para>
/// The canonical attribute is <c>XPact.CoreXObject.XFunctionAttribute</c>
/// (doc Section 3.3 / 10.6); the marker is matched by metadata name + an
/// optional namespace fallback so the check works without the curated BCL
/// ref (mirrors <see cref="AnalyzerHelpers.IsXObjectType"/>).
/// </para>
/// <para>
/// <b>Codes owned (Section 12).</b> <c>XIL2CPP030</c> (NoThrow proof failed,
/// enriched with the failing callee chain; Error), <c>XIL2CPP031</c>
/// (conservative Tier-1 demotion due to an unresolved cross-module callee;
/// Warning), <c>XIL2CPP036</c> (NoThrow lookup failed; no XHT reflection
/// entry; Error).
/// </para>
/// <para>
/// <b>Determinism.</b> Methods are visited in
/// <see cref="AnalyzerHelpers.EnumerateMemberDeclarations"/> order; call
/// sites within a method in document order; the per-caller forcing-callee
/// lists and the caller-flag list are sorted ordinally before emit (gate
/// X-IL2CPP-CSPATH-DET).
/// </para>
/// </remarks>
public sealed class CrossModuleNoThrowAnalyzer : ISemanticAnalyzer
{
    /// <summary>Metadata names that identify the synthetic NoThrow attribute.</summary>
    private const string XFunctionAttributeMetadataName = "XFunctionAttribute";

    /// <summary>The unqualified C# spelling of the NoThrow attribute.</summary>
    private const string XFunctionAttributeShortName = "XFunction";

    /// <summary>The canonical containing namespace of the NoThrow attribute (doc Section 3.3).</summary>
    private const string XFunctionAttributeNamespace = "XPact.CoreXObject";

    /// <summary>The named argument carrying the NoThrow proof claim.</summary>
    private const string NoThrowArgumentName = "NoThrow";

    /// <summary>Metadata names that identify the synthetic cross-module marker attribute.</summary>
    private const string XExternalModuleAttributeMetadataName = "XExternalModuleAttribute";

    /// <summary>The unqualified C# spelling of the cross-module marker attribute.</summary>
    private const string XExternalModuleAttributeShortName = "XExternalModule";

    /// <summary>The named argument that toggles whether a synthetic reflection entry exists.</summary>
    private const string HasReflectionEntryArgumentName = "HasReflectionEntry";

    /// <summary>
    /// Stable, fully-qualified display format used as a per-function /
    /// per-callee id surrogate: namespace + containing type + member name +
    /// parameter types. <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/>
    /// alone renders only the member's simple name (it qualifies type names,
    /// not members), so a custom format is needed to disambiguate overloads
    /// and to anchor the caller/callee identity.
    /// </summary>
    private static readonly SymbolDisplayFormat s_stableIdFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.ExpandNullable);

    /// <inheritdoc/>
    public string Name => "CrossModuleNoThrowAnalyzer";

    /// <inheritdoc/>
    public void Analyze(NormalizedUnit unit, Pass3ResultBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(builder);

        Pass1Result pass1 = unit.Pass1;
        IAssemblySymbol sourceAssembly = pass1.Compilation.Assembly;

        List<CrossModuleNoThrowCalleeResult> calleeResults = new();

        // Per-caller forcing-callee accumulation. SortedSet keeps the forcing
        // list deterministic; SortedDictionary keeps the caller iteration
        // order deterministic for the per-caller flag emit.
        SortedDictionary<string, SortedSet<string>> callerForcing =
            new(StringComparer.Ordinal);

        foreach (MemberDeclarationSyntax member in AnalyzerHelpers.EnumerateMemberDeclarations(unit))
        {
            if (member is not BaseMethodDeclarationSyntax)
            {
                continue;
            }

            SemanticModel model = pass1.GetSemanticModel(member.SyntaxTree);
            ISymbol? callerSymbol = model.GetDeclaredSymbol(member);
            if (callerSymbol is null)
            {
                continue;
            }

            string callerDisplay = Display(callerSymbol);

            // Ensure every caller has an entry so its flag is emitted even
            // when no callee forces a demotion.
            if (!callerForcing.ContainsKey(callerDisplay))
            {
                callerForcing[callerDisplay] = new SortedSet<string>(StringComparer.Ordinal);
            }

            // NoThrow-proof obligation: a caller annotated [XFunction(NoThrow = true)]
            // must itself be provably no-throw.
            bool callerClaimsNoThrow = HasNoThrowClaim(callerSymbol);

            foreach (InvocationExpressionSyntax invocation in
                member.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                ISymbol? target = model.GetSymbolInfo(invocation).Symbol;
                if (target is not IMethodSymbol callee)
                {
                    continue;
                }

                CrossModuleNoThrowStatus status = ClassifyCallee(callee, sourceAssembly);
                if (status == CrossModuleNoThrowStatus.SameModule)
                {
                    continue;
                }

                (string file, int line, int column) = SpanOf(invocation);
                string calleeDisplay = Display(callee);

                calleeResults.Add(new CrossModuleNoThrowCalleeResult(
                    calleeDisplay, callerDisplay, status, file, line, column));

                switch (status)
                {
                    case CrossModuleNoThrowStatus.ConservativeTier1:
                        callerForcing[callerDisplay].Add(calleeDisplay);
                        builder.AddDiagnostic(new DiagnosticRecord(
                            DiagnosticSeverity.Warning,
                            DiagnosticCodes.ConservativeTier1UnresolvedCallee,
                            $"Function '{callerDisplay}' conservatively Tier 1 due to unresolved "
                                + $"cross-module callee '{calleeDisplay}' (no NoThrow annotation).",
                            file, line, column, pass1.ModuleName));
                        break;

                    case CrossModuleNoThrowStatus.MissingReflectionEntry:
                        callerForcing[callerDisplay].Add(calleeDisplay);
                        builder.AddDiagnostic(new DiagnosticRecord(
                            DiagnosticSeverity.Error,
                            DiagnosticCodes.NoThrowLookupFailed,
                            $"NoThrow lookup failed; function '{calleeDisplay}' has no "
                                + "XHT-emitted reflection entry.",
                            file, line, column, pass1.ModuleName));
                        break;

                    case CrossModuleNoThrowStatus.Tier2Eligible:
                        // Tier-2 eligible: no demotion, no diagnostic.
                        break;
                }
            }

            // [XFunction(NoThrow = true)] proof obligation on the caller.
            if (callerClaimsNoThrow)
            {
                IReadOnlyList<string> chain = BuildNoThrowFailureChain(
                    member, model, sourceAssembly, callerDisplay);
                if (chain.Count > 0)
                {
                    (string file, int line, int column) = SpanOf(member);
                    builder.AddDiagnostic(new DiagnosticRecord(
                        DiagnosticSeverity.Error,
                        DiagnosticCodes.NoThrowProofFailed,
                        $"[XFunction(NoThrow = true)] proof failed for '{callerDisplay}'. "
                            + "Failing callee chain: " + string.Join(" -> ", chain),
                        file, line, column, pass1.ModuleName));
                }
            }
        }

        // Build the per-caller flag list deterministically.
        List<CrossModuleCallerTierFlag> callerFlags = new(callerForcing.Count);
        foreach (KeyValuePair<string, SortedSet<string>> kv in callerForcing)
        {
            callerFlags.Add(new CrossModuleCallerTierFlag(
                kv.Key,
                kv.Value.Count > 0,
                kv.Value.ToList()));
        }

        builder.SetSingleton(new CrossModuleNoThrowTable(calleeResults, callerFlags));
    }

    /// <summary>
    /// Classify the cross-module NoThrow status of a resolved callee.
    /// </summary>
    private static CrossModuleNoThrowStatus ClassifyCallee(
        IMethodSymbol callee, IAssemblySymbol sourceAssembly)
    {
        bool syntheticCrossModule = HasExternalModuleMarker(callee, out bool hasReflectionEntry);
        bool genuineCrossModule = !syntheticCrossModule
            && IsGenuineCrossModuleMetadataCallee(callee, sourceAssembly);

        if (!syntheticCrossModule && !genuineCrossModule)
        {
            return CrossModuleNoThrowStatus.SameModule;
        }

        if (HasNoThrowClaim(callee))
        {
            return CrossModuleNoThrowStatus.Tier2Eligible;
        }

        // No reflection entry -> lookup fails outright (XIL2CPP036). For a
        // genuine non-BCL metadata callee there is likewise no XHT reflection
        // entry available in Phase 6.b.
        if (syntheticCrossModule && !hasReflectionEntry)
        {
            return CrossModuleNoThrowStatus.MissingReflectionEntry;
        }
        if (genuineCrossModule)
        {
            return CrossModuleNoThrowStatus.MissingReflectionEntry;
        }

        // Resolved cross-module callee, has a reflection entry, but not
        // NoThrow-annotated -> conservative Tier 1 (XIL2CPP031).
        return CrossModuleNoThrowStatus.ConservativeTier1;
    }

    /// <summary>
    /// Return true iff <paramref name="callee"/> is a genuine cross-module
    /// metadata callee subject to the NoThrow lookup: it comes from a
    /// referenced assembly (not the source compilation) AND that assembly is
    /// not a BCL / system assembly (the curated BCL surface is exempt).
    /// </summary>
    private static bool IsGenuineCrossModuleMetadataCallee(
        IMethodSymbol callee, IAssemblySymbol sourceAssembly)
    {
        IAssemblySymbol? calleeAssembly = callee.ContainingAssembly;
        if (calleeAssembly is null
            || SymbolEqualityComparer.Default.Equals(calleeAssembly, sourceAssembly))
        {
            return false;
        }
        return !IsBclAssembly(calleeAssembly);
    }

    /// <summary>
    /// Heuristic BCL / system-assembly classification: the curated BCL
    /// surface and the framework assemblies are exempt from the XPact
    /// cross-module NoThrow lookup. Matches the conventional framework
    /// assembly-name prefixes.
    /// </summary>
    private static bool IsBclAssembly(IAssemblySymbol assembly)
    {
        string name = assembly.Identity.Name;
        return name.StartsWith("System", StringComparison.Ordinal)
            || name.StartsWith("Microsoft", StringComparison.Ordinal)
            || name == "mscorlib"
            || name == "netstandard"
            || name.Equals("System.Private.CoreLib", StringComparison.Ordinal);
    }

    /// <summary>
    /// Return true iff <paramref name="symbol"/> carries the synthetic
    /// cross-module marker <c>[XExternalModule]</c>, also reporting whether a
    /// synthetic reflection entry is declared to exist (default true; the
    /// attribute may set <c>HasReflectionEntry = false</c> to model a missing
    /// XHT reflection entry).
    /// </summary>
    private static bool HasExternalModuleMarker(ISymbol symbol, out bool hasReflectionEntry)
    {
        hasReflectionEntry = true;
        foreach (AttributeData attr in symbol.GetAttributes())
        {
            if (!IsAttribute(attr,
                    XExternalModuleAttributeMetadataName,
                    XExternalModuleAttributeShortName))
            {
                continue;
            }

            foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
            {
                if (named.Key == HasReflectionEntryArgumentName
                    && named.Value.Value is bool b)
                {
                    hasReflectionEntry = b;
                }
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Return true iff <paramref name="symbol"/> carries
    /// <c>[XFunction(NoThrow = true)]</c>.
    /// </summary>
    private static bool HasNoThrowClaim(ISymbol symbol)
    {
        foreach (AttributeData attr in symbol.GetAttributes())
        {
            if (!IsAttribute(attr,
                    XFunctionAttributeMetadataName,
                    XFunctionAttributeShortName))
            {
                continue;
            }

            foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
            {
                if (named.Key == NoThrowArgumentName
                    && named.Value.Value is bool b
                    && b)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Match an attribute by its attribute-class metadata name (e.g.
    /// <c>XFunctionAttribute</c>) or its short C# spelling (e.g.
    /// <c>XFunction</c>), with a containing-namespace fallback so the check
    /// works whether the attribute is the canonical
    /// <c>XPact.CoreXObject</c> one or a locally-declared stand-in.
    /// </summary>
    private static bool IsAttribute(AttributeData attr, string metadataName, string shortName)
    {
        INamedTypeSymbol? cls = attr.AttributeClass;
        if (cls is null)
        {
            return false;
        }

        if (cls.MetadataName == metadataName)
        {
            return true;
        }

        // Short-name match (e.g. a stand-in declared simply as `XFunction`).
        // Constrain to the canonical namespace when the stand-in lives there;
        // otherwise accept any namespace (Phase 6.b fixtures declare the
        // stand-in in their own namespace).
        if (cls.Name == metadataName || cls.Name == shortName)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Build the failing-callee chain for an <c>[XFunction(NoThrow = true)]</c>
    /// proof that fails: the caller's body contains a <c>throw</c> statement /
    /// expression, or it calls a callee that is not provably no-throw. The
    /// chain is the human-readable enrichment XIL2CPP030 carries. Returns an
    /// empty list iff the proof succeeds.
    /// </summary>
    private static IReadOnlyList<string> BuildNoThrowFailureChain(
        SyntaxNode body,
        SemanticModel model,
        IAssemblySymbol sourceAssembly,
        string callerDisplay)
    {
        List<string> chain = new();

        // Direct throw in the body breaks the proof.
        foreach (SyntaxNode node in body.DescendantNodes())
        {
            if (node is ThrowStatementSyntax or ThrowExpressionSyntax)
            {
                FileLinePositionSpan fls = node.GetLocation().GetLineSpan();
                chain.Add(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0} contains throw at {1}:{2}:{3}",
                    callerDisplay,
                    fls.Path,
                    fls.StartLinePosition.Line + 1,
                    fls.StartLinePosition.Character + 1));
                break;
            }
        }

        // A callee that is not provably no-throw breaks the proof. A callee is
        // provably no-throw when it is same-module-and-NoThrow-annotated or a
        // cross-module Tier-2-eligible callee. Cross-module callees that are
        // conservative Tier 1 / missing-entry break the proof.
        SortedSet<string> calleeFailures = new(StringComparer.Ordinal);
        foreach (InvocationExpressionSyntax invocation in
            body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee)
            {
                continue;
            }

            if (HasNoThrowClaim(callee))
            {
                continue;
            }

            CrossModuleNoThrowStatus status = ClassifyCallee(callee, sourceAssembly);
            switch (status)
            {
                case CrossModuleNoThrowStatus.ConservativeTier1:
                    calleeFailures.Add(
                        $"{Display(callee)} (no NoThrow annotation; cross-module)");
                    break;
                case CrossModuleNoThrowStatus.MissingReflectionEntry:
                    calleeFailures.Add(
                        $"{Display(callee)} (no XHT reflection entry; cross-module)");
                    break;
                default:
                    // SameModule / Tier2Eligible: a same-module callee without
                    // NoThrow annotation is left to the same-module fixpoint
                    // (Pass 4), not this proof. Tier2Eligible is provably safe.
                    break;
            }
        }

        chain.AddRange(calleeFailures);
        return chain;
    }

    /// <summary>Fully-qualified display string for a symbol (stable id surrogate).</summary>
    private static string Display(ISymbol symbol)
        => symbol.ToDisplayString(s_stableIdFormat);

    /// <summary>Extract the 1-based (file, line, column) of a node's start.</summary>
    private static (string File, int Line, int Column) SpanOf(SyntaxNode node)
    {
        FileLinePositionSpan fls = node.GetLocation().GetLineSpan();
        return (
            fls.Path,
            fls.StartLinePosition.Line + 1,
            fls.StartLinePosition.Character + 1);
    }
}
