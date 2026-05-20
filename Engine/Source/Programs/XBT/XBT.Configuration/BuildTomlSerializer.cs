// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.Configuration;

/// <summary>
/// Round-trip companion to <see cref="BuildTomlParser"/>. Serializes a
/// <see cref="ModuleRules"/> back to TOML in a deterministic ordering:
/// keys appear in the canonical order declared by
/// <see cref="BuildTomlParser.KnownTopLevelKeys"/>; array contents
/// preserve the order of the input <see cref="ModuleRules"/>; defaults
/// are emitted only when the caller asks for the "verbose" form so the
/// round-trip from <c>Parse</c> back through <c>Serialize</c> produces
/// the smallest TOML that re-parses to the same <see cref="ModuleRules"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a hand-rolled writer?</b> Tomlyn's <c>Toml.FromModel</c>
/// emits a TomlTable in insertion order with whatever formatting it
/// prefers. We need a stable canonical order for golden-file tests:
/// the canonical order is the declaration order of the
/// <see cref="BuildTomlParser.KnownTopLevelKeys"/> set, not Tomlyn's
/// insertion order. A hand-rolled writer with explicit key ordering
/// guarantees byte-identical output for the same logical descriptor.
/// </para>
/// <para>
/// <b>Determinism rules</b> the writer enforces:
/// </para>
/// <list type="bullet">
///   <item>Keys appear in <see cref="BuildTomlParser.KnownTopLevelKeys"/>
///   alphabetical order; the canonical traversal sorts the set once.</item>
///   <item>String values are double-quoted with the standard TOML
///   escape set.</item>
///   <item>Boolean values are lowercase (<c>true</c>/<c>false</c>).</item>
///   <item>Enum values use the C# member name verbatim
///   (<c>"SSE42"</c>, <c>"NoSharedPCHs"</c>).</item>
///   <item>Arrays of strings are one-line (<c>["a", "b"]</c>) unless
///   they exceed a small threshold, then wrapped one-per-line for
///   diff readability.</item>
///   <item>Dependency entries serialise as bare strings when
///   <see cref="ModuleDep.InterfaceModule"/> is false and as inline
///   tables otherwise.</item>
/// </list>
/// </remarks>
public static class BuildTomlSerializer
{
    /// <summary>
    /// Maximum number of string-array entries to emit on a single TOML
    /// line before wrapping to one-per-line form. Tuned for diff
    /// readability of typical dependency lists.
    /// </summary>
    private const int ArrayWrapThreshold = 4;

