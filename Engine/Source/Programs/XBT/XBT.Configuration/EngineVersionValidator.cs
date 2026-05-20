// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.Configuration;

/// <summary>
/// Validates engine-version compatibility at module-resolution time
/// per Toolchain Contract Rev 13 Section 9.4 +
/// <c>/Documents/XBT.html</c> Rev 4 Section 12.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three inputs (Section 12.1):</b>
/// </para>
/// <list type="bullet">
///   <item><c>/Engine/Engine.xengine</c> -- declares the engine's
///   semantic version.</item>
///   <item>Every reachable <c>.xplugin</c> -- carries its own version
///   plus optional <c>MinEngineVersion</c>/<c>MaxEngineVersion</c>.</item>
///   <item>Project <c>.xproject</c> -- declares the project's engine
///   version (may be null for empty projects).</item>
/// </list>
/// <para>
/// <b>Rules (Section 12.2):</b>
/// </para>
/// <list type="number">
///   <item>Project's <c>EngineVersion</c> must be compatible with the
///   engine's <c>EngineVersion</c>. MVP rule: <c>major == major</c> and
///   <c>engine.minor &gt;= project.minor</c>.</item>
///   <item>Every enabled plugin's <c>[MinEngineVersion,
///   MaxEngineVersion]</c> must contain the engine version (inclusive,
///   both ends). Missing endpoints widen the range -- a null
///   <c>MinEngineVersion</c> means "no minimum"; a null
///   <c>MaxEngineVersion</c> means "no maximum".</item>
///   <item>Pre-1.0 semver: per Contract Section 9.6 footnote, the
///   compatibility-from-first-build rule applies regardless of leading
///   digit. <c>0.x.y</c> follows the same major/minor rule as
///   <c>1.x.y</c>.</item>
/// </list>
/// <para>
/// <b>Failure mode.</b> Any incompatibility throws
/// <see cref="EngineVersionMismatchException"/> with exit code <c>23</c>;
/// the diagnostic batches every offender into one report so the
/// developer can resolve them in a single pass.
/// </para>
/// </remarks>
public static class EngineVersionValidator
{
    /// <summary>
    /// Read <c>&lt;engineRootPath&gt;/Engine.xengine</c> and extract the
    /// <c>EngineVersion</c> field. Throws on missing file or malformed
    /// JSON; the caller surfaces these as build failures because the
    /// engine descriptor is the source of truth for every other check
    /// in this module.
    /// </summary>
    /// <exception cref="EngineVersionMismatchException">
    /// Thrown when the descriptor is missing, unreadable, or lacks an
    /// <c>EngineVersion</c> field.
    /// </exception>
    public static SemanticVersion DiscoverEngineVersion(string engineRootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(engineRootPath);

        string descriptorPath = Path.Combine(engineRootPath, "Engine.xengine");
        if (!File.Exists(descriptorPath))
        {
            throw new EngineVersionMismatchException(
                $"Engine descriptor not found at {descriptorPath}. " +
                "XBT requires the .xengine descriptor to identify the engine version per XBT.html Section 12.1.");
        }

        string? versionString;
        try
        {
            using FileStream stream = File.OpenRead(descriptorPath);
            using JsonDocument doc = JsonDocument.Parse(stream);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("EngineVersion", out JsonElement versionElement)
                || versionElement.ValueKind != JsonValueKind.String)
            {
                throw new EngineVersionMismatchException(
                    $"Engine descriptor at {descriptorPath} is missing the required EngineVersion string field.");
            }
            versionString = versionElement.GetString();
        }
        catch (JsonException ex)
        {
            throw new EngineVersionMismatchException(
                $"Engine descriptor at {descriptorPath} could not be parsed as JSON: {ex.Message}.");
        }

        if (string.IsNullOrEmpty(versionString))
        {
            throw new EngineVersionMismatchException(
                $"Engine descriptor at {descriptorPath} has an empty EngineVersion field.");
        }

        if (!SemanticVersion.TryParse(versionString, out SemanticVersion version))
        {
            throw new EngineVersionMismatchException(
                $"Engine descriptor at {descriptorPath} has malformed EngineVersion '{versionString}'. " +
                "Expected semver in MAJOR.MINOR.PATCH form.");
        }

