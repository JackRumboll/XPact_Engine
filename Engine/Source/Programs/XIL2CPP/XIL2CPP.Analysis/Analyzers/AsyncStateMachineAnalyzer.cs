// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;

namespace Simgenics.XPact.XIL2CPP.Analysis;

/// <summary>
/// One <c>XObject*</c> local / parameter that an async method's emitted
/// state machine must capture into its <c>XGCRootSpan</c> because it is live
/// across an <c>await</c> per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.9
/// ("async ... emit as state-machine struct with <c>XGCRootSpan</c> over
/// captured <c>XObject*</c>"). A variable is "live across an await" when its
/// value -- set on or before an <c>await</c> point -- still flows into the
/// region after that <c>await</c>, so the suspended-then-resumed state machine
/// holds the reference across a possible GC cycle and the non-moving collector
/// must see it as a root.
/// </summary>
/// <param name="VariableDisplay">
/// The captured symbol's display string (e.g. <c>"obj"</c>) -- a stable,
/// human-readable identity for the local / parameter.
/// </param>
/// <param name="VariableKind">
/// Whether the captured symbol is a method <see cref="SymbolKind.Parameter"/>
/// or a <see cref="SymbolKind.Local"/>.
/// </param>
/// <param name="TypeDisplay">
/// The captured symbol's type, fully qualified (the <c>XObject</c>-derived
/// reference type the C++ emit lowers to an <c>XObject*</c> root slot).
/// </param>
public sealed record AsyncCapturedRoot(
    string VariableDisplay,
    SymbolKind VariableKind,
    string TypeDisplay);

/// <summary>
/// The Pass-3 record of one async method (or async lambda / async local
/// function) per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.9: the
/// method's identity, its declaration anchor, and the deterministically-sorted
/// set of <c>XObject*</c> locals / parameters that are live across an
/// <c>await</c> and therefore become the emitted state machine's
/// <c>XGCRootSpan</c> captures. Recorded for EVERY async method (non-sim-path
/// async is in the MVP per Section 5.9).
/// </summary>
/// <param name="MethodDisplay">
/// The owning async method's fully-qualified display string, or a synthesized
/// label for an async lambda (anonymous functions have no source name).
/// </param>
/// <param name="MethodMetadataName">
/// The owning method's metadata name (e.g. <c>"RunAsync"</c>); empty for an
/// anonymous async lambda.
/// </param>
/// <param name="Span">The 1-based source anchor of the async declaration.</param>
/// <param name="ReturnsTaskLike">
/// True iff the async method's declared return type is a Task-like /
/// async-stream type (<c>Task</c>, <c>Task&lt;T&gt;</c>, <c>ValueTask</c>,
/// <c>ValueTask&lt;T&gt;</c>, or <c>IAsyncEnumerable&lt;T&gt;</c>); false for
/// an <c>async void</c> handler.
/// </param>
/// <param name="IsAsyncIterator">
/// True iff the method is an async iterator (an <c>async</c> method that
/// contains <c>yield</c>), which the C# compiler shapes as an
/// <c>IAsyncEnumerable&lt;T&gt;</c> state machine.
/// </param>
/// <param name="CapturedRoots">
/// The <c>XObject*</c> locals / parameters live across an <c>await</c>, sorted
/// deterministically (by variable display string, then kind, then type). These
/// are the state machine's <c>XGCRootSpan</c> captures. Possibly empty (an
/// async method that holds no <c>XObject*</c> across a suspension point still
/// gets a state machine, just an empty root span).
/// </param>
public sealed record AsyncSite(
    string MethodDisplay,
    string MethodMetadataName,
    SourceSpan Span,
    bool ReturnsTaskLike,
    bool IsAsyncIterator,
    IReadOnlyList<AsyncCapturedRoot> CapturedRoots);

/// <summary>
/// Pass-3 analyzer for async methods per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 5.9. Records every async method (ordinary method, async local
/// function, or async lambda) as an <see cref="AsyncSite"/> -- the
/// state-machine view: which <c>XObject*</c> locals / parameters live across an
/// <c>await</c> and therefore become the emitted state machine's
/// <c>XGCRootSpan</c> captures.
/// </summary>
/// <remarks>
/// <para>
/// <b>Recording only -- no diagnostics.</b> This analyzer is a pure
/// emit-metadata pass: it records an <see cref="AsyncSite"/> for every async
/// method regardless of the module's sim-path flag (non-sim-path async is in
/// the MVP per Section 5.9). The sim-path async/await ban
/// (<c>XIL2CPP044</c>) and the sim-path Task-surface ban (<c>XIL2CPP048</c>)
/// are owned SOLELY by <see cref="SimPathBannedApiAnalyzer"/> -- the single
/// owner of every sim-path banned-API code (Section 7.4 / 7.5). Keeping the
/// ban in one analyzer avoids double-emission when both analyzers run under
/// full reflection discovery.
/// </para>
/// <para>
/// <b>Determinism.</b> Async declarations are visited in source-declaration
/// (span) order across the parsed files in their canonical ordinal order; the
/// per-site capture set is sorted by a stable key (variable display string,
/// then kind, then type) so two runs over identical input record an identical
/// set (gates X-IL2CPP-MANGLE-DET / X-IL2CPP-CSPATH-DET, Section 9.9). No
/// ambient state / DateTime / Random.
/// </para>
/// </remarks>
public sealed class AsyncStateMachineAnalyzer : ISemanticAnalyzer
{
    /// <inheritdoc />
    public string Name => "AsyncStateMachineAnalyzer";