    /// <summary>
    /// Serialize a <see cref="ModuleRules"/> instance to TOML text.
    /// Output is deterministic per the rules above.
    /// </summary>
    /// <param name="rules">The <see cref="ModuleRules"/> to serialize.</param>
    /// <param name="emitDefaults">
    /// When true, every known field is emitted (including those left
    /// at their POCO default). When false (the default), only fields
    /// whose value differs from the POCO default are emitted -- the
    /// smaller form a developer typically writes.
    /// </param>
    /// <returns>TOML source text terminated with a trailing newline.</returns>
    public static string Serialize(ModuleRules rules, bool emitDefaults = false)
    {
        ArgumentNullException.ThrowIfNull(rules);

        StringBuilder sb = new();

        // Iterate the canonical key set in alphabetical order. The set
        // ordering is implementation-defined; sorting here pins it.
        foreach (string key in BuildTomlParser.KnownTopLevelKeys.OrderBy(k => k, StringComparer.Ordinal))
        {
            EmitField(sb, key, rules, emitDefaults);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Write a serialized <see cref="ModuleRules"/> to a file via the
    /// atomic-rename contract (this writes via
    /// <see cref="File.WriteAllText(string, string, Encoding)"/>; XBT's
    /// own atomic-rename surface lands when
    /// <c>XBT.ActionGraph</c>'s atomic file I/O ships).
    /// </summary>
    public static void WriteToFile(string filePath, ModuleRules rules, bool emitDefaults = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        string text = Serialize(rules, emitDefaults);
        File.WriteAllText(filePath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // -----------------------------------------------------------------
    // Per-key dispatch. Each branch matches a canonical key from the
    // KnownTopLevelKeys set; defaults are reproduced verbatim from the
    // ModuleRules POCO so emitDefaults=false produces the smallest TOML.
    // -----------------------------------------------------------------

    private static void EmitField(StringBuilder sb, string key, ModuleRules r, bool emitDefaults)
    {
        switch (key)
        {
            case "name":
                if (emitDefaults || !string.IsNullOrEmpty(r.Name))
                {
                    EmitStringField(sb, key, r.Name);
                }
                break;
            case "tier":
                // Tier is required; always emit so a parse failure
                // doesn't accept an empty descriptor.
                EmitEnumField(sb, key, r.Tier);
                break;
            case "module_type":
                EmitEnumField(sb, key, r.ModuleType);
                break;
            case "languages":
                if (emitDefaults || r.Languages != Languages.Cpp)
                {
                    EmitLanguagesField(sb, key, r.Languages);
                }
                break;
            case "short_name":
                if (r.ShortName is not null)
                {
                    EmitStringField(sb, key, r.ShortName);
                }
                break;
            case "sim_path":
                if (emitDefaults || r.SimPath)
                {
                    EmitBoolField(sb, key, r.SimPath);
                }
                break;
            case "sim_path_conservative_roots_allowed":
                if (emitDefaults || r.SimPathConservativeRootsAllowed)
                {
                    EmitBoolField(sb, key, r.SimPathConservativeRootsAllowed);
                }
                break;
            case "simd_level":
                if (emitDefaults || r.SimdLevel != SimdLevel.Default)
                {
                    EmitEnumField(sb, key, r.SimdLevel);
                }
                break;
            case "pch_usage":
                if (emitDefaults || r.PCHUsage != PCHUsageMode.Default)
                {
                    EmitEnumField(sb, key, r.PCHUsage);
                }
                break;
            case "pch_header_file":
                if (r.PrivatePCHHeaderFile is not null)
                {
                    EmitStringField(sb, key, r.PrivatePCHHeaderFile);
                }
                break;
            case "shared_pch_header_file":
                if (r.SharedPCHHeaderFile is not null)
                {
                    EmitStringField(sb, key, r.SharedPCHHeaderFile);
                }
                break;
            case "fp_semantics":
                if (emitDefaults || r.FPSemantics != FPSemantics.Default)
                {
                    EmitEnumField(sb, key, r.FPSemantics);
                }
                break;
            case "optimize_code":
                if (emitDefaults || r.OptimizeCode != OptimizeCodeMode.Default)
                {
                    EmitEnumField(sb, key, r.OptimizeCode);
                }
                break;
            case "b_enable_exceptions":
                if (emitDefaults || r.bEnableExceptions != true)
                {
                    EmitBoolField(sb, key, r.bEnableExceptions);
                }
                break;
            case "b_use_rtti":
                if (emitDefaults || r.bUseRTTI)
                {
                    EmitBoolField(sb, key, r.bUseRTTI);
                }
                break;
            case "b_use_unity":
                if (emitDefaults || r.bUseUnity != true)
                {
                    EmitBoolField(sb, key, r.bUseUnity);
                }
                break;
            case "b_warnings_as_errors":
                if (emitDefaults || r.bWarningsAsErrors != true)
                {
                    EmitBoolField(sb, key, r.bWarningsAsErrors);
                }
                break;
            case "b_exclude_from_shared_pch":
                if (emitDefaults || r.bExcludeFromSharedPCH)
                {
                    EmitBoolField(sb, key, r.bExcludeFromSharedPCH);
                }
                break;
            case "b_allow_hot_reload":
                if (emitDefaults || r.bAllowHotReload)
                {
                    EmitBoolField(sb, key, r.bAllowHotReload);
                }
                break;
            case "b_is_test_module":
                if (emitDefaults || r.bIsTestModule)
                {
                    EmitBoolField(sb, key, r.bIsTestModule);
                }
                break;
            case "deprecation_message":
                if (r.DeprecationMessage is not null)
                {
                    EmitStringField(sb, key, r.DeprecationMessage);
                }
                break;
            case "minimum_toolchain_version":
                if (r.MinimumToolchainVersion is not null)
                {
                    EmitStringField(sb, key, r.MinimumToolchainVersion);
                }
                break;
            case "public_dependency_modules":
                if (emitDefaults || r.PublicDependencyModuleNames.Count > 0)
                {
                    EmitDepArrayField(sb, key, r.PublicDependencyModuleNames);
                }
                break;
            case "private_dependency_modules":
                if (emitDefaults || r.PrivateDependencyModuleNames.Count > 0)
                {
                    EmitDepArrayField(sb, key, r.PrivateDependencyModuleNames);
                }
                break;
            case "dynamically_loaded_modules":
                if (emitDefaults || r.DynamicallyLoadedModuleNames.Count > 0)
                {
                    EmitDepArrayField(sb, key, r.DynamicallyLoadedModuleNames);
                }
                break;
            case "public_include_paths":
                if (emitDefaults || r.PublicIncludePaths.Count > 0)
                {
                    EmitStringArrayField(sb, key, r.PublicIncludePaths);
                }
                break;
            case "private_include_paths":
                if (emitDefaults || r.PrivateIncludePaths.Count > 0)
                {
                    EmitStringArrayField(sb, key, r.PrivateIncludePaths);
                }
                break;
            case "public_definitions":
                if (emitDefaults || r.PublicDefinitions.Count > 0)
                {
                    EmitStringArrayField(sb, key, r.PublicDefinitions);
                }
                break;
            case "private_definitions":
                if (emitDefaults || r.PrivateDefinitions.Count > 0)
                {
                    EmitStringArrayField(sb, key, r.PrivateDefinitions);
                }
                break;
            default:
                // Should be impossible: keys originate from
                // KnownTopLevelKeys above. Throw to surface any drift.
                throw new InvalidOperationException(
                    $"BuildTomlSerializer has no emit branch for known key '{key}'. " +
                    "If you added the key to KnownTopLevelKeys, add a matching branch here.");
        }
    }

    // -----------------------------------------------------------------
    // Primitive emit helpers.
    // -----------------------------------------------------------------

    private static void EmitStringField(StringBuilder sb, string key, string value)
    {
        sb.Append(key).Append(" = ").Append(QuoteString(value)).Append('\n');
    }

    private static void EmitBoolField(StringBuilder sb, string key, bool value)
    {
        sb.Append(key).Append(" = ").Append(value ? "true" : "false").Append('\n');
    }

    private static void EmitEnumField<TEnum>(StringBuilder sb, string key, TEnum value)
        where TEnum : struct, Enum
    {
        sb.Append(key).Append(" = ").Append(QuoteString(value.ToString()!)).Append('\n');
    }

    private static void EmitLanguagesField(StringBuilder sb, string key, Languages langs)
    {
        // Emit as an array of single-language strings. Both = Cpp |
        // CSharp expands to ["Cpp", "CSharp"]; the parser accepts the
        // [Cpp, CSharp] form interchangeably with the Both shorthand
        // but we choose the explicit form for unambiguous round-trip.
        List<string> entries = new();
        if (langs.HasFlag(Languages.Cpp))
        {
            entries.Add("Cpp");
        }
        if (langs.HasFlag(Languages.CSharp))
        {
            entries.Add("CSharp");
        }
        if (entries.Count == 0)
        {
            entries.Add("Cpp");
        }

        EmitStringArrayField(sb, key, entries);
    }

    private static void EmitStringArrayField(StringBuilder sb, string key, IReadOnlyList<string> values)
    {
        sb.Append(key).Append(" = [");
        if (values.Count == 0)
        {
            sb.Append("]\n");
            return;
        }
        if (values.Count <= ArrayWrapThreshold)
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                sb.Append(QuoteString(values[i]));
            }
            sb.Append("]\n");
            return;
        }
        // Wrap one-per-line.
        sb.Append('\n');
        for (int i = 0; i < values.Count; i++)
        {
            sb.Append("    ").Append(QuoteString(values[i]));
            sb.Append(i == values.Count - 1 ? "\n" : ",\n");
        }
        sb.Append("]\n");
    }

    private static void EmitDepArrayField(StringBuilder sb, string key, IReadOnlyList<ModuleDep> deps)
    {
        sb.Append(key).Append(" = [");
        if (deps.Count == 0)
        {
            sb.Append("]\n");
            return;
        }
        if (deps.Count <= ArrayWrapThreshold)
        {
            for (int i = 0; i < deps.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                sb.Append(FormatDep(deps[i]));
            }
            sb.Append("]\n");
            return;
        }
        sb.Append('\n');
        for (int i = 0; i < deps.Count; i++)
        {
            sb.Append("    ").Append(FormatDep(deps[i]));
            sb.Append(i == deps.Count - 1 ? "\n" : ",\n");
        }
        sb.Append("]\n");
    }

    private static string FormatDep(ModuleDep dep)
    {
        // Shorthand whenever InterfaceModule = false; inline-table
        // otherwise. Matches the parser's two accepted forms.
        return dep.InterfaceModule
            ? $"{{ name = {QuoteString(dep.Name)}, interface_module = true }}"
            : QuoteString(dep.Name);
    }

    private static string QuoteString(string s)
    {
        // TOML basic string: double-quoted with the standard escape set.
        // We use the minimal escape set Tomlyn accepts.
        StringBuilder sb = new(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
