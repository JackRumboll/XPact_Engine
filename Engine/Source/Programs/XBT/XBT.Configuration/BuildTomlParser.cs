// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Simgenics.XPact.XBT.Core;
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
    /// Regex that matches a complete <c>@expr:&lt;identifier&gt;</c>
    /// reference at TOML-value scope. The substitution pass only
    /// triggers on values that match this anchored regex; literal
    /// strings containing the prefix mid-text are pass-through.
    /// </summary>
    private static readonly Regex ExprReferencePattern = new(
        @"^@expr:([A-Za-z_][A-Za-z0-9_]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// TOML keys whose underlying POCO field accepts a list-of-strings
    /// value. <c>@expr</c> references on these keys may resolve to a
    /// list (replaces the whole field) or a single string (treated as
    /// a one-element list). Individual array elements that are
    /// <c>@expr</c> references may resolve to a string (1:1) or a list
    /// (spliced in place).
    /// </summary>
    private static readonly HashSet<string> StringListKeys =
        new(StringComparer.Ordinal)
        {
            "public_include_paths",
            "private_include_paths",
            "public_definitions",
            "private_definitions",
            "additional_libraries",
        };

    /// <summary>
    /// TOML keys whose POCO field is a single string. <c>@expr</c>
    /// references on these keys must resolve to a string; a list result
    /// is a type mismatch.
    /// </summary>
    private static readonly HashSet<string> StringScalarKeys =
        new(StringComparer.Ordinal)
        {
            "name",
            "short_name",
            "deprecation_message",
            "minimum_toolchain_version",
            "engine_version_compat",
            "pch_header_file",
            "shared_pch_header_file",
            "module_def_file",
            // Enum-string keys -- the result must be a string, then
            // re-parsed by the enum reader. Treated as string scalars
            // for the purposes of @expr substitution.
            "tier",
            "module_type",
            "simd_level",
            "pch_usage",
            "fp_semantics",
            "optimize_code",
        };

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
            "pch_header_file",
            "shared_pch_header_file",
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
            // Audit fix R4-M5: engine_version_compat declared as a
            // top-level field. Previously ModuleRules.EngineVersionCompat
            // was reachable only via the Roslyn .Build.cs escape hatch.
            "engine_version_compat",
            "public_dependency_modules",
            "private_dependency_modules",
            "dynamically_loaded_modules",
            "public_include_paths",
            "private_include_paths",
            "public_definitions",
            "private_definitions",
            "additional_libraries",
            // Phase 1g Sleef wiring: optional .def export list (Win64/MSVC).
            // BuildMode resolves the module-relative path to absolute and
            // passes it to XMSVCToolChain.LinkModule which emits
            // /DEF:<abs> so link.exe writes the named symbols into the
            // DLL's export table and produces the matching .lib import
            // library next to the .dll. See ModuleRules.ModuleDefFile.
            "module_def_file",
        };

    /// <summary>
    /// Parse a <c>.Build.toml</c> file at <paramref name="filePath"/>.
    /// The returned <see cref="ModuleRules"/>'s
    /// <see cref="ModuleRules.Name"/> defaults to the file stem (the
    /// portion of the filename before <c>.Build.toml</c>) when the
    /// TOML does not set it explicitly.
    /// </summary>
    /// <param name="filePath">Absolute path of the <c>.Build.toml</c>.</param>
    /// <param name="target">
    /// Optional active target. When non-null, the parser performs the
    /// <c>@expr:&lt;identifier&gt;</c> substitution pass per Toolchain
    /// Contract Rev 13 Section 9.6: any string value matching
    /// <c>^@expr:&lt;identifier&gt;$</c> is replaced by evaluating the
    /// named expression from the sibling <c>.Build.expr</c> file
    /// against the target's <c>target.*</c> bindings + this module's
    /// <c>module.*</c> bindings. When null, <c>@expr</c> references
    /// pass through unchanged (test-only convenience).
    /// </param>
    /// <exception cref="DescriptorParseException">
    /// Thrown on TOML syntax error, unknown top-level key, bad enum
    /// value, type mismatch, missing <c>@expr</c> identifier, or
    /// orphan <c>.Build.expr</c> file. Carries the file path + line +
    /// column when Tomlyn can localise the error. Exit code 30.
    /// </exception>
    public static ModuleRules ParseFile(string filePath, TargetRules? target = null)
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

        // Locate the optional sibling .Build.expr (per /Documents/XBT.html
        // Section 3.3): prefer <stem>.Build.expr (same stem as the .toml);
        // fall back to a *.Build.expr glob if exactly one such file
        // exists in the directory and the stem-specific lookup missed.
        IReadOnlyDictionary<string, string>? expressions = null;
        string? exprPath = LocateBuildExpr(filePath);
        if (exprPath is not null)
        {
            expressions = BuildExprFile.Load(exprPath);
        }

        // Audit fix R8-M3: compute the descriptor content hash from the
        // bytes we just read so the value flows into every emitted
        // action's CacheKeyComponents. A descriptor edit -- even one
        // that doesn't change any single field the parser reads --
        // changes this hash, which invalidates the cache for every
        // compile in the module. The hash spans the TOML bytes + the
        // sibling .Build.expr bytes (when present) so an expr-only edit
        // that drives a conditional flag also picks up the change.
        string descriptorHash = ComputeDescriptorContentHash(text, exprPath);

        return ParseInternal(text, filePath, defaultName, target, expressions, exprPath, descriptorHash);
    }

    /// <summary>
    /// Audit fix R8-M3: compose the first 16 hex characters of the
    /// BLAKE3 over the canonical descriptor bytes (TOML text + the
    /// sibling .Build.expr bytes when present). Length-prefixed
    /// concatenation so the TOML+expr boundary cannot collide with a
    /// single-file descriptor whose body happens to contain the same
    /// bytes.
    /// </summary>
    private static string ComputeDescriptorContentHash(string tomlText, string? exprPath)
    {
        using Blake3.Hasher hasher = Blake3.Hasher.New();
        Span<byte> intBuffer = stackalloc byte[4];

        byte[] tomlBytes = System.Text.Encoding.UTF8.GetBytes(tomlText);
        BitConverter.TryWriteBytes(intBuffer, tomlBytes.Length);
        hasher.Update(intBuffer);
        hasher.Update(tomlBytes);

        if (!string.IsNullOrEmpty(exprPath) && File.Exists(exprPath))
        {
            try
            {
                byte[] exprBytes = File.ReadAllBytes(exprPath);
                BitConverter.TryWriteBytes(intBuffer, exprBytes.Length);
                hasher.Update(intBuffer);
                hasher.Update(exprBytes);
            }
            catch (IOException)
            {
                // Best-effort: an unreadable expr at hash time will
                // still surface as an error in the BuildExprFile.Load
                // path called earlier; we don't double-fail here.
                BitConverter.TryWriteBytes(intBuffer, -1);
                hasher.Update(intBuffer);
            }
        }
        else
        {
            BitConverter.TryWriteBytes(intBuffer, -1);
            hasher.Update(intBuffer);
        }

        Span<byte> digest = stackalloc byte[Simgenics.XPact.XBT.Core.IoHash.Length];
        hasher.Finalize(digest);
        return new Simgenics.XPact.XBT.Core.IoHash(digest).ToString()[..16];
    }

    /// <summary>
    /// Detect an orphan <c>.Build.expr</c> file in a directory that has
    /// no matching <c>.Build.toml</c>. Per Toolchain Contract Rev 13
    /// Section 9.6 / <c>/Documents/XBT.html</c> Section 3.3, a
    /// <c>.Build.expr</c> with no companion <c>.Build.toml</c> is a
    /// build failure with exit code 30 because the descriptor surface
    /// is the TOML; the expr is a sidecar that only has meaning when
    /// referenced.
    /// </summary>
    /// <param name="directory">Module directory to check.</param>
    /// <exception cref="DescriptorParseException">
    /// Thrown when the directory contains a <c>.Build.expr</c> but no
    /// <c>.Build.toml</c>.
    /// </exception>
    public static void ValidateNoOrphanBuildExpr(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        if (!Directory.Exists(directory))
        {
            return;
        }

        bool hasToml = Directory.EnumerateFiles(directory, "*.Build.toml").Any();
        if (hasToml)
        {
            return;
        }
        string[] orphanExprs = Directory.GetFiles(directory, "*" + BuildExprFile.BuildExprSuffix);
        if (orphanExprs.Length > 0)
        {
            throw new DescriptorParseException(
                $"Orphan .Build.expr file at '{orphanExprs[0]}': a .Build.expr only " +
                "has meaning as a sidecar to a .Build.toml in the same directory. " +
                "Either add the missing .Build.toml or remove the .Build.expr.",
                filePath: orphanExprs[0]);
        }
    }

    /// <summary>
    /// Locate the sibling <c>.Build.expr</c> for a given
    /// <c>.Build.toml</c> file. Per <c>/Documents/XBT.html</c>
    /// Section 3.3 the canonical pattern is same-stem-different-suffix
    /// (<c>X.Build.toml</c> → <c>X.Build.expr</c>); the fallback when
    /// the canonical lookup misses is a single-match
    /// <c>*.Build.expr</c> in the same directory.
    /// </summary>
    /// <returns>Absolute path of the expr file, or null when none exists.</returns>
    private static string? LocateBuildExpr(string tomlPath)
    {
        string? directory = Path.GetDirectoryName(tomlPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }
        string fileName = Path.GetFileName(tomlPath);
        const string tomlSuffix = ".Build.toml";
        string stem = fileName.EndsWith(tomlSuffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^tomlSuffix.Length]
            : Path.GetFileNameWithoutExtension(fileName);

        // Canonical lookup: <stem>.Build.expr next to the TOML.
        string canonical = Path.Combine(directory, stem + BuildExprFile.BuildExprSuffix);
        if (File.Exists(canonical))
        {
            return canonical;
        }

        // Fallback: exactly one *.Build.expr in the directory.
        string[] candidates = Directory.GetFiles(directory, "*" + BuildExprFile.BuildExprSuffix);
        if (candidates.Length == 1)
        {
            return candidates[0];
        }
        return null;
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
    /// <param name="target">
    /// Optional active target. When non-null, the parser performs the
    /// <c>@expr:&lt;identifier&gt;</c> substitution pass. When null,
    /// <c>@expr</c> references are left as literal strings (test-only
    /// path; lets the TOML-only happy path keep working without a
    /// target).
    /// </param>
    /// <param name="expressions">
    /// Pre-loaded expression name -&gt; source map (as produced by
    /// <see cref="BuildExprFile.Parse(string, string?)"/>). Allows tests
    /// to supply the expression body in-process without writing a
    /// sidecar file.
    /// </param>
    /// <exception cref="DescriptorParseException">
    /// Thrown on parse failure or validation failure.
    /// </exception>
    public static ModuleRules Parse(
        string text,
        string? sourcePath = null,
        string? defaultModuleName = null,
        TargetRules? target = null,
        IReadOnlyDictionary<string, string>? expressions = null)
    {
        // Audit fix R8-M3: tests that call this string overload don't
        // have a sibling .Build.expr on disk; the hash is computed over
        // the TOML text alone. Production code (ParseFile) goes through
        // its own ComputeDescriptorContentHash which folds the expr
        // bytes too.
        string descriptorHash = ComputeDescriptorContentHash(text, exprPath: null);
        return ParseInternal(text, sourcePath, defaultModuleName, target, expressions, exprSourcePath: null, descriptorHash);
    }

    private static ModuleRules ParseInternal(
        string text,
        string? sourcePath,
        string? defaultModuleName,
        TargetRules? target,
        IReadOnlyDictionary<string, string>? expressions,
        string? exprSourcePath,
        string descriptorContentHash)
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

        // @expr substitution pass. Per Toolchain Contract Rev 13
        // Section 9.6 / /Documents/XBT.html Section 3.3: walk the
        // TomlTable and replace @expr:<identifier> values with their
        // Starlark-evaluated equivalents. Only triggers when a target
        // is supplied (test paths without a target leave @expr
        // references as literal strings); the substitution operates
        // on the TomlTable before the POCO is built so init-only
        // properties receive the substituted value at construction.
        if (target is not null)
        {
            SubstituteExpressions(model, expressions, sourcePath, exprSourcePath, target);
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
            PrivatePCHHeaderFile = ReadString(model, "pch_header_file", sourcePath),
            SharedPCHHeaderFile = ReadString(model, "shared_pch_header_file", sourcePath),
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
            // Audit fix R4-M5: engine_version_compat parsed from TOML.
            // Defaults to "*" (any-version-compatible) when the key is
            // absent so existing fixtures remain valid without edits.
            EngineVersionCompat = ReadString(model, "engine_version_compat", sourcePath) ?? "*",
            PublicDependencyModuleNames = ReadDepList(model, "public_dependency_modules", sourcePath, allowInterfaceFlag: true),
            PrivateDependencyModuleNames = ReadDepList(model, "private_dependency_modules", sourcePath, allowInterfaceFlag: true),
            DynamicallyLoadedModuleNames = ReadDepList(model, "dynamically_loaded_modules", sourcePath, allowInterfaceFlag: false),
            PublicIncludePaths = ReadStringList(model, "public_include_paths", sourcePath),
            PrivateIncludePaths = ReadStringList(model, "private_include_paths", sourcePath),
            PublicDefinitions = ReadStringList(model, "public_definitions", sourcePath),
            PrivateDefinitions = ReadStringList(model, "private_definitions", sourcePath),
            // Phase 5 test-link wiring: optional list of library paths
            // appended verbatim to the link line. Module-relative entries
            // are NOT resolved here; BuildMode resolves them against the
            // module's descriptor parent directory before passing to the
            // toolchain. Empty default keeps existing fixtures compiling.
            AdditionalLibraries = ReadStringList(model, "additional_libraries", sourcePath),
            // Phase 1g Sleef wiring: optional .def export list. The value
            // is a module-relative path; BuildMode resolves it to absolute
            // at link-emit time. Path-traversal validation runs below.
            ModuleDefFile = ReadString(model, "module_def_file", sourcePath),
        };
        // Audit fix R8-M3: inject the descriptor content hash via the
        // in-assembly setter. DescriptorContentHash is private-set so
        // user-authored .Build.cs subclasses cannot fake the value;
        // the parser (this method) and BuildCsCompiler are the only
        // legitimate writers.
        rules.ApplyDescriptorContentHash(descriptorContentHash);

        // PCH-header mutual-exclusion check. A module may declare a
        // private OR a shared PCH header, never both. Per Toolchain
        // Contract Rev 13 Section 1.5: a module has at most one PCH
        // source. Exit code 30 (RulesCompileFailed).
        ValidatePchHeaderExclusivity(rules, sourcePath);

        // SimPath constraint checks (XBT.html Section 4.5). These run
        // at parse time so a malformed descriptor fails before any
        // action-graph work happens.
        ValidateSimPathConstraints(rules, sourcePath);

        // Path-traversal validation. Every path-typed field must
        // resolve relative to the module's BaseDirectory; absolute
        // paths leak host-machine layout into the descriptor, and
        // `..` segments allow a malicious or buggy descriptor to
        // include headers from outside the module's source tree
        // (cross-module escape; reproducibility hazard). Per Toolchain
        // Contract Rev 13 Section 2.1 the reproducibility envelope
        // requires all source paths to be machine-portable.
        ValidatePathField("pch_header_file", rules.PrivatePCHHeaderFile, sourcePath);

        // shared_pch_header_file supports two forms per the UE-style
        // include-path resolution semantics:
        //   (a) BARE NAME (no path separator) -- the BuildMode grouping
        //       pass looks the file up in every module's
        //       PublicIncludePaths and resolves to a canonical absolute
        //       path. This is how two modules sharing a single physical
        //       header (declared by a third "host" module) reference
        //       the same shared PCH without cross-module `..`
        //       references. Bare names skip the path-traversal
        //       validation because there is no path to traverse; the
        //       resolver in BuildMode rejects unresolved names with
        //       exit 30.
        //   (b) RELATIVE PATH (contains '/' or '\\') -- resolved
        //       relative to the module's BaseDirectory; path-traversal
        //       and absolute-path checks apply.
        // The bare-name acceptance is per Toolchain Contract Rev 13.1
        // Section 1.5 (PCH rules) + /Documents/XBT.html Rev 4
        // Section 7 (toolchain abstraction).
        string? sharedPch = rules.SharedPCHHeaderFile;
        if (!string.IsNullOrEmpty(sharedPch))
        {
            if (sharedPch.Contains('/') || sharedPch.Contains('\\'))
            {
                // Form (b): relative-path with separator -- subject to
                // path-traversal + absolute-path validation.
                ValidatePathField("shared_pch_header_file", sharedPch, sourcePath);
            }
            else if (string.IsNullOrWhiteSpace(sharedPch))
            {
                // Whitespace-only bare name: reject at parse time. An
                // empty string was already filtered by IsNullOrEmpty
                // above; this catches "   " typos before they slip
                // into the BuildMode resolver.
                throw new DescriptorParseException(
                    "Field 'shared_pch_header_file' is whitespace-only; provide a " +
                    "header name (e.g. \"EngineCommon.h\") or a relative path " +
                    "(e.g. \"Public/EngineCommon.h\"), or omit the key.",
                    exitCode: 30,
                    filePath: sourcePath);
            }
            // else: form (a) bare name (no separators, non-whitespace).
            // The BuildMode grouping pass resolves it via UE-style
            // PublicIncludePaths lookup; nothing to validate at parse
            // time.
        }
        foreach (string p in rules.PublicIncludePaths)
        {
            ValidatePathField("public_include_paths", p, sourcePath);
        }
        foreach (string p in rules.PrivateIncludePaths)
        {
            ValidatePathField("private_include_paths", p, sourcePath);
        }

        // Phase 1g Sleef wiring: validate module_def_file as path-typed.
        // Same discipline as pch_header_file -- module-relative, no
        // path traversal, no absolute. BuildMode resolves the relative
        // form to absolute at link-emit time.
        ValidatePathField("module_def_file", rules.ModuleDefFile, sourcePath);

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

    /// <summary>
    /// Reject any path-typed field that contains an absolute path or
    /// a parent-directory (<c>..</c>) segment. Per Toolchain Contract
    /// Rev 13 Section 2.1 every path the descriptor names must be
    /// relative to the module's BaseDirectory; absolute paths and
    /// path-traversal references are forbidden. The check normalises
    /// both forward- and back-slash directory separators so a TOML
    /// authored on Windows or Linux fails identically. Exit code 30.
    /// </summary>
    private static void ValidatePathField(string fieldName, string? value, string? moduleSourcePath)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        // Reject absolute paths. Path.IsPathRooted catches both
        // Windows-style ("C:\..." or "\foo") and Unix-style ("/foo").
        if (Path.IsPathRooted(value))
        {
            throw new DescriptorParseException(
                $"Field '{fieldName}' has absolute path '{value}'; only paths " +
                "relative to the module's BaseDirectory are allowed (per Toolchain " +
                "Contract Rev 13 Section 2.1).",
                exitCode: 30,
                filePath: moduleSourcePath);
        }

        // Reject `..` segments (path traversal). Normalise the
        // separator first so a Windows-authored "..\.." and a
        // Linux-authored "../.." both fail.
        string[] segments = value.Replace('\\', '/').Split('/');
        foreach (string segment in segments)
        {
            if (segment == "..")
            {
                throw new DescriptorParseException(
                    $"Field '{fieldName}' value '{value}' contains a parent-directory " +
                    "segment '..'; path-traversal references are forbidden (per Toolchain " +
                    "Contract Rev 13 Section 2.1). Paths must stay within the module's " +
                    "BaseDirectory.",
                    exitCode: 30,
                    filePath: moduleSourcePath);
            }
        }
    }

    /// <summary>
    /// Reject a module that declares both <c>pch_header_file</c> and
    /// <c>shared_pch_header_file</c>. Per Toolchain Contract Rev 13
    /// Section 1.5 a module has at most one PCH source.
    /// </summary>
    private static void ValidatePchHeaderExclusivity(ModuleRules rules, string? sourcePath)
    {
        if (!string.IsNullOrEmpty(rules.PrivatePCHHeaderFile)
            && !string.IsNullOrEmpty(rules.SharedPCHHeaderFile))
        {
            throw new DescriptorParseException(
                $"Module '{rules.Name}' declares both pch_header_file = " +
                $"'{rules.PrivatePCHHeaderFile}' and shared_pch_header_file = " +
                $"'{rules.SharedPCHHeaderFile}'. A module can only use one PCH source " +
                "(per Toolchain Contract Rev 13 Section 1.5). Pick exactly one.",
                exitCode: 30,
                filePath: sourcePath);
        }
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

        // SimPath modules cannot declare a shared PCH header at all --
        // they're not allowed to participate in any shared PCH group
        // (Section 1.5). Exit 30.
        if (!string.IsNullOrEmpty(rules.SharedPCHHeaderFile))
        {
            throw new DescriptorParseException(
                $"SimPath module '{rules.Name}' declared shared_pch_header_file = " +
                $"'{rules.SharedPCHHeaderFile}', but SimPath modules cannot participate " +
                "in a shared PCH (per Toolchain Contract Rev 13 Section 1.5). Use " +
                "pch_header_file = ... for a private PCH instead, or omit both keys.",
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

    // -----------------------------------------------------------------
    // @expr substitution pass (Toolchain Contract Rev 13 Section 9.6 /
    // /Documents/XBT.html Rev 4 Section 3.3).
    //
    // The pass operates at the TomlTable level so substituted values
    // are visible to the POCO constructor (the POCO has init-only
    // properties; mutating after construction is impossible without
    // reflection trickery). Each known top-level key is dispatched
    // according to its expected target type:
    //
    //   string scalar : value must be a string; @expr ref must yield a string.
    //   list-of-string: value can be either a list (each element checked) or
    //                   a scalar @expr ref yielding a list.
    //   list-of-dep   : same as list-of-string for the element-level @expr
    //                   case (dep entries don't themselves substitute,
    //                   only their bare-string form does).
    //   bool / int     : @expr refs are NOT valid on these keys per spec.
    //
    // The spec also documents that expression-references-expression is
    // out of scope for Phase 1.2.1 -- the Starlark evaluator does not
    // expose expression-name bindings, so an expression has no way to
    // reference another expression by name. Enforced by construction.
    // -----------------------------------------------------------------

    private static void SubstituteExpressions(
        TomlTable model,
        IReadOnlyDictionary<string, string>? expressions,
        string? sourcePath,
        string? exprSourcePath,
        TargetRules target)
    {
        // First, build the partially-loaded module bindings from the
        // raw TOML literal values BEFORE any substitution. Phase 1.2.1
        // does NOT support @expr on the module.* binding fields
        // (tier, sim_path, simd_level); the spec calls expression-
        // references-expression out of scope, and these fields ARE the
        // expression's input. Treat any @expr value on them as an
        // unbindable module.* lookup -- it will surface naturally as
        // "Unbound name" if a downstream expression references it.
        string moduleTier = ReadRawString(model, "tier") ?? string.Empty;
        bool moduleSimPath = ReadRawBool(model, "sim_path") ?? false;
        string moduleSimdLevel = ReadRawString(model, "simd_level") ?? SimdLevel.Default.ToString();

        StarlarkEvaluator.Bindings bindings = new()
        {
            TargetPlatform = target.Platform.ToString(),
            TargetConfiguration = target.Configuration.ToString(),
            TargetStationRole = target.StationRole.ToString(),
            TargetFipsMode = target.FipsMode,
            TargetArchitecture = target.Architecture,
            ModuleTier = moduleTier,
            ModuleSimPath = moduleSimPath,
            ModuleSimdLevel = moduleSimdLevel,
        };

        // Track which expression identifiers we resolved so we can
        // warn on unused names afterwards.
        HashSet<string> usedExpressionNames = new(StringComparer.Ordinal);

        // Snapshot keys -- we mutate the table inside the loop, but
        // the key set itself does not change (we replace values in
        // place).
        List<string> keys = model.Keys.ToList();
        foreach (string key in keys)
        {
            object value = model[key];
            object? replacement = SubstituteValueForKey(
                key,
                value,
                expressions,
                bindings,
                sourcePath,
                exprSourcePath,
                usedExpressionNames);
            if (replacement is not null && !ReferenceEquals(replacement, value))
            {
                model[key] = replacement;
            }
        }

        // Unused-expression warning. The spec calls for a non-fatal
        // Logger.Warning when a .Build.expr defines a name that the
        // .Build.toml never references; the warning identifies the
        // expr file path and the unused identifier so the developer
        // can clean it up. Emitted lazily so the test paths that
        // don't bother with a Logger configuration still pass.
        if (expressions is not null)
        {
            foreach (string exprName in expressions.Keys)
            {
                if (!usedExpressionNames.Contains(exprName))
                {
                    Logger.Warning(
                        $"Unused expression name '{exprName}' in {exprSourcePath ?? "<inline expressions>"}.",
                        new DiagnosticContext { File = exprSourcePath });
                }
            }
        }
    }

    private static object? SubstituteValueForKey(
        string key,
        object value,
        IReadOnlyDictionary<string, string>? expressions,
        StarlarkEvaluator.Bindings bindings,
        string? sourcePath,
        string? exprSourcePath,
        HashSet<string> usedExpressionNames)
    {
        // Scalar string at the top level.
        if (value is string str)
        {
            if (TryParseExprReference(str, out string? identifier))
            {
                object evaluated = EvaluateExpression(
                    identifier!, expressions, bindings, sourcePath, exprSourcePath, usedExpressionNames);

                // String-scalar field -- result must be a string.
                if (StringScalarKeys.Contains(key))
                {
                    if (evaluated is string s)
                    {
                        return s;
                    }
                    throw new DescriptorParseException(
                        $"Type mismatch in @expr:{identifier} for field '{key}': " +
                        $"expression returned {DescribeEvalType(evaluated)}, but field expects a string.",
                        filePath: sourcePath);
                }

                // List-of-strings field declared as a scalar @expr ref --
                // the expression must return either a list of strings
                // or a single string (treated as one-element list).
                if (StringListKeys.Contains(key))
                {
                    return ConvertExprResultToTomlList(
                        evaluated, key, identifier!, sourcePath);
                }

                // Bool / int / array fields cannot be substituted via a
                // scalar @expr ref -- spec says "@expr pattern only
                // valid in string positions in the TOML. A TOML integer
                // or boolean cannot be @expr:..."
                throw new DescriptorParseException(
                    $"Field '{key}' does not accept an @expr scalar reference. " +
                    "@expr substitution is only supported on string-typed and list-of-string-typed fields.",
                    filePath: sourcePath);
            }
            // Literal string, not an @expr ref: pass through.
            return value;
        }

        // TomlArray at the top level. Elements may individually be
        // @expr refs; iterate and substitute. Type-check depends on
        // whether the array is a string list or a dep list.
        if (value is TomlArray array)
        {
            return SubstituteArray(
                key, array, expressions, bindings, sourcePath, exprSourcePath, usedExpressionNames);
        }

        // Anything else (bool, int, table) is left alone.
        return value;
    }

    private static TomlArray SubstituteArray(
        string key,
        TomlArray array,
        IReadOnlyDictionary<string, string>? expressions,
        StarlarkEvaluator.Bindings bindings,
        string? sourcePath,
        string? exprSourcePath,
        HashSet<string> usedExpressionNames)
    {
        bool isStringList = StringListKeys.Contains(key);

        // Walk elements and accumulate the substituted result. Each
        // string element that is an @expr ref expands; everything
        // else passes through.
        TomlArray substituted = new();
        for (int i = 0; i < array.Count; i++)
        {
            object? entry = array[i];
            if (entry is string s && TryParseExprReference(s, out string? identifier))
            {
                object evaluated = EvaluateExpression(
                    identifier!, expressions, bindings, sourcePath, exprSourcePath, usedExpressionNames);

                if (evaluated is string singleStr)
                {
                    substituted.Add(singleStr);
                    continue;
                }
                if (evaluated is List<object> list)
                {
                    if (!isStringList)
                    {
                        // Dep-list element returning a list -- not in
                        // scope for Phase 1.2.1; the dep array's element
                        // shape is a name (possibly inline table).
                        throw new DescriptorParseException(
                            $"@expr:{identifier} in array element of '{key}' returned a list, " +
                            "but dependency-list fields only support per-element string @expr refs " +
                            "returning a single name.",
                            filePath: sourcePath);
                    }
                    foreach (object item in list)
                    {
                        if (item is not string itemStr)
                        {
                            throw new DescriptorParseException(
                                $"Type mismatch in @expr:{identifier} for list field '{key}': " +
                                $"expression returned a list containing a {DescribeEvalType(item)} " +
                                "(every element of a list-of-string field must be a string).",
                                filePath: sourcePath);
                        }
                        substituted.Add(itemStr);
                    }
                    continue;
                }
                // Bool, int, etc. on a string-list element -> mismatch.
                throw new DescriptorParseException(
                    $"Type mismatch in @expr:{identifier} for list field '{key}': " +
                    $"expression returned {DescribeEvalType(evaluated)}, " +
                    "but list-of-string elements must be strings (or a list of strings).",
                    filePath: sourcePath);
            }
            substituted.Add(entry);
        }
        return substituted;
    }

    /// <summary>
    /// Convert a Starlark evaluation result into a TomlArray suitable
    /// for placement into a list-of-string TOML field. The expression
    /// may yield a single string (one-element list) or a list of
    /// strings (the canonical multi-value case).
    /// </summary>
    private static TomlArray ConvertExprResultToTomlList(
        object evaluated,
        string key,
        string identifier,
        string? sourcePath)
    {
        TomlArray result = new();
        if (evaluated is string s)
        {
            result.Add(s);
            return result;
        }
        if (evaluated is List<object> list)
        {
            foreach (object item in list)
            {
                if (item is not string itemStr)
                {
                    throw new DescriptorParseException(
                        $"Type mismatch in @expr:{identifier} for list field '{key}': " +
                        $"expression returned a list containing a {DescribeEvalType(item)} " +
                        "(every element of a list-of-string field must be a string).",
                        filePath: sourcePath);
                }
                result.Add(itemStr);
            }
            return result;
        }
        throw new DescriptorParseException(
            $"Type mismatch in @expr:{identifier} for list field '{key}': " +
            $"expression returned {DescribeEvalType(evaluated)}, " +
            "but field expects a string or a list of strings.",
            filePath: sourcePath);
    }

    /// <summary>
    /// Attempt to parse a string as a complete <c>@expr:&lt;identifier&gt;</c>
    /// reference. Returns true and emits the bare identifier on match,
    /// false otherwise (in which case the caller treats the string as
    /// a literal).
    /// </summary>
    private static bool TryParseExprReference(string value, out string? identifier)
    {
        Match match = ExprReferencePattern.Match(value);
        if (match.Success)
        {
            identifier = match.Groups[1].Value;
            return true;
        }
        identifier = null;
        return false;
    }

    /// <summary>
    /// Resolve a named expression to its evaluated value. Per spec,
    /// missing identifiers fail with a clear "@expr:&lt;name&gt; not
    /// found" diagnostic naming both the missing identifier and the
    /// expression file path.
    /// </summary>
    private static object EvaluateExpression(
        string identifier,
        IReadOnlyDictionary<string, string>? expressions,
        StarlarkEvaluator.Bindings bindings,
        string? sourcePath,
        string? exprSourcePath,
        HashSet<string> usedExpressionNames)
    {
        if (expressions is null)
        {
            throw new DescriptorParseException(
                $"@expr:{identifier} referenced from {sourcePath ?? "<inline>"} " +
                "but no .Build.expr file was found in the same directory. " +
                "Create a sibling .Build.expr file (same stem as the .Build.toml) " +
                $"defining '{identifier}'.",
                filePath: sourcePath);
        }
        if (!expressions.TryGetValue(identifier, out string? expressionSource))
        {
            throw new DescriptorParseException(
                $"@expr:{identifier} referenced from {sourcePath ?? "<inline>"} " +
                $"but expression '{identifier}' is not defined in " +
                $"{exprSourcePath ?? "<inline expressions>"}. " +
                "Add a definition for the missing identifier or remove the reference.",
                filePath: sourcePath);
        }

        usedExpressionNames.Add(identifier);
        return StarlarkEvaluator.Evaluate(expressionSource, bindings, exprSourcePath);
    }

    /// <summary>
    /// Describe an evaluator-returned value for use in diagnostics.
    /// Mirrors <see cref="StarlarkEvaluator"/>'s internal naming
    /// (the evaluator's own DescribeType is private).
    /// </summary>
    private static string DescribeEvalType(object value) => value switch
    {
        string => "string",
        long => "integer",
        bool => "boolean",
        List<object> => "list",
        StarlarkEvaluator.Nothing => "None",
        _ => value.GetType().Name,
    };

    /// <summary>
    /// Read a TOML scalar string value without triggering any of the
    /// type-checking diagnostics the normal readers raise. Returns
    /// null when the key is absent, when the value is not a string,
    /// or when the value is a string but happens to be an @expr ref
    /// (used for module.* binding lookup; the spec excludes
    /// expression-references-expression from Phase 1.2.1 so a module
    /// binding cannot itself be @expr).
    /// </summary>
    private static string? ReadRawString(TomlTable t, string key)
    {
        if (!t.TryGetValue(key, out object? raw))
        {
            return null;
        }
        if (raw is string s && !ExprReferencePattern.IsMatch(s))
        {
            return s;
        }
        return null;
    }

    private static bool? ReadRawBool(TomlTable t, string key)
    {
        if (!t.TryGetValue(key, out object? raw))
        {
            return null;
        }
        if (raw is bool b)
        {
            return b;
        }
        return null;
    }
}
