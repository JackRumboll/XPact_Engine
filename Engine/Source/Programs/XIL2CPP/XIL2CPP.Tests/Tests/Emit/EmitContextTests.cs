// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Tiering;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit;

/// <summary>
/// Tests for <see cref="EmitContext"/>: the accessors (<see cref="EmitContext.FindMangling"/>,
/// <see cref="EmitContext.FindTier"/>, <see cref="EmitContext.GetSemanticModel"/>),
/// the ABI-tag content strings, and the sim-path flag.
/// </summary>
public sealed class EmitContextTests
{
    private const string Source = """
        namespace Game
        {
            public class Widget { public int Compute(int x) { return x; } }
        }
        """;

    [Fact]
    public void FindMangling_ReturnsRecordForAClassifiedFunction()
    {
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(Source);
        StableId id = ctx.ManglingTable.Records.First(r => r.Id.Value.Contains("Compute")).Id;

        ManglingRecord? record = ctx.FindMangling(id);
        Assert.NotNull(record);
        Assert.StartsWith("_v1ab12cd34__", record!.Value.LinkerSymbol);
    }

    [Fact]
    public void FindTier_ReturnsTier1ForUnknownId()
    {
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(Source);
        Assert.Equal(FunctionTier.Tier1, ctx.FindTier(new StableId("does.not.exist")));
    }

    [Fact]
    public void FindTier_ReturnsRecordedTierForKnownId()
    {
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(Source);
        TierClassification c = ctx.TierTable.Classifications.First(x => x.Id.Value.Contains("Compute"));
        Assert.Equal(c.Tier, ctx.FindTier(c.Id));
    }

    [Fact]
    public void GetSemanticModel_ReturnsModelForAParsedTree()
    {
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(Source);
        var tree = ctx.Unit.Pass1.ParsedFiles[0].Tree;
        Assert.NotNull(ctx.GetSemanticModel(tree));
    }

    [Fact]
    public void AbiTagContent_MatchesContractPhase1Values()
    {
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(Source);
        Assert.Equal("Span-based v1", ctx.GCRootABI);
        Assert.Equal("Tier1-Shim/Tier2-Direct", ctx.ExceptionABI);
        Assert.Equal("Itanium-LengthPrefixed-v1", ctx.ManglingScheme);
    }

    [Fact]
    public void ModuleNameAndContractTag_AreExposed()
    {
        EmitContext ctx = EmitTestHelpers.BuildEmitContext(Source);
        Assert.Equal("TestModule", ctx.ModuleName);
        Assert.Equal(EmitTestHelpers.ContractVersionTag, ctx.ContractVersionTag);
        Assert.False(ctx.IsSimPath);
    }
}