        return version;
    }

    /// <summary>
    /// Validate the project's declared <see cref="ProjectDescriptor.EngineVersion"/>
    /// against the engine's actual semver. Throws
    /// <see cref="EngineVersionMismatchException"/> on incompatibility.
    /// </summary>
    /// <param name="project">
    /// The parsed project descriptor. A <c>null</c>
    /// <see cref="ProjectDescriptor.EngineVersion"/> emits a warning but
    /// not a failure (back-compat for empty projects per the Phase 1.3
    /// spec).
    /// </param>
    /// <param name="engineVersion">The engine's actual semver.</param>
    public static void ValidateProject(ProjectDescriptor project, SemanticVersion engineVersion)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (project.EngineVersion is null)
        {
            Logger.Warning(
                $"Project '{project.Name}' did not declare an EngineVersion in its .xproject. " +
                $"XBT cannot verify engine-compatibility for this project. " +
                $"Engine version is {engineVersion}.",
                new DiagnosticContext { Action = "validate-engine-version" });
            return;
        }

        if (!SemanticVersion.TryParse(project.EngineVersion, out SemanticVersion projectVersion))
        {
            throw new EngineVersionMismatchException(
                $"Project '{project.Name}' declares malformed EngineVersion '{project.EngineVersion}'. " +
                "Expected semver in MAJOR.MINOR.PATCH form.");
        }

        if (!engineVersion.IsCompatibleWith(projectVersion))
        {
            throw new EngineVersionMismatchException(
                $"Engine-version compatibility violation.\n" +
                $"  Engine: {engineVersion} (from Engine.xengine)\n" +
                $"  Project: {project.Name}\n" +
                $"    declares EngineVersion = {projectVersion} (incompatible -- requires major == major and engine.minor >= project.minor).");
        }
    }

    /// <summary>
    /// Validate every plugin's
    /// <c>[MinEngineVersion, MaxEngineVersion]</c> against the engine's
    /// semver. Collects every offender, then throws a single
    /// <see cref="EngineVersionMismatchException"/> naming them all.
    /// </summary>
    public static void ValidatePlugins(
        IReadOnlyList<PluginDescriptor> plugins,
        SemanticVersion engineVersion)
    {
        ArgumentNullException.ThrowIfNull(plugins);

        List<string> offenders = new();

        foreach (PluginDescriptor plugin in plugins)
        {
            SemanticVersion? min = null;
            SemanticVersion? max = null;

            if (plugin.MinEngineVersion is not null)
            {
                if (!SemanticVersion.TryParse(plugin.MinEngineVersion, out SemanticVersion parsedMin))
                {
                    offenders.Add(
                        $"Plugin '{plugin.Name}' {plugin.Version} declares malformed " +
                        $"MinEngineVersion = '{plugin.MinEngineVersion}'.");
                    continue;
                }
                min = parsedMin;
            }

            if (plugin.MaxEngineVersion is not null)
            {
                if (!SemanticVersion.TryParse(plugin.MaxEngineVersion, out SemanticVersion parsedMax))
                {
                    offenders.Add(
                        $"Plugin '{plugin.Name}' {plugin.Version} declares malformed " +
                        $"MaxEngineVersion = '{plugin.MaxEngineVersion}'.");
                    continue;
                }
                max = parsedMax;
            }

            if (!engineVersion.IsInRange(min, max))
            {
                offenders.Add(
                    $"Plugin '{plugin.Name}' {plugin.Version}\n" +
                    $"    declares MinEngineVersion = {FormatNullable(min)}, MaxEngineVersion = {FormatNullable(max)}\n" +
                    $"    not satisfied by engine {engineVersion}.");
            }
        }

        if (offenders.Count > 0)
        {
            StringBuilder sb = new();
            sb.AppendLine("Engine-version compatibility violation.");
            sb.AppendLine($"  Engine: {engineVersion}");
            foreach (string offender in offenders)
            {
                sb.AppendLine($"  {offender}");
            }
            throw new EngineVersionMismatchException(sb.ToString().TrimEnd());
        }
    }

    private static string FormatNullable(SemanticVersion? version)
        => version is { } v ? v.ToString() : "(none)";
}

