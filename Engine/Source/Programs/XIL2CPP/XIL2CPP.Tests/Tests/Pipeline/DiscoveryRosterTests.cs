// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Normalization;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Pipeline;

/// <summary>
/// Locks the reflection-discovered Pass-2 normalizer roster (11) and Pass-3
/// analyzer roster (12) by EXACT name set per /Documents/XIL2CPP.html Rev 4
/// Section 3.2. This is the standing guard against a future pass silently not
/// being discovered (e.g. a missing public parameterless constructor, a wrong
/// assembly, or a renamed interface) and documents the roster in one place.
/// </summary>
/// <remarks>
/// If a roster count or name mismatches, this test FAILS rather than being
/// adjusted down: a discovery regression must surface loudly. When a new
/// normalizer / analyzer is legitimately added, the expected roster here is
/// the single place to extend it (and the count assertion documents the new
/// total).
/// </remarks>
public sealed class DiscoveryRosterTests
{
    /// <summary>
    /// The exact set of 11 Pass-2 normalizer <c>Name</c>s expected under full
    /// reflection discovery (Phase 6.b). Listed explicitly so the roster is
    /// self-documenting and a silent non-discovery fails the test.
    /// </summary>
    private static readonly string[] ExpectedNormalizerNames =
    {
        "CollectionExpressionNormalizer",
        "ConditionalAttributeElisionNormalizer",
        "ExpressionBodyNormalizer",
        "LocalFunctionNormalizer",
        "PartialMethodElisionNormalizer",
        "PatternMatchNormalizer",
        "RecordPositionalCtorNormalizer",
        "RecordWithExpressionNormalizer",
        "StringInterpolationNormalizer",
        "TupleNameNormalizer",
        "UsingBlockNormalizer",
    };

    /// <summary>
    /// The exact set of 12 Pass-3 analyzer <c>Name</c>s expected under full
    /// reflection discovery (Phase 6.b). Listed explicitly so the roster is
    /// self-documenting and a silent non-discovery fails the test.
    /// </summary>
    private static readonly string[] ExpectedAnalyzerNames =
    {
        "AsyncStateMachineAnalyzer",
        "BannedFeatureAnalyzer",
        "ContainerAnalyzer",
        "CrossModuleNoThrowAnalyzer",
        "GenericInstantiationAnalyzer",
        "LambdaCaptureAnalyzer",
        "NewExpressionAnalyzer",
        "ReferenceStoreAnalyzer",
        "ReflectionConsumptionAnalyzer",
        "SimPathBannedApiAnalyzer",
        "StackAllocAnalyzer",
        "XhtCorrelationAnalyzer",
    };

    [Fact]
    public void DiscoverNormalizers_ReturnsExactlyTheElevenExpected()
    {
        IReadOnlyList<INormalizer> discovered = Pass2Driver.DiscoverNormalizers();
        List<string> names = discovered.Select(n => n.Name).ToList();

        Assert.Equal(11, names.Count);
        Assert.Equal(
            ExpectedNormalizerNames.OrderBy(s => s, System.StringComparer.Ordinal).ToList(),
            names);
    }

    [Fact]
    public void DiscoverAnalyzers_ReturnsExactlyTheTwelveExpected()
    {
        IReadOnlyList<ISemanticAnalyzer> discovered = Pass3Driver.DiscoverAnalyzers();
        List<string> names = discovered.Select(a => a.Name).ToList();

        Assert.Equal(12, names.Count);
        Assert.Equal(
            ExpectedAnalyzerNames.OrderBy(s => s, System.StringComparer.Ordinal).ToList(),
            names);
    }

    [Fact]
    public void DiscoverNormalizers_NamesAreUnique()
    {
        IReadOnlyList<INormalizer> discovered = Pass2Driver.DiscoverNormalizers();
        List<string> names = discovered.Select(n => n.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(System.StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DiscoverAnalyzers_NamesAreUnique()
    {
        IReadOnlyList<ISemanticAnalyzer> discovered = Pass3Driver.DiscoverAnalyzers();
        List<string> names = discovered.Select(a => a.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(System.StringComparer.Ordinal).Count());
    }
}
