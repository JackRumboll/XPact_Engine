// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Frontend.Corpus;

/// <summary>
/// The parse-stage foundation of the <c>X-IL2CPP-*-DET</c> determinism gates
/// (<c>/Documents/XIL2CPP.html</c> Rev 4 Section 9.9 / 11.2): parse + bind the
/// ENTIRE corpus (every Supported and banned / post-MVP fixture) twice
/// through the real Pass-1 path and assert the two runs produce a
/// byte-identical diagnostic stream and a byte-identical syntax-tree dump.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 6.a scope boundary.</b> This is the PARSE-stage determinism
/// foundation only -- it proves the parser + binder are reproducible over a
/// large, diverse corpus on a single run pair. The full cross-machine /
/// cross-arch byte-exact emit determinism (mangling, PDB checksums) is an
/// emit sub-phase. Here "byte-identical" means: identical diagnostic IDs,
/// severities, ordered spans, and invariant-culture messages, plus identical
/// full-tree text + structural dumps.
/// </para>
/// <para>
/// <b>Why a fresh compilation per run.</b> Each run rebuilds the reference
/// set and compilation from scratch (no workspace reuse, Section 3.4), so the
/// determinism asserted is the determinism of the parse + bind pipeline
/// itself, not of a cached object graph.
/// </para>
/// </remarks>
public sealed class DeterminismCorpusTests
{
    private static IReadOnlyList<CorpusFeature> WholeCorpus()
        => SupportedFeatureCorpus.Features
            .Concat(BannedFeatureCorpus.Features)
            .ToList();

    /// <summary>
    /// Produce a canonical, culture-invariant dump for one fixture: its parse
    /// diagnostics, then its full bind diagnostic stream (in compilation
    /// order), then the parsed tree's full text + structural node/token dump.
    /// </summary>
    private static string DumpFixture(CorpusFeature feature)
    {
        CorpusBinder.BindOutcome outcome = CorpusBinder.ParseAndBind(feature.Source);

        StringBuilder sb = new();
        sb.Append("=== FEATURE: ").Append(feature.Name).Append(" ===\n");

        sb.Append("-- parse diagnostics --\n");
        foreach (Diagnostic d in outcome.Parsed.SyntaxDiagnostics)
        {
            AppendDiagnostic(sb, d);
        }

        sb.Append("-- bind diagnostics --\n");
        foreach (Diagnostic d in outcome.Compilation.GetDiagnostics())
        {
            AppendDiagnostic(sb, d);
        }

        sb.Append("-- tree full text --\n");
        sb.Append(outcome.Parsed.Tree.GetText().ToString());
        sb.Append('\n');

        sb.Append("-- tree structure --\n");
        // The full-string round-trips the source; the node-kind walk dumps
        // the structural shape so a determinism break in tree construction
        // (not just text) is also caught.
        foreach (SyntaxNodeOrToken nodeOrToken in
                 outcome.Parsed.Tree.GetRoot().DescendantNodesAndTokens(descendIntoTrivia: false))
        {
            sb.Append(nodeOrToken.Kind().ToString())
                .Append('@')
                .Append(nodeOrToken.Span.Start.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(nodeOrToken.Span.Length.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }

        return sb.ToString();
    }

    private static void AppendDiagnostic(StringBuilder sb, Diagnostic d)
    {
        // Culture-invariant, location-stable rendering. Diagnostic.ToString()
        // can localize the message; GetMessage(InvariantCulture) pins it.
        FileLinePositionSpan span = d.Location.GetLineSpan();
        sb.Append(d.Id)
            .Append('|')
            .Append(d.Severity.ToString())
            .Append('|')
            .Append(span.StartLinePosition.Line.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(span.StartLinePosition.Character.ToString(CultureInfo.InvariantCulture))
            .Append('|')
            .Append(d.GetMessage(CultureInfo.InvariantCulture))
            .Append('\n');
    }

    private static string DumpWholeCorpus()
    {
        StringBuilder sb = new();
        foreach (CorpusFeature feature in WholeCorpus())
        {
            sb.Append(DumpFixture(feature));
        }

        return sb.ToString();
    }

    [Fact]
    public void WholeCorpus_ParsedTwice_ProducesByteIdenticalDump()
    {
        string first = DumpWholeCorpus();
        string second = DumpWholeCorpus();

        // string equality is the byte-identity assertion (both are UTF-16
        // .NET strings built from the same invariant rendering).
        Assert.Equal(first, second);
    }

    [Fact]
    public void EachFixture_ParsedTwice_IsIndividuallyDeterministic()
    {
        // A per-fixture pass localizes any determinism break to one row
        // (the whole-corpus test only tells you SOMETHING drifted).
        foreach (CorpusFeature feature in WholeCorpus())
        {
            string first = DumpFixture(feature);
            string second = DumpFixture(feature);
            Assert.True(
                first == second,
                "Non-deterministic parse/bind dump for feature: " + feature.Name);
        }
    }

    [Fact]
    public void WholeCorpus_IsNonTrivial()
    {
        // Sanity: the corpus actually has content so the determinism check is
        // meaningful (guards against an empty-corpus false green).
        Assert.True(WholeCorpus().Count >= 100, "Corpus unexpectedly small.");
        Assert.NotEqual(0, DumpWholeCorpus().Length);
    }
}