/// <summary>
/// In-memory representation of a <c>.xproject</c> descriptor.
/// Field-for-field canonical per Toolchain Contract Rev 13 Section 9.4.
/// </summary>
/// <remarks>
/// The <c>.xproject</c> descriptor identifies a buildable project: its
/// name, the engine version it pins, the enabled / disabled plugin list,
/// and additional metadata. Phase 1.3 surfaces only the fields the build
/// orchestration consumes; later phases extend the record without
/// breaking the action-history key (descriptor parses are not part of
/// the action's <see cref="IExternalAction.CommandVersion"/>; only their
/// content hashes are, via the TargetMakefile in Phase 2).
/// </remarks>
public sealed record ProjectDescriptor
{
    /// <summary>Project name.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Engine semver the project is built against. MVP requires exact
    /// match with the engine's <c>EngineVersion</c>; future MAJOR-compat
    /// work may relax this. Null = unspecified (warning, not failure).
    /// </summary>
    public string? EngineVersion { get; init; }

    /// <summary>Project description. Optional.</summary>
    public string? Description { get; init; }

    /// <summary>Authoring organisation. Optional.</summary>
    public string? Author { get; init; }

    /// <summary>Plugins explicitly enabled by this project.</summary>
    public IReadOnlyList<string> EnabledPlugins { get; init; } = Array.Empty<string>();

    /// <summary>Plugins explicitly disabled by this project.</summary>
    public IReadOnlyList<string> DisabledPlugins { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Parses a <c>.xproject</c> JSON descriptor into a
/// <see cref="ProjectDescriptor"/>. Mirrors the safety + strict-parse
/// settings of <see cref="PluginDescriptorParser"/>.
/// </summary>
public static class ProjectDescriptorParser
{
    private static readonly HashSet<string> s_knownKeys = new(StringComparer.Ordinal)
    {
        "Name",
        "EngineVersion",
        "Description",
        "Author",
        "EnabledPlugins",
        "DisabledPlugins",
        "Copyright",            // optional copyright field (validated separately).
    };

    private static readonly JsonDocumentOptions s_documentOptions = new()
    {
        MaxDepth = 64,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    /// <summary>
    /// Parse a <c>.xproject</c> file from disk.
    /// </summary>
    public static ProjectDescriptor ParseFile(string filePath)
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
                $"Could not read .xproject at {filePath}: {ex.Message}",
                exitCode: 50,
                filePath: filePath);
        }
        return Parse(text, filePath);
    }

    /// <summary>
    /// Parse a <c>.xproject</c> JSON string.
    /// </summary>
    public static ProjectDescriptor Parse(string json, string? sourcePath = null)
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
                $"Invalid JSON in .xproject: {ex.Message}",
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
                    "Top-level value in a .xproject must be a JSON object.",
                    exitCode: 50,
                    filePath: sourcePath);
            }

            foreach (JsonProperty prop in root.EnumerateObject())
            {
                if (!s_knownKeys.Contains(prop.Name))
                {
                    throw new DescriptorParseException(
                        $"Unknown top-level key '{prop.Name}' in .xproject.",
                        exitCode: 50,
                        filePath: sourcePath);
                }
            }

            string? name = ReadString(root, "Name", sourcePath, required: true);

            return new ProjectDescriptor
            {
                Name = name!,
                EngineVersion = ReadString(root, "EngineVersion", sourcePath),
                Description = ReadString(root, "Description", sourcePath),
                Author = ReadString(root, "Author", sourcePath),
                EnabledPlugins = ReadStringArray(root, "EnabledPlugins", sourcePath),
                DisabledPlugins = ReadStringArray(root, "DisabledPlugins", sourcePath),
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
                    $"Required key '{key}' missing in .xproject.",
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
                $"Key '{key}' must be a string in .xproject (got {el.ValueKind}).",
                exitCode: 50,
                filePath: sourcePath);
        }
        return el.GetString();
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
                $"Key '{key}' must be an array in .xproject (got {el.ValueKind}).",
                exitCode: 50,
                filePath: sourcePath);
        }
        List<string> result = new(el.GetArrayLength());
        foreach (JsonElement entry in el.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                throw new DescriptorParseException(
                    $"Array entry under '{key}' must be a string in .xproject (got {entry.ValueKind}).",
                    exitCode: 50,
                    filePath: sourcePath);
            }
            result.Add(entry.GetString()!);
        }
        return result;
    }
}

