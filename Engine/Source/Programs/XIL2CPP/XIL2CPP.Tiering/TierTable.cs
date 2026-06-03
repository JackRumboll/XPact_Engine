// Copyright Simgenics. All Rights Reserved.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Simgenics.XPact.XIL2CPP.Tiering;

/// <summary>
/// The per-module product of XIL2CPP Pass 4 (tier classification), per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2 / 3.3: the module name
/// and the deterministically-sorted per-function
/// <see cref="TierClassification"/> list, with a byte-deterministic JSON
/// serializer (<see cref="Serialize"/>) and a round-trippable parser
/// (<see cref="Parse"/>). Emitted as
/// <c>TierTable.partial.&lt;Module&gt;.json</c> (Pass 1 of the two-pass
/// protocol; Section 3.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Determinism.</b> <see cref="Classifications"/> is always sorted by
/// <see cref="StableId.Value"/> (ordinal); <see cref="Serialize"/> writes the
/// table fields in a fixed order, formats every value through the invariant
/// culture, and does not emit any ambient state (no timestamp / machine
/// identity), so two serializations of equal tables produce byte-identical
/// output (gate X-IL2CPP-CSPATH-DET). <see cref="Parse"/> reverses
/// <see cref="Serialize"/>: <c>Parse(Serialize(t))</c> equals <c>t</c>.
/// </para>
/// <para>
/// <b>Schema.</b> The emitted JSON carries the table-level
/// <c>$schema</c> + <c>schemaVersion</c> + <c>module</c> envelope the doc
/// sketches (Section 3.3) and a <c>functions</c> array of the minimal
/// Phase-6.c per-function shape: <c>stableId</c>, <c>functionDisplay</c>,
/// <c>tier</c> (<c>"Tier1"</c> / <c>"Tier2"</c>), and <c>reason</c>. The
/// richer per-function fields the doc sketches
/// (<c>exported</c> / <c>throwsInBody</c> / <c>calleesUnverified</c> /
/// <c>manglingV1</c>) are a later-pass enrichment.
/// </para>
/// </remarks>
public sealed class TierTable
{
    /// <summary>The <c>$schema</c> URI the emitted JSON carries (Section 3.3).</summary>
    public const string SchemaUri = "https://xpact.dev/schemas/TierTable.partial.v1.json";

    /// <summary>The schema version integer the emitted JSON carries.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Construct a tier table for <paramref name="moduleName"/> over
    /// <paramref name="classifications"/>. The classifications are sorted by
    /// <see cref="StableId.Value"/> (ordinal) and snapshotted, so the stored
    /// list is always in canonical order regardless of the caller's order.
    /// </summary>
    /// <param name="moduleName">The module the table classifies. Must not be null / empty / whitespace.</param>
    /// <param name="classifications">The per-function verdicts (any order). Must not be null.</param>
    /// <exception cref="ArgumentException">If <paramref name="moduleName"/> is null / empty / whitespace.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="classifications"/> is null.</exception>
    public TierTable(string moduleName, IEnumerable<TierClassification> classifications)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentNullException.ThrowIfNull(classifications);

