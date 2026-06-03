// Copyright Simgenics. All Rights Reserved.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Simgenics.XPact.XIL2CPP.Tiering;

namespace Simgenics.XPact.XIL2CPP.Emit.Mangling;

/// <summary>
/// The per-module product of XIL2CPP Pass 5 (mangling assignment) per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2: the module name, the
/// contract-version tag the mangle was computed under, and the
/// deterministically-sorted per-function <see cref="ManglingRecord"/> list,
/// with a byte-deterministic JSON sidecar serializer (<see cref="Serialize"/>)
/// and a round-trippable parser (<see cref="Parse"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Determinism.</b> <see cref="Records"/> is always sorted by
/// <see cref="StableId.Value"/> (ordinal); <see cref="Serialize"/> writes the
/// fields in a fixed order, formats through the invariant culture, and emits
/// no ambient state, so two serializations of equal tables produce
/// byte-identical output (gate X-IL2CPP-MANGLE-DET). <see cref="Parse"/>
/// reverses <see cref="Serialize"/>.
/// </para>
/// <para>
/// <b>Join to the tier table.</b> <see cref="StableId"/> is the human key
/// shared with the Pass-4 <see cref="Tiering.TierTable"/>;
/// <see cref="ManglingRecord.LinkerSymbol"/> is the ABI key. The Pass-7
/// enriched tier table joins the two by <see cref="StableId.Value"/>.
/// </para>
/// </remarks>
public sealed class ManglingTable
{
    /// <summary>The <c>$schema</c> URI the emitted JSON carries.</summary>
    public const string SchemaUri = "https://xpact.dev/schemas/ManglingTable.v1.json";

    /// <summary>The schema version integer the emitted JSON carries.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Construct a mangling table for <paramref name="moduleName"/> over
    /// <paramref name="records"/>. The records are sorted by
    /// <see cref="StableId.Value"/> (ordinal) and snapshotted.
    /// </summary>
    /// <param name="moduleName">The module the table mangles. Must not be null / empty / whitespace.</param>
    /// <param name="contractVersionTag">The contract-version short tag the mangle was computed under (WITHOUT the leading <c>_v</c>). Must not be null / empty / whitespace.</param>
    /// <param name="records">The per-function mangling records (any order). Must not be null.</param>
    /// <exception cref="ArgumentException">If <paramref name="moduleName"/> or <paramref name="contractVersionTag"/> is null / empty / whitespace.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="records"/> is null.</exception>
    public ManglingTable(string moduleName, string contractVersionTag, IEnumerable<ManglingRecord> records)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractVersionTag);
        ArgumentNullException.ThrowIfNull(records);

        ModuleName = moduleName;
        ContractVersionTag = contractVersionTag;
        Records = records
            .OrderBy(r => r.Id.Value, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The module the table mangles.</summary>
    public string ModuleName { get; }

    /// <summary>
    /// The contract-version short tag (WITHOUT the leading <c>_v</c>) the
    /// mangle was computed under.
    /// </summary>
    public string ContractVersionTag { get; }

    /// <summary>
    /// The per-function mangling records, sorted by
    /// <see cref="StableId.Value"/> (ordinal). Never null.
    /// </summary>
    public IReadOnlyList<ManglingRecord> Records { get; }

    /// <summary>
    /// Look up the mangling record for <paramref name="id"/>, or null when the
    /// table has no row for it.
    /// </summary>
    /// <param name="id">The stable id to look up.</param>
    /// <returns>The record, or null.</returns>
    public ManglingRecord? Find(StableId id)
    {
        foreach (ManglingRecord r in Records)
        {
            if (StringComparer.Ordinal.Equals(r.Id.Value, id.Value))
            {
                return r;
            }
        }
        return null;
    }

    /// <summary>
    /// Serialize the table to a deterministic, indented JSON string (no BOM).
    /// Two serializations of equal tables produce byte-identical output.
    /// </summary>
    /// <returns>The JSON text.</returns>
    public string Serialize()
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            WriteJson(writer);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Serialize the table to deterministic UTF-8 bytes (no BOM).
    /// </summary>
    /// <returns>The UTF-8 JSON bytes (no BOM).</returns>
    public byte[] SerializeToUtf8Bytes()
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            WriteJson(writer);
        }
        return buffer.WrittenSpan.ToArray();
    }

    private void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("$schema", SchemaUri);
        writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
        writer.WriteString("module", ModuleName);
        writer.WriteString("contractVersion", ContractVersionTag);

        writer.WriteStartArray("functions");
        foreach (ManglingRecord r in Records)
        {
            writer.WriteStartObject();
            writer.WriteString("stableId", r.Id.Value);
            writer.WriteString("canonicalForm", r.CanonicalForm);
            writer.WriteString("linkerSymbol", r.LinkerSymbol);
            writer.WriteBoolean("isStaticMethod", r.IsStaticMethod);
            writer.WriteBoolean("isConstructor", r.IsConstructor);
            writer.WriteBoolean("isDestructor", r.IsDestructor);
            writer.WriteBoolean("isPropertyGetter", r.IsPropertyGetter);
            writer.WriteBoolean("isPropertySetter", r.IsPropertySetter);
            writer.WriteBoolean("isOperator", r.IsOperator);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    /// <summary>
    /// Parse a mangling table from JSON produced by <see cref="Serialize"/>.
    /// Round-trips: <c>Parse(t.Serialize())</c> is value-equal to <c>t</c>.
    /// </summary>
    /// <param name="json">The JSON text to parse. Must not be null.</param>
    /// <returns>The parsed table (records re-sorted into canonical order).</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="json"/> is null.</exception>
    /// <exception cref="FormatException">If the JSON is malformed or missing a required field.</exception>
    public static ManglingTable Parse(string json)
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
            throw new FormatException($"ManglingTable JSON is malformed: {ex.Message}", ex);
        }
    }

    private static ManglingTable FromElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("ManglingTable JSON root must be an object.");
        }

        string module = RequireString(root, "module");
        string contractVersion = RequireString(root, "contractVersion");

        List<ManglingRecord> records = new();
        if (root.TryGetProperty("functions", out JsonElement functions))
        {
            if (functions.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("ManglingTable 'functions' must be an array.");
            }

            foreach (JsonElement fn in functions.EnumerateArray())
            {
                if (fn.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException("ManglingTable 'functions' entries must be objects.");
                }

                records.Add(new ManglingRecord(
                    new StableId(RequireString(fn, "stableId")),
                    RequireString(fn, "canonicalForm"),
                    RequireString(fn, "linkerSymbol"),
                    RequireBool(fn, "isStaticMethod"),
                    RequireBool(fn, "isConstructor"),
                    RequireBool(fn, "isDestructor"),
                    RequireBool(fn, "isPropertyGetter"),
                    RequireBool(fn, "isPropertySetter"),
                    RequireBool(fn, "isOperator")));
            }
        }

        return new ManglingTable(module, contractVersion, records);
    }

    private static string RequireString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"ManglingTable JSON is missing required string field '{name}'.");
        }
        return value.GetString() ?? throw new FormatException(
            $"ManglingTable JSON field '{name}' is null.");
    }

    private static bool RequireBool(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new FormatException($"ManglingTable JSON is missing required boolean field '{name}'.");
        }
        return value.GetBoolean();
    }
}