    /// <summary>
    /// Display format for the recorded method identity: fully-qualified
    /// namespace + containing types, the member name, and the parameter-type
    /// list (e.g. <c>"global::M.A.RunAsync()"</c>). The built-in
    /// <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/> is a TYPE format
    /// that drops the member-name qualification for a method symbol, so we
    /// compose an explicit format that keeps it.
    /// </summary>
    private static readonly SymbolDisplayFormat MethodDisplayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType);

    /// <inheritdoc />
    public void Analyze(NormalizedUnit unit, Pass3ResultBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(builder);

        Pass1Result pass1 = unit.Pass1;

        // Visit files in their canonical ordinal order, then async-method
        // declarations in document (span) order within each file.
        // DescendantNodesAndSelf yields nodes in span order, so the visit
        // order is deterministic.
        foreach (ModuleParser.ParsedFile parsed in pass1.ParsedFiles)
        {
            SemanticModel model = pass1.GetSemanticModel(parsed.Tree);
            SyntaxNode root = parsed.Tree.GetRoot();

            foreach (SyntaxNode node in root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case MethodDeclarationSyntax method when HasAsyncModifier(method.Modifiers):
                        RecordAsyncBody(
                            model, builder,
                            declaration: method,
                            body: (SyntaxNode?)method.Body ?? method.ExpressionBody,
                            symbol: model.GetDeclaredSymbol(method));
                        break;

                    case LocalFunctionStatementSyntax local when HasAsyncModifier(local.Modifiers):
                        RecordAsyncBody(
                            model, builder,
                            declaration: local,
                            body: (SyntaxNode?)local.Body ?? local.ExpressionBody,
                            symbol: model.GetDeclaredSymbol(local) as IMethodSymbol);
                        break;

                    case AnonymousFunctionExpressionSyntax lambda
                        when lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword):
                        RecordAsyncBody(
                            model, builder,
                            declaration: lambda,
                            body: lambda.Body,
                            symbol: model.GetSymbolInfo(lambda).Symbol as IMethodSymbol);
                        break;
                }
            }
        }
    }

    private static bool HasAsyncModifier(SyntaxTokenList modifiers)
        => modifiers.Any(SyntaxKind.AsyncKeyword);

    /// <summary>
    /// Record one async method as an <see cref="AsyncSite"/> (the
    /// state-machine view). No diagnostics are emitted here; the sim-path ban
    /// is owned by <see cref="SimPathBannedApiAnalyzer"/>.
    /// </summary>
    private static void RecordAsyncBody(
        SemanticModel model,
        Pass3ResultBuilder builder,
        SyntaxNode declaration,
        SyntaxNode? body,
        IMethodSymbol? symbol)
    {
        SourceSpan span = SpanOf(declaration);

        bool isAsyncIterator = body is not null && ContainsYield(body);
        bool returnsTaskLike = symbol is not null
            ? IsTaskLikeReturn(symbol.ReturnType)
            : isAsyncIterator; // async iterators are IAsyncEnumerable-shaped.

        IReadOnlyList<AsyncCapturedRoot> captures = ComputeCrossAwaitRoots(model, body);

        string methodDisplay = symbol is not null
            ? symbol.ToDisplayString(MethodDisplayFormat)
            : SynthesizeLambdaLabel(span);
        string metadataName = symbol?.MetadataName ?? string.Empty;

        builder.Add(new AsyncSite(
            methodDisplay,
            metadataName,
            span,
            returnsTaskLike,
            isAsyncIterator,
            captures));
    }

    /// <summary>
    /// Compute the <c>XObject*</c> locals / parameters that are live across an
    /// <c>await</c> in <paramref name="body"/> -- the state machine's
    /// <c>XGCRootSpan</c> captures -- from the authoritative semantic model's
    /// dataflow.
    /// </summary>
    /// <remarks>
    /// For each <c>await</c> expression we look at the region of statements
    /// that lexically follow the statement containing the <c>await</c> within
    /// the same block. A variable is live across that suspension point when its
    /// value flows INTO the post-await region (<c>DataFlowsIn</c>): it was
    /// assigned on / before the await and is still read afterwards, so the
    /// resumed state machine holds the reference. We union the captures over
    /// every await point and keep only <c>XObject</c>-derived locals /
    /// parameters (the references the non-moving GC must root).
    /// </remarks>
    private static IReadOnlyList<AsyncCapturedRoot> ComputeCrossAwaitRoots(
        SemanticModel model,
        SyntaxNode? body)
    {
        if (body is null)
        {
            return Array.Empty<AsyncCapturedRoot>();
        }

        Dictionary<ISymbol, AsyncCapturedRoot> captured = new(SymbolEqualityComparer.Default);

        foreach (AwaitExpressionSyntax await in body.DescendantNodes().OfType<AwaitExpressionSyntax>())
        {
            StatementSyntax? awaitStatement = await.FirstAncestorOrSelf<StatementSyntax>();
            if (awaitStatement is null
                || awaitStatement.Parent is not BlockSyntax block)
            {
                continue;
            }

            int idx = block.Statements.IndexOf(awaitStatement);
            if (idx < 0 || idx + 1 >= block.Statements.Count)
            {
                continue;
            }

            StatementSyntax first = block.Statements[idx + 1];
            StatementSyntax last = block.Statements[block.Statements.Count - 1];

            DataFlowAnalysis? flow = model.AnalyzeDataFlow(first, last);
            if (flow is not { Succeeded: true })
            {
                continue;
            }

            foreach (ISymbol symbol in flow.DataFlowsIn)
            {
                if (captured.ContainsKey(symbol))
                {
                    continue;
                }
                if (TryMakeRoot(symbol, out AsyncCapturedRoot root))
                {
                    captured.Add(symbol, root);
                }
            }
        }

        if (captured.Count == 0)
        {
            return Array.Empty<AsyncCapturedRoot>();
        }

        // Deterministic order: variable display string (ordinal), then kind,
        // then type display string.
        return captured.Values
            .OrderBy(r => r.VariableDisplay, StringComparer.Ordinal)
            .ThenBy(r => (int)r.VariableKind)
            .ThenBy(r => r.TypeDisplay, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Make an <see cref="AsyncCapturedRoot"/> for <paramref name="symbol"/>
    /// iff it is an <c>XObject</c>-derived local or parameter (the references
    /// the state machine's <c>XGCRootSpan</c> must root). Returns false for
    /// non-XObject variables and for non-local / non-parameter symbols.
    /// </summary>
    private static bool TryMakeRoot(ISymbol symbol, out AsyncCapturedRoot root)
    {
        ITypeSymbol? type = symbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
            _ => null,
        };

        if (type is INamedTypeSymbol named
            && (AnalyzerHelpers.IsXObjectType(named) || AnalyzerHelpers.IsXObjectDerived(named)))
        {
            root = new AsyncCapturedRoot(
                symbol.Name,
                symbol.Kind,
                type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            return true;
        }

        root = default!;
        return false;
    }

    /// <summary>
    /// True iff <paramref name="returnType"/> is a Task-like / async-stream
    /// type the C# compiler shapes a state machine around:
    /// <c>System.Threading.Tasks.Task</c>, <c>Task&lt;T&gt;</c>,
    /// <c>ValueTask</c>, <c>ValueTask&lt;T&gt;</c>, or
    /// <c>System.Collections.Generic.IAsyncEnumerable&lt;T&gt;</c>. An
    /// <c>async void</c> handler returns <c>System.Void</c> and is NOT
    /// Task-like.
    /// </summary>
    private static bool IsTaskLikeReturn(ITypeSymbol? returnType)
    {
        if (returnType is not INamedTypeSymbol named)
        {
            return false;
        }

        string name = named.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return name is "global::System.Threading.Tasks.Task"
            or "global::System.Threading.Tasks.Task<TResult>"
            or "global::System.Threading.Tasks.ValueTask"
            or "global::System.Threading.Tasks.ValueTask<TResult>"
            or "global::System.Collections.Generic.IAsyncEnumerable<T>";
    }

    /// <summary>
    /// True iff <paramref name="body"/> contains a <c>yield return</c> /
    /// <c>yield break</c> -- marking an async iterator
    /// (<c>IAsyncEnumerable&lt;T&gt;</c> state machine). Nested lambdas /
    /// local functions are excluded (their yields belong to a different state
    /// machine).
    /// </summary>
    private static bool ContainsYield(SyntaxNode body)
    {
        foreach (SyntaxNode node in body.DescendantNodes(descendIntoChildren: n =>
                     n is not AnonymousFunctionExpressionSyntax
                     && n is not LocalFunctionStatementSyntax))
        {
            if (node is YieldStatementSyntax)
            {
                return true;
            }
        }
        return false;
    }

    private static string SynthesizeLambdaLabel(SourceSpan span)
        => $"<async-lambda>@{span.File}:{span.StartLine}:{span.StartColumn}";

    /// <summary>
    /// Build a 1-based <see cref="SourceSpan"/> from a syntax node's start
    /// location (Roslyn reports 0-based line / character positions).
    /// </summary>
    private static SourceSpan SpanOf(SyntaxNode node)
    {
        FileLinePositionSpan lp = node.GetLocation().GetLineSpan();
        LinePosition start = lp.StartLinePosition;
        LinePosition end = lp.EndLinePosition;
        return new SourceSpan(
            lp.Path,
            start.Line + 1,
            start.Character + 1,
            end.Line + 1,
            end.Character + 1,
            validate: true);
    }
}
