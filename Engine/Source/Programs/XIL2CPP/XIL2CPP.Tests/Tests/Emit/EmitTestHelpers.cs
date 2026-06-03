// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Simgenics.XPact.XIL2CPP.Tiering;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit;

/// <summary>
/// Shared helpers for the Pass-5 / Pass-6 / Pass-7 emit-framework tests:
/// drive synthetic C# sources through the real Pass 1 -&gt; Pass 2 -&gt;
/// Pass 3 -&gt; Pass 4 -&gt; Pass 5 pipeline and build a fully-populated
/// <see cref="EmitContext"/>, so the framework is exercised against real
/// Roslyn trees + semantic models without touching the filesystem.
/// </summary>
internal static class EmitTestHelpers
{
    /// <summary>The contract-version short tag the emit tests mangle under (WITHOUT the leading <c>_v</c>).</summary>
    public const string ContractVersionTag = "1ab12cd34";

    /// <summary>The GC-root ABI envelope tag content (Contract Phase 1 value).</summary>
    public const string GCRootAbi = "Span-based v1";

    /// <summary>The exception ABI envelope tag content (Contract Phase 1 value).</summary>
    public const string ExceptionAbi = "Tier1-Shim/Tier2-Direct";

    /// <summary>The mangling-scheme ABI envelope tag content (Contract Phase 1 value).</summary>
    public const string ManglingSchemeTag = "Itanium-LengthPrefixed-v1";

    /// <summary>
    /// Run the full pipeline over <paramref name="sources"/> and return the
    /// Pass-2 unit, Pass-3 result, Pass-4 tier table, and Pass-5 mangling
    /// table.
    /// </summary>
    public static (NormalizedUnit Unit, Pass3Result Pass3, TierTable TierTable, ManglingTable ManglingTable)
        RunPipeline(params string[] sources)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(isSimPath: false, sources);
        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        Pass3Result pass3 = Pass3Driver.Run(
            unit, new ISemanticAnalyzer[] { new CrossModuleNoThrowAnalyzer() });
        TierTable tierTable = Pass4Driver.Run(unit, pass3);
        ManglingTable manglingTable = Pass5Driver.Run(unit, tierTable, ContractVersionTag);
        return (unit, pass3, tierTable, manglingTable);
    }

    /// <summary>
    /// Build a fully-populated <see cref="EmitContext"/> from
    /// <paramref name="sources"/> (defaults to a single minimal class so the
    /// pipeline has at least one emittable function).
    /// </summary>
    public static EmitContext BuildEmitContext(params string[] sources)
    {
        if (sources.Length == 0)
        {
            sources = new[]
            {
                """
                namespace Game
                {
                    public class Widget
                    {
                        public int Compute(int x) { return x; }
                    }
                }
                """,
            };
        }

        (NormalizedUnit unit, Pass3Result pass3, TierTable tierTable, ManglingTable manglingTable) =
            RunPipeline(sources);

        return new EmitContext(
            unit,
            pass3,
            tierTable,
            manglingTable,
            ContractVersionTag,
            GCRootAbi,
            ExceptionAbi,
            ManglingSchemeTag);
    }
}
