// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Simgenics.XPact.XBT.Manifest;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Syntax;

namespace Simgenics.XPact.XBT.Configuration;

/// <summary>
/// Parses a <c>.Build.toml</c> file into a <see cref="ModuleRules"/>
/// instance. Per <c>/Documents/XBT.html</c> Rev 4 Section 3.2 +
/// Toolchain Contract Rev 13 Section 9.6.
/// </summary>
/// <remarks>
/// <para>
/// <b>Strict parse.</b> Unknown top-level keys fail discovery with exit
/// code 30 (closed surface). Typos never silently degrade behaviour
/// per <c>/Documents/XBT.html</c> Section 3.2.
/// </para>
/// <para>
/// <b>Determinism.</b> The parser preserves the order each TOML key
/// appears in the file. Multiple TOML files producing the same logical
/// content (modulo key order) merge to byte-identical
/// <see cref="ModuleRules"/> via <see cref="BuildTomlSerializer"/>'s
/// deterministic round-trip.
/// </para>
/// <para>
/// <b>Schema shape</b> (snake_case, top-level table form per the
/// Contract's Rev 12 example):
/// </para>
/// <code>
/// name = "XScoring"             # optional; defaults to file stem
/// tier = "Engine"
/// module_type = "Runtime"
/// languages = ["Cpp", "CSharp"]
/// sim_path = true
/// simd_level = "SSE42"
/// pch_usage = "NoSharedPCHs"
/// fp_semantics = "Precise"
/// optimize_code = "Default"
/// b_enable_exceptions = true
/// b_use_rtti = false
/// b_use_unity = true
/// b_warnings_as_errors = true
/// b_exclude_from_shared_pch = false
/// b_allow_hot_reload = false
/// b_is_test_module = false
/// short_name = "XScoring"               # optional
/// deprecation_message = null            # optional
/// minimum_toolchain_version = "1.0.0"   # optional, semver
///
/// public_dependency_modules  = [ "XCore", { name = "XAttr", interface_module = true } ]
/// private_dependency_modules = [ "XTelemetry" ]
/// dynamically_loaded_modules = [ "XHotReload" ]
///
/// public_include_paths  = [ "Public" ]
/// private_include_paths = [ "Private" ]
///
/// public_definitions  = [ "XSCORING_API=DLLIMPORT" ]
/// private_definitions = []
/// </code>
/// </remarks>
public static class BuildTomlParser
{
    /// <summary>
    /// The set of TOML keys that the parser recognises at the top
    /// level of a <c>.Build.toml</c> file. Anything outside this set is
    /// rejected as an unknown key (strict-parse mode per
    /// <c>/Documents/XBT.html</c> Section 3.2). Ordering here mirrors
    /// the canonical serialization order used by
    /// <see cref="BuildTomlSerializer"/>; the set itself is order-free.
    /// </summary>
    internal static readonly HashSet<string> KnownTopLevelKeys =
        new(StringComparer.Ordinal)
        {
            "name",
            "tier",
            "module_type",
            "languages",
            "short_name",
            "sim_path",
            "sim_path_conservative_roots_allowed",
            "simd_level",
            "pch_usage",
            "fp_semantics",
            "optimize_code",
            "b_enable_exceptions",
            "b_use_rtti",
            "b_use_unity",
            "b_warnings_as_errors",
            "b_exclude_from_shared_pch",
            "b_allow_hot_reload",
            "b_is_test_module",
            "deprecation_message",
            "minimum_toolchain_version",
            "public_dependency_modules",
            "private_dependency_modules",
            "dynamically_loaded_modules",
            "public_include_paths",
            "private_include_paths",
            "public_definitions",
            "private_definitions",
        };

