// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Simgenics.XPact.XBT.Core;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Core;

/// <summary>
/// Audit fix R8-M5: pins the wire shape of the streaming JSON channel
/// record defined by <see cref="DiagnosticRecord"/>. Per
/// <c>/Documents/XBT.html</c> Rev 10 Section 8.1 the channel schema is
/// an <strong>intentional exclusion</strong> from <c>ContractSurface</c>
/// (its consumers are IDE / CI dashboards that version independently
/// of the XHT / XIL2CPP / XBT.ActionGraph triangle), but a silent
/// rename or drop of any field is still a wire break that this pin
/// surfaces in CI.
/// </summary>
/// <remarks>
/// <para>
/// The pinning policy is:
/// </para>
/// <list type="bullet">
///   <item><strong>Renaming or dropping</strong> any field name listed in
///   <see cref="ExpectedJsonFieldNames"/> is a non-additive schema
///   change. The author MUST bump
///   <c>ContractSurface.SemanticVersionTag</c> at the same time even
///   though the rename does not affect the auto-derived structure hash
///   directly.</item>
///   <item><strong>Adding</strong> a new optional field is allowed without a
///   tag bump because every documented consumer (Section 21.2: IDE
///   filtering + CI parsing) is required to tolerate unknown JSON
///   properties. The test still surfaces the new field in review by
///   failing on the count mismatch; the reviewer extends
///   <see cref="ExpectedJsonFieldNames"/> in the same commit that adds
///   the field.</item>
/// </list>
/// </remarks>
public sealed class DiagnosticRecordSchemaTests
{
    /// <summary>
    /// The exact JSON property names the streaming channel emits.
    /// The names are camel-cased by the channel writer's
    /// <see cref="JsonNamingPolicy.CamelCase"/> policy except for the
    /// explicitly-overridden <c>simpath</c> (per the example in
    /// <c>/Documents/XBT.html</c> §21.2 which uses lowercase
    /// <c>simpath</c> rather than the auto-camel-cased
    /// <c>simPath</c>).
    /// </summary>
    private static readonly IReadOnlyList<string> ExpectedJsonFieldNames = new[]
    {
        "timestamp",
        "level",
        "message",
        "action",
        "module",
        "file",
        "line",
        "column",
        "tier",
        "simpath",
        "exitCode",
    };

    /// <summary>
    /// Audit fix R8-M5: the property set on <see cref="DiagnosticRecord"/>
    /// matches the pinned list. Adding a property fails the test (the
    /// reviewer extends <see cref="ExpectedJsonFieldNames"/> in the
    /// same commit); renaming a property fails the test for the same
    /// reason. The test is the wire-shape gate that the spec's
    /// "intentional exclusion from ContractSurface" decision relies on.
    /// </summary>
    [Fact]
    public void DiagnosticRecord_PropertySet_MatchesPinnedSchema()
    {
        // Walk the type's public instance properties in declaration order.
        PropertyInfo[] props = typeof(DiagnosticRecord)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);

        // Resolve each property's effective JSON name. The shared
        // JsonSerializerOptions on Logger uses
        // PropertyNamingPolicy.CamelCase; any property carrying an
        // explicit JsonPropertyName overrides the camel-case default
        // (SimPath -> "simpath" is the one override per the spec
        // example).
        string[] actualJsonNames = props
            .Select(p =>
            {
                JsonPropertyNameAttribute? attr = p.GetCustomAttribute<JsonPropertyNameAttribute>();
                if (attr is not null)
                {
                    return attr.Name;
                }
                return JsonNamingPolicy.CamelCase.ConvertName(p.Name);
            })
            .ToArray();

        // Sort both lists ordinal so the assertion does not care about
        // declaration order (the spec describes a field SET, not a
        // sequence). The serialiser already produces a stable order
        // because JsonSerializerOptions preserves declared-property
        // order; the test compares set membership.
        List<string> expected = ExpectedJsonFieldNames.OrderBy(s => s, StringComparer.Ordinal).ToList();
        List<string> actual = actualJsonNames.OrderBy(s => s, StringComparer.Ordinal).ToList();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Audit fix R8-M5: serialising a fully-populated record produces a
    /// JSON object whose keys are exactly the pinned set (no extra,
    /// no missing).
    /// </summary>
    [Fact]
    public void DiagnosticRecord_Serialisation_EmitsPinnedJsonKeys()
    {
        DiagnosticRecord record = new()
        {
            Timestamp = new DateTimeOffset(2026, 5, 21, 14, 0, 0, TimeSpan.Zero),
            Level = DiagnosticLevel.Error,
            Message = "test",
            Action = "compile",
            Module = "XCore",
            File = "Engine/Source/X.cpp",
            Line = 42,
            Column = 7,
            Tier = "Engine",
            SimPath = true,
            ExitCode = 70,
        };

        // Use the same options shape the production Logger uses so
        // the test is wire-equivalent.
        JsonSerializerOptions opts = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        opts.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        string json = JsonSerializer.Serialize(record, opts);
        using JsonDocument doc = JsonDocument.Parse(json);

        HashSet<string> emitted = new(StringComparer.Ordinal);
        foreach (JsonProperty p in doc.RootElement.EnumerateObject())
        {
            emitted.Add(p.Name);
        }

        HashSet<string> expected = new(ExpectedJsonFieldNames, StringComparer.Ordinal);
        // Each expected key MUST appear on the wire. The
        // WhenWritingNull policy means optional null fields are
        // dropped, but every field above was supplied.
        Assert.Equal(expected.OrderBy(s => s, StringComparer.Ordinal).ToList(),
                     emitted.OrderBy(s => s, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Audit fix R8-M5: <see cref="DiagnosticLevel"/> values render as
    /// lowercase strings via the camelCase enum converter. A rename
    /// (e.g. Info -> Information) would silently break IDE consumers
    /// that filter by level; the test pins the mapping.
    /// </summary>
    [Fact]
    public void DiagnosticLevel_Serialisation_EmitsLowercaseStrings()
    {
        JsonSerializerOptions opts = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        opts.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        Assert.Equal("\"debug\"", JsonSerializer.Serialize(DiagnosticLevel.Debug, opts));
        Assert.Equal("\"info\"", JsonSerializer.Serialize(DiagnosticLevel.Info, opts));
        Assert.Equal("\"warning\"", JsonSerializer.Serialize(DiagnosticLevel.Warning, opts));
        Assert.Equal("\"error\"", JsonSerializer.Serialize(DiagnosticLevel.Error, opts));
    }
}
