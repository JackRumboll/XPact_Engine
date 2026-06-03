// Copyright Simgenics. All Rights Reserved.

using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Emit.Output;
using Simgenics.XPact.XIL2CPP.Tiering;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit.Output;

/// <summary>
/// Tests for <see cref="EnrichedTierTableSerializer"/>: it adds the top-level
/// <c>contractVersion</c> field + the per-function <c>manglingV1</c> field by
/// joining the Pass-4 tier table to the Pass-5 mangling table on
/// <see cref="StableId"/>, preserves the base tier-table fields, and is
/// deterministic. It must NOT modify the wrapped <see cref="TierTable"/>.
/// </summary>
public sealed class EnrichedTierTableSerializerTests
{
    private static readonly StableId Id = new("Game.Widget.Compute(int):int");

    private static TierTable Tier() => new("MyModule", new[]
    {
        new TierClassification(Id, "Game.Widget.Compute(int):int", FunctionTier.Tier1, "exported"),
    });

    private static ManglingTable Mangle() => new("MyModule", "1ab12cd34", new[]
    {
        new ManglingRecord(
            Id,
            "_v1ab12cd34__Game::Widget::Compute_P_(R Game::Widget, V int)",
            "_v1ab12cd34__Game__Widget__Compute_P_R_Game__Widget_V_int",
            false, false, false, false, false, false),
    });

    [Fact]
    public void Serialize_AddsContractVersionTopLevel()
    {
        string json = EnrichedTierTableSerializer.Serialize(Tier(), Mangle());
        Assert.Contains("\"contractVersion\": \"1ab12cd34\"", json);
    }

    [Fact]
    public void Serialize_AddsManglingV1PerFunction()
    {
        string json = EnrichedTierTableSerializer.Serialize(Tier(), Mangle());
        Assert.Contains(
            "\"manglingV1\": \"_v1ab12cd34__Game__Widget__Compute_P_R_Game__Widget_V_int\"",
            json);
    }

    [Fact]
    public void Serialize_PreservesBaseTierTableFields()
    {
        string json = EnrichedTierTableSerializer.Serialize(Tier(), Mangle());
        Assert.Contains(TierTable.SchemaUri, json);
        Assert.Contains("\"module\": \"MyModule\"", json);
        Assert.Contains("\"stableId\": \"Game.Widget.Compute(int):int\"", json);
        Assert.Contains("\"tier\": \"Tier1\"", json);
        Assert.Contains("\"reason\": \"exported\"", json);
    }

    [Fact]
    public void Serialize_JoinGapEmitsEmptyManglingV1()
    {
        // A tier function with no mangling row joins to an empty manglingV1.
        TierTable tier = new("MyModule", new[]
        {
            new TierClassification(new StableId("Unmangled"), "Unmangled", FunctionTier.Tier2, string.Empty),
        });
        string json = EnrichedTierTableSerializer.Serialize(tier, Mangle());
        Assert.Contains("\"manglingV1\": \"\"", json);
    }

    [Fact]
    public void Serialize_IsDeterministic()
    {
        Assert.Equal(
            EnrichedTierTableSerializer.Serialize(Tier(), Mangle()),
            EnrichedTierTableSerializer.Serialize(Tier(), Mangle()));
    }

    [Fact]
    public void Serialize_DoesNotMutateWrappedTierTable()
    {
        TierTable tier = Tier();
        string baseBefore = tier.Serialize();
        EnrichedTierTableSerializer.Serialize(tier, Mangle());
        string baseAfter = tier.Serialize();
        // The base TierTable serializer is unchanged (no contractVersion /
        // manglingV1 in the base shape; the wrapper never mutated the table).
        Assert.Equal(baseBefore, baseAfter);
        Assert.DoesNotContain("manglingV1", baseAfter);
        Assert.DoesNotContain("contractVersion", baseAfter);
    }
}
