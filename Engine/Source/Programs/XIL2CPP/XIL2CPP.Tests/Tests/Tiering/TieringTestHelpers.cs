// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Simgenics.XPact.XIL2CPP.Tiering;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Tiering;

/// <summary>
/// Shared helpers for the Pass-4 (tier classification) tests: drive a
/// synthetic C# source corpus through the real Pass 1 -&gt; Pass 2 -&gt;
/// Pass 3 -&gt; Pass 4 pipeline and surface the resulting
/// <see cref="TierTable"/>, so the tier verdicts + the JSON round-trip are
/// exercised against real Roslyn trees + semantic models.
/// </summary>
internal static class TieringTestHelpers
{
    /// <summary>
    /// Locally-declared stand-in attributes the synthetic fixtures use. They
    /// match the canonical <c>XPact.CoreXObject</c> attributes by simple name
    /// (the <see cref="CrossModuleNoThrowAnalyzer"/> + <see cref="Pass4Driver"/>
    /// match by metadata / short name with a namespace fallback), so the
    /// curated BCL is not needed:
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>[XFunction(NoThrow = ..., CanThrow = ...)]</c> -- the NoThrow
    ///     proof claim (Pass 3) + the CanThrow Tier-1 marker (Pass 4 clause b).
    ///   </description></item>
    ///   <item><description>
    ///     <c>[CanThrow]</c> -- the standalone Tier-1 marker (Pass 4 clause b).
    ///   </description></item>
    ///   <item><description>
    ///     <c>[XExternalModule(HasReflectionEntry = ...)]</c> -- the Phase-6.b
    ///     synthetic cross-module marker (lets a single-module fixture stand in
    ///     a callee that lives in another module).
    ///   </description></item>
    /// </list>
    /// </summary>
    public const string AttributeShims = """
        namespace XPact.CoreXObject
        {
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public sealed class XFunctionAttribute : System.Attribute
            {
                public bool NoThrow { get; set; }
                public bool CanThrow { get; set; }
            }

            [System.AttributeUsage(System.AttributeTargets.Method)]
            public sealed class CanThrowAttribute : System.Attribute
            {
            }

            [System.AttributeUsage(System.AttributeTargets.Method)]
            public sealed class XExternalModuleAttribute : System.Attribute
            {
                public bool HasReflectionEntry { get; set; } = true;
            }
        }
        """;

    /// <summary>
    /// Run the full Pass 1 -&gt; Pass 2 -&gt; Pass 3 -&gt; Pass 4 pipeline over
    /// the supplied sources (the attribute shims are prepended automatically),
    /// using the real <see cref="CrossModuleNoThrowAnalyzer"/> for Pass 3's
    /// cross-module NoThrow table, and return the Pass-4 tier table.
    /// </summary>
    /// <param name="sources">The synthetic C# source strings (without the attribute shims).</param>
    /// <returns>The Pass-4 tier table.</returns>
    public static TierTable Classify(params string[] sources)
    {
        (TierTable table, _) = ClassifyWithPass3(sources);
        return table;
    }

    /// <summary>
    /// Like <see cref="Classify"/> but also returns the Pass-3 result so a
    /// test can assert the cross-module table the classification consumed.
    /// </summary>
    public static (TierTable Table, Pass3Result Pass3) ClassifyWithPass3(params string[] sources)
    {
        string[] all = new string[sources.Length + 1];
        all[0] = AttributeShims;
        sources.CopyTo(all, 1);

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(isSimPath: false, all);
        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        Pass3Result pass3 = Pass3Driver.Run(
            unit, new ISemanticAnalyzer[] { new CrossModuleNoThrowAnalyzer() });
        TierTable table = Pass4Driver.Run(unit, pass3);
        return (table, pass3);
    }

    /// <summary>
    /// Find the single classification whose <see cref="StableId.Value"/>
    /// contains <paramref name="displaySubstring"/>. Throws if zero or more
    /// than one matches (an unambiguous fixture is the test's responsibility).
    /// </summary>
    public static TierClassification ById(TierTable table, string displaySubstring)
        => table.Classifications.Single(c => c.Id.Value.Contains(displaySubstring));

    /// <summary>
    /// True iff some classification's stable id contains
    /// <paramref name="displaySubstring"/>.
    /// </summary>
    public static bool Has(TierTable table, string displaySubstring)
        => table.Classifications.Any(c => c.Id.Value.Contains(displaySubstring));
}
