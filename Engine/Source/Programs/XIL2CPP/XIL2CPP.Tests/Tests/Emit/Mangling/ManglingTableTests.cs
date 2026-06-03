// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Tiering;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit.Mangling;

/// <summary>
/// Tests for <see cref="ManglingTable"/>: the JSON sidecar round-trips, the
/// records are sorted by stable id (ordinal), the lookup works, and
/// serialization is deterministic.
/// </summary>
public sealed class ManglingTableTests
{
    private static ManglingRecord Rec(string id, string linker) => new(
        new StableId(id),
        "_v1__" + id,
        linker,
        IsStaticMethod: false,
        IsConstructor: false,
        IsDestructor: false,
        IsPropertyGetter: false,
        IsPropertySetter: false,
        IsOperator: false);

    [Fact]
    public void Constructor_SortsRecordsByStableIdOrdinal()
    {
        ManglingTable table = new("M", "1ab", new[]
        {
            Rec("Zebra", "z"),
            Rec("Apple", "a"),
            Rec("Mango", "m"),
        });

        Assert.Equal(new[] { "Apple", "Mango", "Zebra" }, table.Records.Select(r => r.Id.Value));
    }

    [Fact]
    public void RoundTrip_ParseOfSerializeIsValueEqual()
    {
        ManglingTable original = new("MyModule", "1ab12cd34", new[]
        {
            Rec("A", "linkA") with { IsConstructor = true },
            Rec("B", "linkB") with { IsPropertyGetter = true },
        });

        ManglingTable parsed = ManglingTable.Parse(original.Serialize());

        Assert.Equal(original.ModuleName, parsed.ModuleName);
        Assert.Equal(original.ContractVersionTag, parsed.ContractVersionTag);
        Assert.Equal(original.Records, parsed.Records);
    }

    [Fact]
    public void Serialize_IsDeterministic()
    {
        ManglingTable a = new("M", "1ab", new[] { Rec("A", "a"), Rec("B", "b") });
        ManglingTable b = new("M", "1ab", new[] { Rec("B", "b"), Rec("A", "a") });
        Assert.Equal(a.Serialize(), b.Serialize());
    }

    [Fact]
    public void Find_ReturnsRecordForKnownIdAndNullOtherwise()
    {
        ManglingTable table = new("M", "1ab", new[] { Rec("A", "linkA") });
        Assert.Equal("linkA", table.Find(new StableId("A"))!.Value.LinkerSymbol);
        Assert.Null(table.Find(new StableId("Missing")));
    }

    [Fact]
    public void Parse_MalformedJson_Throws()
    {
        Assert.Throws<FormatException>(() => ManglingTable.Parse("{ not json"));
    }

    [Fact]
    public void Serialize_CarriesSchemaEnvelope()
    {
        ManglingTable table = new("M", "1ab", new List<ManglingRecord>());
        string json = table.Serialize();
        Assert.Contains(ManglingTable.SchemaUri, json);
        Assert.Contains("\"contractVersion\": \"1ab\"", json);
    }
}