        ModuleName = moduleName;
        Classifications = classifications
            .OrderBy(c => c.Id.Value, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The module the table classifies.</summary>
    public string ModuleName { get; }

    /// <summary>
    /// The per-function tier verdicts, sorted by <see cref="StableId.Value"/>
    /// (ordinal). Never null.
    /// </summary>
    public IReadOnlyList<TierClassification> Classifications { get; }

    /// <summary>
    /// Serialize the table to a deterministic, indented JSON string. Two
    /// serializations of equal tables produce byte-identical output.
    /// </summary>
    /// <returns>The JSON text (no BOM; invariant formatting; fixed field order).</returns>
    public string Serialize()
    {
        ArrayBufferWriter<byte> buffer = new();
        // Indented output keeps the file human-diffable; the indentation is a
        // fixed two-space convention so it does not vary across machines.
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions
        {
            Indented = true,
            // The writer escapes only the characters JSON requires; the
            // default encoder is deterministic. Strings here are symbol
            // displays (ASCII-dominant), but the default encoder handles any
            // codepoint deterministically.
        }))
        {
            writer.WriteStartObject();

            writer.WriteString("$schema", SchemaUri);
            writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
            writer.WriteString("module", ModuleName);

            writer.WriteStartArray("functions");
            foreach (TierClassification c in Classifications)
            {
                writer.WriteStartObject();
                writer.WriteString("stableId", c.Id.Value);
                writer.WriteString("functionDisplay", c.FunctionDisplay);
                writer.WriteString("tier", TierToString(c.Tier));
                writer.WriteString("reason", c.DemotionReason);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Serialize the table to deterministic UTF-8 bytes (no BOM). Equivalent
    /// to <c>Encoding.UTF8.GetBytes(Serialize())</c> but avoids the
    /// round-trip through a managed string. Two serializations of equal
    /// tables produce byte-identical arrays.
    /// </summary>
    /// <returns>The UTF-8 JSON bytes (no BOM).</returns>
    public byte[] SerializeToUtf8Bytes()
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("$schema", SchemaUri);
            writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
            writer.WriteString("module", ModuleName);
            writer.WriteStartArray("functions");
            foreach (TierClassification c in Classifications)
            {
                writer.WriteStartObject();
                writer.WriteString("stableId", c.Id.Value);
                writer.WriteString("functionDisplay", c.FunctionDisplay);
                writer.WriteString("tier", TierToString(c.Tier));
                writer.WriteString("reason", c.DemotionReason);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Parse a tier table from JSON produced by <see cref="Serialize"/>.
    /// Round-trips: <c>Parse(t.Serialize())</c> is value-equal to <c>t</c>.
    /// </summary>
    /// <param name="json">The JSON text to parse. Must not be null.</param>
    /// <returns>The parsed table (classifications re-sorted into canonical order).</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="json"/> is null.</exception>
    /// <exception cref="FormatException">If the JSON is malformed or missing a required field.</exception>
    public static TierTable Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            return FromElement(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"TierTable JSON is malformed: {ex.Message}", ex);
        }
    }

    private static TierTable FromElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("TierTable JSON root must be an object.");
        }

        string module = RequireString(root, "module");

        List<TierClassification> classifications = new();
        if (root.TryGetProperty("functions", out JsonElement functions))
        {
            if (functions.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("TierTable 'functions' must be an array.");
            }

            foreach (JsonElement fn in functions.EnumerateArray())
            {
                if (fn.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException("TierTable 'functions' entries must be objects.");
                }

                string stableId = RequireString(fn, "stableId");
                string functionDisplay = RequireString(fn, "functionDisplay");
                string tierText = RequireString(fn, "tier");
                // 'reason' is empty for Tier 2; accept an absent key as empty.
                string reason = fn.TryGetProperty("reason", out JsonElement r)
                    && r.ValueKind == JsonValueKind.String
                    ? r.GetString() ?? string.Empty
                    : string.Empty;

                classifications.Add(new TierClassification(
                    new StableId(stableId),
                    functionDisplay,
                    TierFromString(tierText),
                    reason));
            }
        }

        return new TierTable(module, classifications);
    }

    private static string RequireString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"TierTable JSON is missing required string field '{name}'.");
        }
        return value.GetString() ?? throw new FormatException(
            $"TierTable JSON field '{name}' is null.");
    }

    /// <summary>Render a <see cref="FunctionTier"/> to its canonical JSON token.</summary>
    private static string TierToString(FunctionTier tier) => tier switch
    {
        FunctionTier.Tier1 => "Tier1",
        FunctionTier.Tier2 => "Tier2",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown FunctionTier."),
    };

    /// <summary>Parse a canonical JSON tier token back to a <see cref="FunctionTier"/>.</summary>
    private static FunctionTier TierFromString(string text) => text switch
    {
        "Tier1" => FunctionTier.Tier1,
        "Tier2" => FunctionTier.Tier2,
        _ => throw new FormatException($"TierTable JSON has unknown tier token '{text}'."),
    };
}
