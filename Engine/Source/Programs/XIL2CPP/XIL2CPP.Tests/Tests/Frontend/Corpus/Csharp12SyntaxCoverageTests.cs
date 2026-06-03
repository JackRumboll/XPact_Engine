// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Frontend.Corpus;

/// <summary>
/// The exhaustive Section-15 deliverable for the <em>Supported</em> half of
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 4.1 ("The coverage matrix"):
/// one [Theory] case per Supported C# 12 feature, asserting the Pass-1
/// front-end PARSES it (no lexer / parser diagnostics) and BINDS it (the
/// Roslyn compilation has zero error-severity diagnostics).
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 6.a scope boundary.</b> Phase 6.a is PARSE + BIND only. For
/// Supported features the contract is: parses, binds, and the Roslyn
/// compilation has zero UNEXPECTED errors. No C++ emit is asserted and no
/// XIL2CPP-specific diagnostic is asserted (those are later sub-phases).
/// </para>
/// <para>
/// This suite EXTENDS the WU-4 cross-section in
/// <see cref="Pass1FeatureCoverageTests"/> to the full matrix; the corpus
/// itself lives in <see cref="SupportedFeatureCorpus"/> so it is reused by
/// <see cref="DeterminismCorpusTests"/>.
/// </para>
/// </remarks>
public sealed class Csharp12SyntaxCoverageTests
{
    /// <summary>
    /// Every Supported fixture, projected to xUnit theory data
    /// <c>(name, source)</c>. The name leads so it shows in the test display.
    /// </summary>
    public static IEnumerable<object[]> SupportedFeatures()
        => SupportedFeatureCorpus.Features.Select(c => new object[] { c.Name, c.Source });

    [Theory]
    [MemberData(nameof(SupportedFeatures))]
    public void SupportedFeature_ParsesAndBindsWithZeroErrors(string featureName, string source)
    {
        _ = featureName; // surfaced in the display name for triage.

        CorpusBinder.BindOutcome outcome = CorpusBinder.ParseAndBind(source);

        Assert.True(
            outcome.Parsed.SyntaxDiagnostics.Count == 0,
            "Expected no parse diagnostics for Supported feature but got: "
                + string.Join(" | ", outcome.Parsed.SyntaxDiagnostics.Select(d => d.ToString())));

        IReadOnlyList<Diagnostic> errors = CorpusBinder.BindErrors(outcome.Compilation);
        Assert.True(
            errors.Count == 0,
            "Expected clean bind for Supported feature but got: "
                + string.Join(" | ", errors.Select(e => e.ToString())));
    }

    /// <summary>
    /// Guards the corpus against silent shrinkage: the Section 4.1 Supported
    /// matrix has a fixed, audited row count. If a future edit drops a
    /// fixture this fails, forcing the author to confirm the deletion is
    /// intentional (Prime Directive: never weaken a test to make it pass).
    /// </summary>
    [Fact]
    public void SupportedCorpus_HasExpectedRowCount()
    {
        // Count of Supported rows authored from Section 4.1 (Rev 4). Bump
        // this deliberately when adding a genuinely new Supported feature.
        const int ExpectedSupportedFeatureCount = 103;
        Assert.Equal(ExpectedSupportedFeatureCount, SupportedFeatureCorpus.Features.Count);
    }

    /// <summary>
    /// Every Supported fixture must be uniquely named so a failing theory
    /// case is unambiguous.
    /// </summary>
    [Fact]
    public void SupportedCorpus_FeatureNamesAreUnique()
    {
        List<string> names = SupportedFeatureCorpus.Features.Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }
}
