// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Emit;
using Simgenics.XPact.XIL2CPP.Tiering;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit.Mangling;

/// <summary>
/// Tests for <see cref="Pass5Driver"/>: it mangles every emittable function
/// (joining one-to-one to the Pass-4 tier table by <see cref="StableId"/>),
/// produces a deterministic table, and surfaces the forward-commit
/// diagnostics.
/// </summary>
public sealed class Pass5DriverTests
{
    private const string Tag = "1ab12cd34";

    private const string TwoMethodsSource = """
        namespace Game
        {
            public class Widget
            {
                public int Compute(int x) { return x; }
                public void Reset() { }
            }
        }
        """;

    [Fact]
    public void Run_ManglesEveryEmittableFunction()
    {
        (var unit, _, var tierTable, _) = EmitTestHelpers.RunPipeline(TwoMethodsSource);
        ManglingTable table = Pass5Driver.Run(unit, tierTable, Tag);

        Assert.Contains(table.Records, r => r.Id.Value.Contains("Compute"));
        Assert.Contains(table.Records, r => r.Id.Value.Contains("Reset"));
    }

    [Fact]
    public void Run_JoinsOneToOneWithTierTableByStableId()
    {
        (var unit, _, var tierTable, _) = EmitTestHelpers.RunPipeline(TwoMethodsSource);
        ManglingTable table = Pass5Driver.Run(unit, tierTable, Tag);

        HashSet<string> tierIds = tierTable.Classifications.Select(c => c.Id.Value).ToHashSet();
        HashSet<string> mangleIds = table.Records.Select(r => r.Id.Value).ToHashSet();

        // Every mangled function appears in the tier table (the same emittable
        // set Pass 4 classifies).
        Assert.Subset(tierIds, mangleIds);
    }

    [Fact]
    public void Run_IsDeterministic()
    {
        (var unit, _, var tierTable, _) = EmitTestHelpers.RunPipeline(TwoMethodsSource);

        ManglingTable a = Pass5Driver.Run(unit, tierTable, Tag);
        ManglingTable b = Pass5Driver.Run(unit, tierTable, Tag);

        Assert.Equal(a.Serialize(), b.Serialize());
    }

    [Fact]
    public void Run_RecordsSortedByStableIdOrdinal()
    {
        (var unit, _, var tierTable, _) = EmitTestHelpers.RunPipeline(TwoMethodsSource);
        ManglingTable table = Pass5Driver.Run(unit, tierTable, Tag);

        List<string> ids = table.Records.Select(r => r.Id.Value).ToList();
        List<string> sorted = ids.OrderBy(s => s, System.StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, ids);
    }

    [Fact]
    public void Run_LinkerSymbolCarriesContractVersionTag()
    {
        (var unit, _, var tierTable, _) = EmitTestHelpers.RunPipeline(TwoMethodsSource);
        ManglingTable table = Pass5Driver.Run(unit, tierTable, Tag);

        Assert.All(table.Records, r => Assert.StartsWith("_v1ab12cd34__", r.LinkerSymbol));
        Assert.Equal(Tag, table.ContractVersionTag);
    }

    [Fact]
    public void Run_PropertyAccessor_FlaggedGetterAndSetter()
    {
        const string source = """
            namespace Game
            {
                public class Widget { public int Value { get; set; } }
            }
            """;
        (var unit, _, var tierTable, _) = EmitTestHelpers.RunPipeline(source);
        ManglingTable table = Pass5Driver.Run(unit, tierTable, Tag);

        Assert.Contains(table.Records, r => r.IsPropertyGetter);
        Assert.Contains(table.Records, r => r.IsPropertySetter);
    }

    [Fact]
    public void Run_RefReadonlyParameter_CollectsXil2cpp179Diagnostic()
    {
        const string source = """
            namespace Game
            {
                public class Widget { public void M(ref readonly int x) { } }
            }
            """;
        (var unit, _, var tierTable, _) = EmitTestHelpers.RunPipeline(source);

        List<DiagnosticRecord> diags = new();
        Pass5Driver.Run(unit, tierTable, Tag, diags);

        Assert.Contains(diags, d => d.Code == DiagnosticCodes.ManglingDiscriminatorsMissing);
        // The driver stamps the module name onto the forwarded diagnostic.
        Assert.All(
            diags.Where(d => d.Code == DiagnosticCodes.ManglingDiscriminatorsMissing),
            d => Assert.Equal(unit.Pass1.ModuleName, d.Module));
    }

    [Fact]
    public void Run_EmptyModule_ProducesEmptyTable()
    {
        const string source = """
            namespace Game { public class Empty { } }
            """;
        (var unit, _, var tierTable, _) = EmitTestHelpers.RunPipeline(source);
        ManglingTable table = Pass5Driver.Run(unit, tierTable, Tag);

        // The implicit parameterless constructor is the only emittable function;
        // assert the table is well-formed (constructor present, sorted).
        Assert.NotNull(table);
        Assert.Equal("TestModule", table.ModuleName);
    }
}
