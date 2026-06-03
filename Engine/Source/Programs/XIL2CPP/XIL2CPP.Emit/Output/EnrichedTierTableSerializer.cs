// Copyright Simgenics. All Rights Reserved.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Tiering;

namespace Simgenics.XPact.XIL2CPP.Emit.Output;

/// <summary>
/// Serializes the Pass-7 ENRICHED <c>TierTable.partial.&lt;Module&gt;.json</c>:
/// the Pass-4 <see cref="TierTable"/> joined to the Pass-5
/// <see cref="ManglingTable"/> by <see cref="StableId.Value"/>, adding the
/// top-level <c>contractVersion</c> field and the per-function
/// <c>manglingV1</c> field per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 3.3 (the enriched two-pass-protocol schema).
/// </summary>
/// <remarks>
/// <para>
/// <b>Wraps, never mutates.</b> <see cref="Tiering.TierTable"/> is NOT
/// modified (it is a Pass-4 artefact with its own locked serializer); this
/// serializer reads the table's classifications + the mangling table and
/// emits a NEW JSON document that is a superset of the base
/// <see cref="TierTable.Serialize"/> shape. The base fields (<c>$schema</c>,
/// <c>schemaVersion</c>, <c>module</c>, and the per-function <c>stableId</c> /
/// <c>functionDisplay</c> / <c>tier</c> / <c>reason</c>) are preserved
/// byte-compatibly; the enrichment adds <c>contractVersion</c> (top-level,
/// after <c>module</c>) and <c>manglingV1</c> (per function, after
/// <c>stableId</c>) exactly as the Rev 4 Section 3.3 schema example shows.
/// </para>
/// <para>
/// <b>Determinism.</b> Classifications are iterated in the table's canonical
/// (stable-id-ordinal) order; the mangling join is a pure lookup; fields are
/// written in a fixed order; no ambient state participates. Two runs over
/// equal inputs produce byte-identical output (gate X-IL2CPP-MANGLE-DET).
/// </para>
/// </remarks>
public static class EnrichedTierTableSerializer
{
    /// <summary>
    /// Serialize the enriched tier table for <paramref name="tierTable"/>
    /// joined to <paramref name="manglingTable"/> to a deterministic, indented
    /// JSON string (no BOM).
    /// </summary>
    /// <param name="tierTable">The Pass-4 tier table. Must not be null.</param>
    /// <param name="manglingTable">The Pass-5 mangling table (the <c>manglingV1</c> source). Must not be null.</param>
    /// <returns>The enriched JSON text.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="tierTable"/> or <paramref name="manglingTable"/> is null.</exception>
    public static string Serialize(TierTable tierTable, ManglingTable manglingTable)
    {
        ArgumentNullException.ThrowIfNull(tierTable);
        ArgumentNullException.ThrowIfNull(manglingTable);

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            WriteJson(writer, tierTable, manglingTable);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Serialize the enriched tier table to deterministic UTF-8 bytes (no BOM).
    /// </summary>
    /// <param name="tierTable">The Pass-4 tier table. Must not be null.</param>
    /// <param name="manglingTable">The Pass-5 mangling table. Must not be null.</param>
    /// <returns>The enriched UTF-8 JSON bytes (no BOM).</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="tierTable"/> or <paramref name="manglingTable"/> is null.</exception>
    public static byte[] SerializeToUtf8Bytes(TierTable tierTable, ManglingTable manglingTable)
    {
        ArgumentNullException.ThrowIfNull(tierTable);
        ArgumentNullException.ThrowIfNull(manglingTable);

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            WriteJson(writer, tierTable, manglingTable);
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteJson(Utf8JsonWriter writer, TierTable tierTable, ManglingTable manglingTable)
    {
        // Build the StableId.Value -> linker symbol lookup once.
        Dictionary<string, string> manglingByStableId = new(StringComparer.Ordinal);
        foreach (ManglingRecord r in manglingTable.Records)
        {
            // First-wins on a duplicate id (the table is already de-duped, but
            // guard so the join is deterministic regardless).
            manglingByStableId.TryAdd(r.Id.Value, r.LinkerSymbol);
        }

        writer.WriteStartObject();

        // Base table envelope (byte-compatible with TierTable.Serialize) +
        // the contractVersion enrichment (Rev 4 §3.3 schema example).
        writer.WriteString("$schema", TierTable.SchemaUri);
        writer.WriteNumber("schemaVersion", TierTable.CurrentSchemaVersion);
        writer.WriteString("module", tierTable.ModuleName);
        writer.WriteString("contractVersion", manglingTable.ContractVersionTag);

        writer.WriteStartArray("functions");
        foreach (TierClassification c in tierTable.Classifications)
        {
            writer.WriteStartObject();
            writer.WriteString("stableId", c.Id.Value);
            // manglingV1 enrichment: the linker symbol for this function, or an
            // empty string when the mangling table has no row (a function the
            // tier table classified that Pass 5 did not mangle is a join gap;
            // emit the empty marker rather than dropping the field so the schema
            // is uniform).
            writer.WriteString(
                "manglingV1",
                manglingByStableId.TryGetValue(c.Id.Value, out string? symbol) ? symbol : string.Empty);
            writer.WriteString("functionDisplay", c.FunctionDisplay);
            writer.WriteString("tier", TierToString(c.Tier));
            writer.WriteString("reason", c.DemotionReason);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private static string TierToString(FunctionTier tier) => tier switch
    {
        FunctionTier.Tier1 => "Tier1",
        FunctionTier.Tier2 => "Tier2",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown FunctionTier."),
    };
}
