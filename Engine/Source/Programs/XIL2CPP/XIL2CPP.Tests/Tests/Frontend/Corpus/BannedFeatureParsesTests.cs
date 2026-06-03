// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Frontend.Corpus;

/// <summary>
/// The Section-15 deliverable for the <em>BANNED</em> / <em>post-MVP</em>
/// half of <c>/Documents/XIL2CPP.html</c> Rev 4 Section 4.1: one [Theory]
/// case per banned / post-MVP C# 12 feature, asserting the Pass-1 front-end
/// PARSES + BINDS the feature WITHOUT throwing / crashing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 6.a scope boundary.</b> Phase 6.a is PARSE + BIND only. For
/// banned / post-MVP features the front-end's only contract here is survival:
/// it must construct a bound compilation without an exception so the later
/// XIL2CPP-diagnostic pass (Pass 3) can walk the tree and emit the
/// per-feature rejection (XIL2CPP001 / 040 / 044 / 048 / 061 / ...). This
/// suite therefore makes NO XIL2CPP-diagnostic assertion and does NOT require
/// a clean Roslyn bind: some banned features are also rejected by the C#
/// language itself (e.g. <c>dynamic</c> with no runtime-binder reference,
/// <c>params Span&lt;T&gt;</c> under the C# 12 pin), which is expected and
/// irrelevant to "did the front-end survive".
/// </para>
/// </remarks>
public sealed class BannedFeatureParsesTests
{
    /// <summary>
    /// Every banned / post-MVP fixture, projected to xUnit theory data
    /// <c>(name, source)</c>.
    /// </summary>
    public static IEnumerable<object[]> BannedFeatures()
        => BannedFeatureCorpus.Features.Select(c => new object[] { c.Name, c.Source });

    [Theory]
    [MemberData(nameof(BannedFeatures))]
    public void BannedFeature_ParsesAndBindsWithoutCrashing(string featureName, string source)
    {
        _ = featureName; // surfaced in the display name for triage.

        // The contract: ParseAndBind must not throw, and the resulting
        // compilation must be queryable (GetDiagnostics drives the binder).
        // We deliberately do NOT assert the bind is clean -- the XIL2CPP
        // rejection (and any genuine C# rejection) is out of Phase-6.a scope.
        CorpusBinder.BindOutcome outcome = CorpusBinder.ParseAndBind(source);

        Assert.NotNull(outcome.Compilation);

        // Forcing the binder must not throw either: this is the real survival
        // assertion (a binder that crashed on a banned construct would throw
        // here rather than return a diagnostic list).
        IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic> all =
            outcome.Compilation.GetDiagnostics().ToList();
        Assert.NotNull(all);

        // The parsed tree must round-trip to the original text (proof the
        // parser produced a complete tree even for an unsupported construct).
        Assert.Equal(source, outcome.Parsed.Tree.GetText().ToString());
    }

    /// <summary>
    /// Guards the banned / post-MVP corpus against silent shrinkage (same
    /// rationale as the Supported guard).
    /// </summary>
    [Fact]
    public void BannedCorpus_HasExpectedRowCount()
    {
        // Count of banned / post-MVP rows authored from Section 4.1 (Rev 4).
        const int ExpectedBannedFeatureCount = 26;
        Assert.Equal(ExpectedBannedFeatureCount, BannedFeatureCorpus.Features.Count);
    }

    /// <summary>Every banned fixture must be uniquely named.</summary>
    [Fact]
    public void BannedCorpus_FeatureNamesAreUnique()
    {
        List<string> names = BannedFeatureCorpus.Features.Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }
}