/// <summary>
/// A simple semantic-version triple. Carries the
/// <see cref="IsCompatibleWith"/> rule (major equality + minor monotone)
/// and the <see cref="IsInRange"/> rule used for plugin version checks.
/// </summary>
/// <remarks>
/// <para>
/// <b>Compatibility semantics.</b> <c>engine.IsCompatibleWith(project)</c>
/// returns true iff <c>engine.Major == project.Major</c> and
/// <c>engine.Minor &gt;= project.Minor</c>. Patch is ignored for
/// compatibility (a patch-only release of the engine cannot break a
/// project pinned to the previous patch). Per the Phase 1.3 spec,
/// pre-1.0 semver (<c>0.x.y</c>) follows the same rule: from the first
/// tagged build forward, compatibility guarantees apply regardless of
/// leading digit (Contract Section 9.6 footnote).
/// </para>
/// </remarks>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch)
    : IComparable<SemanticVersion>
{
    /// <summary>
    /// Parse <c>"M.m.p"</c>. Throws <see cref="FormatException"/> on
    /// malformed input; <see cref="TryParse"/> is the
    /// allocation-free non-throwing variant.
    /// </summary>
    public static SemanticVersion Parse(string s)
    {
        if (!TryParse(s, out SemanticVersion result))
        {
            throw new FormatException(
                $"Could not parse '{s}' as semver in MAJOR.MINOR.PATCH form.");
        }
        return result;
    }

    /// <summary>
    /// Try-parse <c>"M.m.p"</c>. Returns true on success and writes
    /// the parsed triple to <paramref name="result"/>; returns false
    /// otherwise.
    /// </summary>
    public static bool TryParse(string s, out SemanticVersion result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }
        string[] parts = s.Trim().Split('.');
        if (parts.Length != 3)
        {
            return false;
        }
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minor)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int patch)
            || major < 0 || minor < 0 || patch < 0)
        {
            return false;
        }
        result = new SemanticVersion(major, minor, patch);
        return true;
    }

    /// <summary>
    /// True iff this version (representing the engine's actual semver)
    /// is compatible with <paramref name="other"/> (representing the
    /// pinned / declared semver). Per Phase 1.3 spec:
    /// <c>major == major</c> and <c>this.Minor &gt;= other.Minor</c>.
    /// </summary>
    public bool IsCompatibleWith(SemanticVersion other)
        => Major == other.Major && Minor >= other.Minor;

    /// <summary>
    /// True iff this version falls within the inclusive range
    /// <c>[min, max]</c>. A null endpoint widens the range on that
    /// side (a null <paramref name="min"/> means "no minimum").
    /// </summary>
    public bool IsInRange(SemanticVersion? min, SemanticVersion? max)
    {
        if (min is { } lower && CompareTo(lower) < 0)
        {
            return false;
        }
        if (max is { } upper && CompareTo(upper) > 0)
        {
            return false;
        }
        return true;
    }

    /// <inheritdoc/>
    public int CompareTo(SemanticVersion other)
    {
        int cmp = Major.CompareTo(other.Major);
        if (cmp != 0) return cmp;
        cmp = Minor.CompareTo(other.Minor);
        if (cmp != 0) return cmp;
        return Patch.CompareTo(other.Patch);
    }

    /// <inheritdoc/>
    public override string ToString()
        => $"{Major}.{Minor}.{Patch}";
}

/// <summary>
/// Thrown by <see cref="EngineVersionValidator"/> on any compatibility
/// violation. Maps to Toolchain Contract Rev 13 Section 13 exit code
/// <c>23</c> (<c>EngineToolchainMismatch</c>).
/// </summary>
public sealed class EngineVersionMismatchException : XBTException
{
    /// <summary>Construct an engine-version mismatch with the supplied diagnostic.</summary>
    public EngineVersionMismatchException(string message)
        : base(message, exitCode: 23)
    {
    }
}