    /// <summary>
    /// Parse a <c>.Build.toml</c> file at <paramref name="filePath"/>.
    /// The returned <see cref="ModuleRules"/>'s
    /// <see cref="ModuleRules.Name"/> defaults to the file stem (the
    /// portion of the filename before <c>.Build.toml</c>) when the
    /// TOML does not set it explicitly.
    /// </summary>
    /// <exception cref="DescriptorParseException">
    /// Thrown on TOML syntax error, unknown top-level key, bad enum
    /// value, or type mismatch. Carries the file path + line + column
    /// when Tomlyn can localise the error.
    /// </exception>
    public static ModuleRules ParseFile(string filePath)
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
                $"Could not read .Build.toml at {filePath}: {ex.Message}",
                filePath);
        }

        string defaultName = DeriveDefaultModuleName(filePath);
        return Parse(text, filePath, defaultName);
    }

    /// <summary>
    /// Parse a TOML string into a <see cref="ModuleRules"/> instance.
    /// </summary>
    /// <param name="text">The TOML source text.</param>
    /// <param name="sourcePath">
    /// Optional source path used in diagnostics (Tomlyn embeds this in
    /// every <c>SourceSpan</c>).
    /// </param>
    /// <param name="defaultModuleName">
    /// Default value for <see cref="ModuleRules.Name"/> when the TOML
    /// does not set it explicitly.
    /// </param>
    /// <exception cref="DescriptorParseException">
    /// Thrown on parse failure or validation failure.
    /// </exception>
    public static ModuleRules Parse(string text, string? sourcePath = null, string? defaultModuleName = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        DocumentSyntax doc = Toml.Parse(text, sourcePath ?? string.Empty);
        if (doc.HasErrors)
        {
            DiagnosticMessage first = doc.Diagnostics.First(d => d.Kind == DiagnosticMessageKind.Error);
            throw new DescriptorParseException(
                $"TOML syntax error: {first.Message}",
                sourcePath,
                line: first.Span.Start.Line + 1,
                column: first.Span.Start.Column + 1);
        }

        TomlTable model = doc.ToModel();

        // Strict-parse pass: every top-level key must be in the known set.
        foreach (KeyValuePair<string, object> kv in model)
        {
            if (!KnownTopLevelKeys.Contains(kv.Key))
            {
                throw new DescriptorParseException(
                    $"Unknown top-level key '{kv.Key}' in .Build.toml. " +
                    $"Allowed keys: {string.Join(", ", KnownTopLevelKeys.OrderBy(k => k, StringComparer.Ordinal))}.",
                    sourcePath);
            }
        }

        // Populate POCO. Unset keys keep their POCO defaults.
        ModuleRules rules = new()
        {
            Name = ReadString(model, "name", sourcePath) ?? defaultModuleName ?? string.Empty,
            Tier = ReadEnum<ModuleTier>(model, "tier", sourcePath, required: true) ?? default,
            ModuleType = ReadEnum<ModuleType>(model, "module_type", sourcePath, required: true) ?? default,
            Languages = ReadLanguagesFlags(model, "languages", sourcePath),
            ShortName = ReadString(model, "short_name", sourcePath),
            SimPath = ReadBool(model, "sim_path", sourcePath) ?? false,
            SimPathConservativeRootsAllowed = ReadBool(model, "sim_path_conservative_roots_allowed", sourcePath) ?? false,
            SimdLevel = ReadEnum<SimdLevel>(model, "simd_level", sourcePath, required: false) ?? SimdLevel.Default,
            PCHUsage = ReadEnum<PCHUsageMode>(model, "pch_usage", sourcePath, required: false) ?? PCHUsageMode.Default,
            FPSemantics = ReadEnum<FPSemantics>(model, "fp_semantics", sourcePath, required: false) ?? FPSemantics.Default,
            OptimizeCode = ReadEnum<OptimizeCodeMode>(model, "optimize_code", sourcePath, required: false) ?? OptimizeCodeMode.Default,
            bEnableExceptions = ReadBool(model, "b_enable_exceptions", sourcePath) ?? true,
            bUseRTTI = ReadBool(model, "b_use_rtti", sourcePath) ?? false,
            bUseUnity = ReadBool(model, "b_use_unity", sourcePath) ?? true,
            bWarningsAsErrors = ReadBool(model, "b_warnings_as_errors", sourcePath) ?? true,
            bExcludeFromSharedPCH = ReadBool(model, "b_exclude_from_shared_pch", sourcePath) ?? false,
            bAllowHotReload = ReadBool(model, "b_allow_hot_reload", sourcePath) ?? false,
            bIsTestModule = ReadBool(model, "b_is_test_module", sourcePath) ?? false,
            DeprecationMessage = ReadString(model, "deprecation_message", sourcePath),
            MinimumToolchainVersion = ReadString(model, "minimum_toolchain_version", sourcePath),
            PublicDependencyModuleNames = ReadDepList(model, "public_dependency_modules", sourcePath, allowInterfaceFlag: true),
            PrivateDependencyModuleNames = ReadDepList(model, "private_dependency_modules", sourcePath, allowInterfaceFlag: true),
            DynamicallyLoadedModuleNames = ReadDepList(model, "dynamically_loaded_modules", sourcePath, allowInterfaceFlag: false),
            PublicIncludePaths = ReadStringList(model, "public_include_paths", sourcePath),
            PrivateIncludePaths = ReadStringList(model, "private_include_paths", sourcePath),
            PublicDefinitions = ReadStringList(model, "public_definitions", sourcePath),
            PrivateDefinitions = ReadStringList(model, "private_definitions", sourcePath),
        };

        // SimPath constraint checks (XBT.html Section 4.5). These run
        // at parse time so a malformed descriptor fails before any
        // action-graph work happens.
        ValidateSimPathConstraints(rules, sourcePath);

        // Closed-surface name check: a module with no name is a parse
        // failure; we ran the file stem fallback above, so if Name is
        // still empty something's wrong with the call site.
        if (string.IsNullOrWhiteSpace(rules.Name))
        {
            throw new DescriptorParseException(
                "Module 'name' is required and could not be derived from the file path.",
                sourcePath);
        }

        return rules;
    }

    private static string DeriveDefaultModuleName(string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        // ".Build.toml" suffix -> strip; otherwise use the bare stem.
        const string suffix = ".Build.toml";
        if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return fileName[..^suffix.Length];
        }
        return Path.GetFileNameWithoutExtension(fileName);
    }

    private static void ValidateSimPathConstraints(ModuleRules rules, string? sourcePath)
    {
        if (!rules.SimPath)
        {
            return;
        }

        // SimPath modules clamp SimdLevel to <= SSE42. AVX / AVX2 /
        // AVX512 are banned per XBT.html Section 4.5; exit 41.
        if (rules.SimdLevel is SimdLevel.AVX or SimdLevel.AVX2 or SimdLevel.AVX512)
        {
            throw new DescriptorParseException(
                $"SimPath module '{rules.Name}' declared SimdLevel = {rules.SimdLevel}, " +
                "but SimPath modules clamp to <= SSE42 per /Documents/XBT.html Section 4.5. " +
                "Allowed values on SimPath: None, Default, SSE2, SSE42.",
                exitCode: 41,
                filePath: sourcePath);
        }

        // SimPath modules must not use SharedPCH. The forms
        // UseSharedPCHs and UseExplicitOrSharedPCHs both fail per
        // XBT.html Section 4.5; exit 30.
        if (rules.PCHUsage is PCHUsageMode.UseSharedPCHs or PCHUsageMode.UseExplicitOrSharedPCHs)
        {
            throw new DescriptorParseException(
                $"SimPath module '{rules.Name}' declared PCHUsage = {rules.PCHUsage}, " +
                "but SimPath modules must use NoSharedPCHs per /Documents/XBT.html Section 4.5.",
                exitCode: 30,
                filePath: sourcePath);
        }

        // FPSemantics on a SimPath module: Default auto-promotes to
        // Precise (the parser does NOT mutate the POCO here; that is
        // the toolchain's job at flag-derivation time). Explicit
        // Imprecise is banned.
        if (rules.FPSemantics == FPSemantics.Imprecise)
        {
            throw new DescriptorParseException(
                $"SimPath module '{rules.Name}' declared FPSemantics = Imprecise, " +
                "but Imprecise floating-point is banned on SimPath modules per " +
                "Toolchain Contract Section 4 / XBT.html Section 4.5.",
                exitCode: 41,
                filePath: sourcePath);
        }
    }

    // -----------------------------------------------------------------
    // Typed readers. Each returns null when the key is absent so the
    // POCO's compile-time default can take over.
    // -----------------------------------------------------------------

    private static bool? ReadBool(TomlTable t, string key, string? sourcePath)
    {
        if (!t.TryGetValue(key, out object? raw))
        {
            return null;
        }
        if (raw is bool b)
        {
            return b;
        }
        throw new DescriptorParseException(
            $"Field '{key}' must be a boolean (got {DescribeType(raw)}).",
            sourcePath);
    }

    private static string? ReadString(TomlTable t, string key, string? sourcePath)
    {
        if (!t.TryGetValue(key, out object? raw))
        {
            return null;
        }
        if (raw is string s)
        {
            return s;
        }
        throw new DescriptorParseException(
            $"Field '{key}' must be a string (got {DescribeType(raw)}).",
            sourcePath);
    }

    private static TEnum? ReadEnum<TEnum>(TomlTable t, string key, string? sourcePath, bool required)
        where TEnum : struct, Enum
    {
        if (!t.TryGetValue(key, out object? raw))
        {
            if (required)
            {
                throw new DescriptorParseException(
                    $"Field '{key}' is required (expected one of: {string.Join(", ", Enum.GetNames<TEnum>())}).",
                    sourcePath);
            }
            return null;
        }
        if (raw is not string s)
        {
            throw new DescriptorParseException(
                $"Field '{key}' must be a string enum value (got {DescribeType(raw)}).",
                sourcePath);
        }
        if (!Enum.TryParse<TEnum>(s, ignoreCase: false, out TEnum parsed))
        {
            throw new DescriptorParseException(
                $"Field '{key}' value '{s}' is not a valid {typeof(TEnum).Name}. " +
                $"Expected one of: {string.Join(", ", Enum.GetNames<TEnum>())}.",
                sourcePath);
        }
        return parsed;
    }

    private static Languages ReadLanguagesFlags(TomlTable t, string key, string? sourcePath)
    {
        // The languages field can appear two ways:
        //   languages = "Cpp"            -- single value (uncommon)
        //   languages = ["Cpp", "CSharp"]-- list (canonical)
        // We always normalize to the OR'd flag value.
        if (!t.TryGetValue(key, out object? raw))
        {
            return Languages.Cpp;
        }

        Languages result = 0;

        if (raw is string single)
        {
            return ParseSingleLanguage(single, sourcePath);
        }

        if (raw is TomlArray array)
        {
            foreach (object? entry in array)
            {
                if (entry is not string langName)
                {
                    throw new DescriptorParseException(
                        $"Field '{key}' array entries must be strings (got {DescribeType(entry)}).",
                        sourcePath);
                }
                result |= ParseSingleLanguage(langName, sourcePath);
            }
            return result == 0 ? Languages.Cpp : result;
        }

        throw new DescriptorParseException(
            $"Field '{key}' must be a string or array of strings (got {DescribeType(raw)}).",
            sourcePath);
    }

    private static Languages ParseSingleLanguage(string s, string? sourcePath)
    {
        return s switch
        {
            "Cpp" => Languages.Cpp,
            "CSharp" => Languages.CSharp,
            "Both" => Languages.Both,
            _ => throw new DescriptorParseException(
                $"Languages entry '{s}' is not valid. Expected: Cpp, CSharp, Both.",
                sourcePath),
        };
    }

    private static List<string> ReadStringList(TomlTable t, string key, string? sourcePath)
    {
        if (!t.TryGetValue(key, out object? raw))
        {
            return new List<string>();
        }
        if (raw is not TomlArray array)
        {
            throw new DescriptorParseException(
                $"Field '{key}' must be an array of strings (got {DescribeType(raw)}).",
                sourcePath);
        }
        List<string> result = new(array.Count);
        foreach (object? entry in array)
        {
            if (entry is not string s)
            {
                throw new DescriptorParseException(
                    $"Field '{key}' array entries must be strings (got {DescribeType(entry)}).",
                    sourcePath);
            }
            result.Add(s);
        }
        return result;
    }

    private static List<ModuleDep> ReadDepList(
        TomlTable t,
        string key,
        string? sourcePath,
        bool allowInterfaceFlag)
    {
        if (!t.TryGetValue(key, out object? raw))
        {
            return new List<ModuleDep>();
        }
        if (raw is not TomlArray array)
        {
            throw new DescriptorParseException(
                $"Field '{key}' must be an array (got {DescribeType(raw)}).",
                sourcePath);
        }

        List<ModuleDep> result = new(array.Count);
        foreach (object? entry in array)
        {
            if (entry is string s)
            {
                // Shorthand: bare module name. InterfaceModule = false.
                if (string.IsNullOrWhiteSpace(s))
                {
                    throw new DescriptorParseException(
                        $"Field '{key}' contains an empty/whitespace dependency name.",
                        sourcePath);
                }
                result.Add(new ModuleDep(s, InterfaceModule: false));
                continue;
            }

            if (entry is TomlTable inline)
            {
                // Inline table: { name = "X", interface_module = true|false }
                if (!inline.TryGetValue("name", out object? nameRaw)
                    || nameRaw is not string name
                    || string.IsNullOrWhiteSpace(name))
                {
                    throw new DescriptorParseException(
                        $"Field '{key}' inline-table entry is missing a 'name' string.",
                        sourcePath);
                }

                bool interfaceModule = false;
                if (inline.TryGetValue("interface_module", out object? imRaw))
                {
                    if (imRaw is not bool imBool)
                    {
                        throw new DescriptorParseException(
                            $"Field '{key}' inline-table 'interface_module' must be a boolean.",
                            sourcePath);
                    }
                    interfaceModule = imBool;
                }

                if (interfaceModule && !allowInterfaceFlag)
                {
                    throw new DescriptorParseException(
                        $"Field '{key}' does not support interface_module = true " +
                        "(dynamic-load deps are not interface-only by definition; " +
                        "the InterfaceModule flag only applies to link deps).",
                        sourcePath);
                }

                // Reject any unknown keys on the inline-table form so a
                // typo like `interfac_module` does not silently degrade.
                foreach (string inlineKey in inline.Keys)
                {
                    if (inlineKey is not ("name" or "interface_module"))
                    {
                        throw new DescriptorParseException(
                            $"Field '{key}' inline-table contains unknown key '{inlineKey}'. " +
                            "Allowed: name, interface_module.",
                            sourcePath);
                    }
                }

                result.Add(new ModuleDep(name, InterfaceModule: interfaceModule));
                continue;
            }

            throw new DescriptorParseException(
                $"Field '{key}' array entries must be either bare strings or inline tables " +
                $"(got {DescribeType(entry)}).",
                sourcePath);
        }
        return result;
    }

    private static string DescribeType(object? value) => value switch
    {
        null => "null",
        string => "string",
        bool => "boolean",
        long or int => "integer",
        double or float => "float",
        TomlArray => "array",
        TomlTable => "table",
        _ => value.GetType().Name,
    };
}
