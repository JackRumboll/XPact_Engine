// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Simgenics.XPact.XBT.Configuration;

/// <summary>
/// Parses a <c>.xplugin</c> JSON file into a
/// <see cref="PluginDescriptor"/>. Per <c>/Documents/XBT.html</c> Rev 4
/// Section 17 and Toolchain Contract Rev 13 Section 9.4.
/// </summary>
/// <remarks>
/// <para>
/// <b>Strict parse.</b> Unknown top-level JSON keys fail with a
/// <see cref="DescriptorParseException"/> carrying exit code 50
/// (<c>ManifestMalformed</c> per Contract Section 13). Typos in the
/// descriptor do not silently degrade behaviour.
/// </para>
/// <para>
/// <b>Safety settings</b> matching the JSON manifest's:
/// </para>
/// <list type="bullet">
///   <item><c>MaxDepth = 64</c> -- bounds recursion-depth attacks on a
///   maliciously crafted descriptor.</item>
///   <item><c>AllowTrailingCommas = false</c> -- strict per the JSON
///   spec (RFC 8259).</item>
///   <item><c>ReadCommentHandling = Disallow</c> -- comments in JSON
///   are non-standard.</item>
/// </list>
/// </remarks>
public static class PluginDescriptorParser
{
    private static readonly HashSet<string> s_knownKeys = new(StringComparer.Ordinal)
    {
        "Name",
        "Version",
        "MinEngineVersion",
        "MaxEngineVersion",
        "Description",
        "Author",
        "Modules",
        "Dependencies",
        "EnabledByDefault",
        "Platforms",
        "Targets",
    };

    private static readonly JsonDocumentOptions s_documentOptions = new()
    {
        MaxDepth = 64,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    /// <summary>
    /// Parse a <c>.xplugin</c> file from disk.
    /// </summary>
    /// <exception cref="DescriptorParseException">
    /// On read failure, JSON parse failure, unknown top-level key,
    /// missing required field, or type mismatch. Exit code 50.
    /// </exception>
    public static PluginDescriptor ParseFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        string text;
        try
        {
            text = File.ReadAllText(filePath);
        }
        catch (IOException ex)
        {
            throw new DescriptorParseException(
                $"Could not read .xplugin at {filePath}: {ex.Message}",
                exitCode: 50,
                filePath: filePath);
        }

        return Parse(text, filePath);
    }

    /// <summary>
    /// Parse a <c>.xplugin</c> JSON string.
    /// </summary>
    public static PluginDescriptor Parse(string json, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, s_documentOptions);
        }
        catch (JsonException ex)
        {
            throw new DescriptorParseException(
                $"Invalid JSON in .xplugin: {ex.Message}",
                exitCode: 50,
                filePath: sourcePath,
                line: (int?)ex.LineNumber + 1,
                column: (int?)ex.BytePositionInLine + 1);
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new DescriptorParseException(
                    "Top-level value in a .xplugin must be a JSON object.",
                    exitCode: 50,
                    filePath: sourcePath);
            }

            // Strict-parse pass.
            foreach (JsonProperty prop in root.EnumerateObject())
            {
                if (!s_knownKeys.Contains(prop.Name))
                {
                    throw new DescriptorParseException(
                        $"Unknown top-level key '{prop.Name}' in .xplugin. " +
                        $"Allowed: {string.Join(", ", s_knownKeys.OrderBy(k => k, StringComparer.Ordinal))}.",
                        exitCode: 50,
                        filePath: sourcePath);
                }
            }

            string? name = ReadString(root, "Name", sourcePath, required: true);
            string? version = ReadString(root, "Version", sourcePath, required: true);

            return new PluginDescriptor
            {
                Name = name!,
                Version = version!,
                MinEngineVersion = ReadString(root, "MinEngineVersion", sourcePath),
                MaxEngineVersion = ReadString(root, "MaxEngineVersion", sourcePath),
                Description = ReadString(root, "Description", sourcePath),
                Author = ReadString(root, "Author", sourcePath),
                Modules = ReadStringArray(root, "Modules", sourcePath),
                Dependencies = ReadStringArray(root, "Dependencies", sourcePath),
                EnabledByDefault = ReadBool(root, "EnabledByDefault", sourcePath) ?? true,
                Platforms = ReadStringArray(root, "Platforms", sourcePath),
                Targets = ReadStringArray(root, "Targets", sourcePath),
            };
        }
    }

    private static string? ReadString(JsonElement root, string key, string? sourcePath, bool required = false)
    {
        if (!root.TryGetProperty(key, out JsonElement el))
        {
            if (required)
            {
                throw new DescriptorParseException(
                    $"Required key '{key}' missing in .xplugin.",
                    exitCode: 50,
                    filePath: sourcePath);
            }
            return null;
        }
        if (el.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (el.ValueKind != JsonValueKind.String)
        {
            throw new DescriptorParseException(
                $"Key '{key}' must be a string in .xplugin (got {el.ValueKind}).",
                exitCode: 50,
                filePath: sourcePath);
        }
        return el.GetString();
    }

    private static bool? ReadBool(JsonElement root, string key, string? sourcePath)
    {
        if (!root.TryGetProperty(key, out JsonElement el))
        {
            return null;
        }
        if (el.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (el.ValueKind is JsonValueKind.True)
        {
            return true;
        }
        if (el.ValueKind is JsonValueKind.False)
        {
            return false;
        }
        throw new DescriptorParseException(
            $"Key '{key}' must be a boolean in .xplugin (got {el.ValueKind}).",
            exitCode: 50,
            filePath: sourcePath);
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string key, string? sourcePath)
    {
        if (!root.TryGetProperty(key, out JsonElement el))
        {
            return Array.Empty<string>();
        }
        if (el.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<string>();
        }
        if (el.ValueKind != JsonValueKind.Array)
        {
            throw new DescriptorParseException(
                $"Key '{key}' must be an array in .xplugin (got {el.ValueKind}).",
                exitCode: 50,
                filePath: sourcePath);
        }
        List<string> result = new(el.GetArrayLength());
        foreach (JsonElement entry in el.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                throw new DescriptorParseException(
                    $"Array entry under '{key}' must be a string in .xplugin (got {entry.ValueKind}).",
                    exitCode: 50,
                    filePath: sourcePath);
            }
            result.Add(entry.GetString()!);
        }
        return result;
    }
}
