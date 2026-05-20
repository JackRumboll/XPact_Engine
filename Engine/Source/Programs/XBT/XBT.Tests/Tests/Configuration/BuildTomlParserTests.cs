// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Manifest;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Configuration;

/// <summary>
/// Exercises <see cref="BuildTomlParser"/> against the canonical
/// descriptor shape locked by Toolchain Contract Rev 13 Section 9.6
/// and <c>/Documents/XBT.html</c> Rev 4 Section 3.2.
/// </summary>
/// <remarks>
/// <para>
/// Each test constructs the TOML in-process (no fixture files on
/// disk) so the tests are self-contained and survive a CI bisect over
/// the source tree without external dependencies.
/// </para>
/// <para>
/// The tests assert both happy-path round-trip behaviour and the
/// failure modes documented in the contract: unknown keys reject;
/// SimPath constraints fire with the right exit code; ModuleDep
/// shorthand vs. inline-table forms parse equivalently.
/// </para>
/// </remarks>
public sealed class BuildTomlParserTests
{
    [Fact]
    public void Minimal_ValidToml_Parses()
    {
        const string toml = """
            name = "XCore"
            tier = "Engine"
            module_type = "Runtime"
            """;

        ModuleRules rules = BuildTomlParser.Parse(toml);

        Assert.Equal("XCore", rules.Name);
        Assert.Equal(ModuleTier.Engine, rules.Tier);
        Assert.Equal(ModuleType.Runtime, rules.ModuleType);
        Assert.Equal(Languages.Cpp, rules.Languages);
        Assert.False(rules.SimPath);
        Assert.Equal(SimdLevel.Default, rules.SimdLevel);
        Assert.True(rules.bEnableExceptions);
        Assert.False(rules.bUseRTTI);
        Assert.True(rules.bUseUnity);
        Assert.True(rules.bWarningsAsErrors);
        Assert.Empty(rules.PublicDependencyModuleNames);
    }

    [Fact]
    public void FullyPopulated_Toml_Parses_AllFieldsExposed()
    {
        const string toml = """
            name = "XScoring"
            tier = "Engine"
            module_type = "Runtime"
            languages = ["Cpp", "CSharp"]
            short_name = "XS"
            sim_path = true
            sim_path_conservative_roots_allowed = false
            simd_level = "SSE42"
            pch_usage = "NoSharedPCHs"
            fp_semantics = "Precise"
            optimize_code = "Always"
            b_enable_exceptions = false
            b_use_rtti = true
            b_use_unity = false
            b_warnings_as_errors = true
            b_exclude_from_shared_pch = true
            b_allow_hot_reload = false
            b_is_test_module = false
            deprecation_message = "use XScoring2 instead"
            minimum_toolchain_version = "1.2.3"
            public_dependency_modules  = ["XCore", "XSerialization"]
            private_dependency_modules = ["XTelemetry"]
            dynamically_loaded_modules = ["XHotReload"]
            public_include_paths  = ["Public"]
            private_include_paths = ["Private"]
            public_definitions    = ["XSCORING_API=DLLIMPORT"]
            private_definitions   = ["XSCORING_INTERNAL"]
            """;

        ModuleRules rules = BuildTomlParser.Parse(toml);

        Assert.Equal("XScoring", rules.Name);
        Assert.Equal("XS", rules.ShortName);
        Assert.True(rules.SimPath);
        Assert.Equal(Languages.Cpp | Languages.CSharp, rules.Languages);
        Assert.Equal(SimdLevel.SSE42, rules.SimdLevel);
        Assert.Equal(PCHUsageMode.NoSharedPCHs, rules.PCHUsage);
        Assert.Equal(FPSemantics.Precise, rules.FPSemantics);
        Assert.Equal(OptimizeCodeMode.Always, rules.OptimizeCode);
        Assert.False(rules.bEnableExceptions);
        Assert.True(rules.bUseRTTI);
        Assert.False(rules.bUseUnity);
        Assert.True(rules.bExcludeFromSharedPCH);
        Assert.Equal("use XScoring2 instead", rules.DeprecationMessage);
        Assert.Equal("1.2.3", rules.MinimumToolchainVersion);
        Assert.Equal(2, rules.PublicDependencyModuleNames.Count);
        Assert.Equal("XCore", rules.PublicDependencyModuleNames[0].Name);
        Assert.Equal("XHotReload", rules.DynamicallyLoadedModuleNames[0].Name);
        Assert.Contains("Private", rules.PrivateIncludePaths);
        Assert.Contains("XSCORING_API=DLLIMPORT", rules.PublicDefinitions);
    }

    [Fact]
    public void BareString_Dependency_Defaults_To_NonInterface()
    {
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            public_dependency_modules = ["XCore", "XMath"]
            """;

        ModuleRules rules = BuildTomlParser.Parse(toml);

        Assert.Equal(2, rules.PublicDependencyModuleNames.Count);
        Assert.All(rules.PublicDependencyModuleNames, d => Assert.False(d.InterfaceModule));
    }

    [Fact]
    public void InlineTable_Dependency_With_InterfaceModuleTrue_PreservesFlag()
    {
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            public_dependency_modules = [
                { name = "XCore", interface_module = false },
                { name = "XHeaderOnly", interface_module = true },
            ]
            """;

        ModuleRules rules = BuildTomlParser.Parse(toml);

        Assert.Equal(2, rules.PublicDependencyModuleNames.Count);
        Assert.False(rules.PublicDependencyModuleNames[0].InterfaceModule);
        Assert.True(rules.PublicDependencyModuleNames[1].InterfaceModule);
        Assert.Equal("XHeaderOnly", rules.PublicDependencyModuleNames[1].Name);
    }

    [Fact]
    public void SimPath_With_AVX_SimdLevel_Rejects_With_Exit41()
    {
        const string toml = """
            name = "XScoring"
            tier = "Engine"
            module_type = "Runtime"
            sim_path = true
            simd_level = "AVX"
            pch_usage = "NoSharedPCHs"
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildTomlParser.Parse(toml));
        Assert.Equal(41, ex.ExitCode);
        Assert.Contains("SimPath", ex.Message);
        Assert.Contains("AVX", ex.Message);
    }

    [Fact]
    public void SimPath_With_UseSharedPCHs_Rejects_With_Exit30()
    {
        const string toml = """
            name = "XScoring"
            tier = "Engine"
            module_type = "Runtime"
            sim_path = true
            pch_usage = "UseSharedPCHs"
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildTomlParser.Parse(toml));
        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("SimPath", ex.Message);
        Assert.Contains("NoSharedPCHs", ex.Message);
    }

    [Fact]
    public void Unknown_TopLevel_Key_Rejects_StrictParse()
    {
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            this_is_a_typo = "oops"
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildTomlParser.Parse(toml));
        Assert.Contains("this_is_a_typo", ex.Message);
        Assert.Contains("Unknown top-level key", ex.Message);
    }

    [Fact]
    public void Malformed_Toml_Rejects_With_LineColumn()
    {
        // Missing closing bracket on the array literal.
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            public_include_paths = ["unterminated
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildTomlParser.Parse(toml));
        // Tomlyn reports a syntax error -- exit code 30, with line info.
        Assert.Equal(30, ex.ExitCode);
        Assert.True(ex.Line.HasValue);
    }

    [Fact]
    public void BadEnumValue_Tier_Rejects()
    {
        const string toml = """
            name = "X"
            tier = "Universe"
            module_type = "Runtime"
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildTomlParser.Parse(toml));
        Assert.Contains("'Universe'", ex.Message);
        Assert.Contains("ModuleTier", ex.Message);
    }

    [Fact]
    public void Round_Trip_Stable_Serialize_Then_Parse()
    {
        // Construct a ModuleRules in code, serialize it, parse the
        // result, and assert structural equality on every observable
        // field. The serializer's canonical ordering is what makes
        // this a meaningful round-trip test.
        ModuleRules input = new()
        {
            Name = "XScoring",
            Tier = ModuleTier.Engine,
            ModuleType = ModuleType.Runtime,
            Languages = Languages.Cpp | Languages.CSharp,
            SimPath = true,
            SimdLevel = SimdLevel.SSE42,
            PCHUsage = PCHUsageMode.NoSharedPCHs,
            FPSemantics = FPSemantics.Precise,
            bWarningsAsErrors = true,
            PublicDependencyModuleNames =
            {
                new ModuleDep("XCore", InterfaceModule: false),
                new ModuleDep("XHeaderOnly", InterfaceModule: true),
            },
            PrivateDependencyModuleNames = { new ModuleDep("XTelemetry", false) },
            DynamicallyLoadedModuleNames = { new ModuleDep("XHotReload", false) },
            PublicIncludePaths = { "Public" },
            PublicDefinitions = { "XSCORING_API=DLLIMPORT" },
            MinimumToolchainVersion = "1.0.0",
        };

        string toml = BuildTomlSerializer.Serialize(input);
        ModuleRules output = BuildTomlParser.Parse(toml);

        Assert.Equal(input.Name, output.Name);
        Assert.Equal(input.Tier, output.Tier);
        Assert.Equal(input.ModuleType, output.ModuleType);
        Assert.Equal(input.Languages, output.Languages);
        Assert.Equal(input.SimPath, output.SimPath);
        Assert.Equal(input.SimdLevel, output.SimdLevel);
        Assert.Equal(input.PCHUsage, output.PCHUsage);
        Assert.Equal(input.FPSemantics, output.FPSemantics);
        Assert.Equal(input.MinimumToolchainVersion, output.MinimumToolchainVersion);
        Assert.Equal(input.PublicDependencyModuleNames.Count, output.PublicDependencyModuleNames.Count);
        for (int i = 0; i < input.PublicDependencyModuleNames.Count; i++)
        {
            Assert.Equal(input.PublicDependencyModuleNames[i].Name, output.PublicDependencyModuleNames[i].Name);
            Assert.Equal(input.PublicDependencyModuleNames[i].InterfaceModule, output.PublicDependencyModuleNames[i].InterfaceModule);
        }
        Assert.Equal(input.PrivateDependencyModuleNames.Single().Name, output.PrivateDependencyModuleNames.Single().Name);
        Assert.Equal(input.DynamicallyLoadedModuleNames.Single().Name, output.DynamicallyLoadedModuleNames.Single().Name);
        Assert.Equal(input.PublicIncludePaths, output.PublicIncludePaths);
        Assert.Equal(input.PublicDefinitions, output.PublicDefinitions);
    }

    [Fact]
    public void Serialize_Is_Deterministic_Across_Two_Calls()
    {
        // The canonical-ordering property: two Serialize calls on the
        // same input produce byte-identical output regardless of when
        // the call happened or which thread it was on.
        ModuleRules rules = new()
        {
            Name = "X",
            Tier = ModuleTier.Studio,
            ModuleType = ModuleType.Editor,
            PublicDependencyModuleNames = { "XCore", "XMath" },
            PublicIncludePaths = { "Public" },
        };

        string s1 = BuildTomlSerializer.Serialize(rules);
        string s2 = BuildTomlSerializer.Serialize(rules);
        Assert.Equal(s1, s2);
    }

    [Fact]
    public void DynamicLoaded_With_InterfaceModule_True_Rejects()
    {
        // The per-dep InterfaceModule flag is meaningless for dynamic
        // deps; the parser rejects an explicit true to surface authoring
        // mistakes.
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            dynamically_loaded_modules = [{ name = "XHotReload", interface_module = true }]
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildTomlParser.Parse(toml));
        Assert.Contains("interface_module = true", ex.Message);
    }

    [Fact]
    public void Inline_Dep_Table_With_Unknown_Key_Rejects()
    {
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            public_dependency_modules = [{ name = "XCore", interfac_module = false }]
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildTomlParser.Parse(toml));
        Assert.Contains("interfac_module", ex.Message);
    }

    // -----------------------------------------------------------------
    // Phase 1.4b: pch_header_file + shared_pch_header_file TOML keys.
    // -----------------------------------------------------------------

    /// <summary>
    /// A TOML descriptor with <c>pch_header_file</c> populates
    /// <see cref="ModuleRules.PrivatePCHHeaderFile"/>; the shared field
    /// remains null.
    /// </summary>
    [Fact]
    public void PchHeaderFile_BindsToPrivatePCHHeaderFile()
    {
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            pch_header_file = "Public/XPrivate.h"
            """;

        ModuleRules rules = BuildTomlParser.Parse(toml);

        Assert.Equal("Public/XPrivate.h", rules.PrivatePCHHeaderFile);
        Assert.Null(rules.SharedPCHHeaderFile);
    }

    /// <summary>
    /// A TOML descriptor with <c>shared_pch_header_file</c> populates
    /// <see cref="ModuleRules.SharedPCHHeaderFile"/>; the private field
    /// remains null.
    /// </summary>
    [Fact]
    public void SharedPchHeaderFile_BindsToSharedPCHHeaderFile()
    {
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            shared_pch_header_file = "Public/XShared.h"
            """;

        ModuleRules rules = BuildTomlParser.Parse(toml);

        Assert.Equal("Public/XShared.h", rules.SharedPCHHeaderFile);
        Assert.Null(rules.PrivatePCHHeaderFile);
    }

    /// <summary>
    /// A TOML descriptor with BOTH <c>pch_header_file</c> AND
    /// <c>shared_pch_header_file</c> is a parse failure (exit 30) per
    /// Toolchain Contract Section 1.5 -- a module can only use one PCH
    /// source.
    /// </summary>
    [Fact]
    public void BothPchKeys_Rejects_With_Exit30()
    {
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            pch_header_file = "Public/XPrivate.h"
            shared_pch_header_file = "Public/XShared.h"
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildTomlParser.Parse(toml));
        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("pch_header_file", ex.Message);
        Assert.Contains("shared_pch_header_file", ex.Message);
    }

    /// <summary>
    /// A SimPath module declaring <c>shared_pch_header_file</c> is a
    /// parse failure (exit 30) -- SimPath modules cannot participate in
    /// a shared PCH per Contract Section 1.5.
    /// </summary>
    [Fact]
    public void SimPath_With_SharedPchHeaderFile_Rejects_With_Exit30()
    {
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            sim_path = true
            simd_level = "SSE42"
            pch_usage = "NoSharedPCHs"
            shared_pch_header_file = "Public/XShared.h"
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildTomlParser.Parse(toml));
        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("SimPath", ex.Message);
        Assert.Contains("shared", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------
    // Path-traversal validation. Per Toolchain Contract Rev 13 Section
    // 2.1 every path-typed field must resolve relative to the module's
    // BaseDirectory. Absolute paths and `..` segments are forbidden;
    // both rejection forms exit with code 30 (RulesCompileFailed).
    // -----------------------------------------------------------------

    [Fact]
    public void PchHeaderFile_With_ParentTraversal_Rejects_With_Exit30()
    {
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            pch_header_file = "../../../etc/passwd"
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildTomlParser.Parse(toml));
        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("pch_header_file", ex.Message);
        Assert.Contains("..", ex.Message);
    }

    [Fact]
    public void PchHeaderFile_With_AbsolutePath_Rejects_With_Exit30()
    {
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            pch_header_file = "/Engine/Source/Other/Other.h"
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildTomlParser.Parse(toml));
        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("pch_header_file", ex.Message);
        Assert.Contains("absolute", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PchHeaderFile_WithRelativePath_Accepts()
    {
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            pch_header_file = "Public/X.h"
            """;

        ModuleRules rules = BuildTomlParser.Parse(toml);
        Assert.Equal("Public/X.h", rules.PrivatePCHHeaderFile);
    }

    [Fact]
    public void SharedPchHeaderFile_With_AbsolutePath_Rejects_With_Exit30()
    {
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            shared_pch_header_file = "/Engine/Source/Other/Other.h"
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildTomlParser.Parse(toml));
        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("shared_pch_header_file", ex.Message);
        Assert.Contains("absolute", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------
    // shared_pch_header_file -- UE-style include-path resolution
    // (Phase 1.4c). The field accepts two forms:
    //   (a) BARE NAME (no path separator) -- skips path-traversal
    //       validation; the BuildMode grouping pass resolves it via
    //       every module's PublicIncludePaths.
    //   (b) RELATIVE PATH (contains `/` or `\`) -- subject to the
    //       existing path-traversal + absolute-path checks.
    // -----------------------------------------------------------------

    [Fact]
    public void SharedPchHeaderFile_BareName_Accepts()
    {
        // Bare-name form: no path separator. The parser accepts the
        // raw string; the BuildMode resolver looks it up at grouping
        // time.
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            shared_pch_header_file = "EngineCommon.h"
            """;

        ModuleRules rules = BuildTomlParser.Parse(toml);
        Assert.Equal("EngineCommon.h", rules.SharedPCHHeaderFile);
    }

    [Fact]
    public void SharedPchHeaderFile_RelativePath_Accepts()
    {
        // Relative-path form with no `..` -- accepted by the
        // path-traversal validator.
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            shared_pch_header_file = "Public/EngineCommon.h"
            """;

        ModuleRules rules = BuildTomlParser.Parse(toml);
        Assert.Equal("Public/EngineCommon.h", rules.SharedPCHHeaderFile);
    }

    [Fact]
    public void SharedPchHeaderFile_With_ParentTraversal_Rejects_With_Exit30()
    {
        // Relative-path form with `..` -- rejected by the path-traversal
        // validator (the separator triggers the validation branch).
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            shared_pch_header_file = "../EngineCommon.h"
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildTomlParser.Parse(toml));
        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("shared_pch_header_file", ex.Message);
        Assert.Contains("..", ex.Message);
    }

    [Fact]
    public void SharedPchHeaderFile_WhitespaceOnly_Rejects_With_Exit30()
    {
        // Whitespace-only field is a typo; reject at parse to surface
        // the error early (vs. silently dropping into the resolver).
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            shared_pch_header_file = "   "
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildTomlParser.Parse(toml));
        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("shared_pch_header_file", ex.Message);
        Assert.Contains("whitespace", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublicIncludePaths_With_ParentTraversal_Rejects_With_Exit30()
    {
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            public_include_paths = ["Public", "../OtherModule/Public"]
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildTomlParser.Parse(toml));
        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("public_include_paths", ex.Message);
        Assert.Contains("..", ex.Message);
    }

    [Fact]
    public void PublicIncludePaths_With_AbsolutePath_Rejects_With_Exit30()
    {
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            public_include_paths = ["/usr/include/foo"]
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildTomlParser.Parse(toml));
        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("public_include_paths", ex.Message);
        Assert.Contains("absolute", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublicIncludePaths_AllRelative_Accepts()
    {
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            public_include_paths = ["Public", "Public/Sub"]
            """;

        ModuleRules rules = BuildTomlParser.Parse(toml);
        Assert.Equal(new[] { "Public", "Public/Sub" }, rules.PublicIncludePaths);
    }

    [Fact]
    public void PrivateIncludePaths_With_BackslashParentTraversal_Rejects()
    {
        // Windows-style separator: the validator must catch `..`
        // regardless of slash direction.
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            private_include_paths = ["Private\\..\\..\\OtherModule"]
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildTomlParser.Parse(toml));
        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("private_include_paths", ex.Message);
        Assert.Contains("..", ex.Message);
    }

    /// <summary>
    /// Audit fix R4-M5: <c>engine_version_compat</c> is a recognized
    /// top-level field. Previously the parser rejected it with exit 30
    /// (unknown key); only the Roslyn <c>.Build.cs</c> escape hatch
    /// could set <c>ModuleRules.EngineVersionCompat</c>. The TOML
    /// surface now accepts the field at parse time and surfaces the
    /// value on the POCO.
    /// </summary>
    [Fact]
    public void EngineVersionCompat_Parses_From_Toml()
    {
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            engine_version_compat = "^0.1"
            """;

        ModuleRules rules = BuildTomlParser.Parse(toml);
        Assert.Equal("^0.1", rules.EngineVersionCompat);
    }

    /// <summary>
    /// Audit fix R4-M5: when <c>engine_version_compat</c> is absent the
    /// POCO default ("*" = any-version-compatible) applies. This is the
    /// safe default that does not constrain consumers.
    /// </summary>
    [Fact]
    public void EngineVersionCompat_Absent_Defaults_To_Star()
    {
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            """;

        ModuleRules rules = BuildTomlParser.Parse(toml);
        Assert.Equal("*", rules.EngineVersionCompat);
    }

    /// <summary>
    /// Audit fix R4-M5: round-trip <c>engine_version_compat</c> through
    /// the serializer + parser pair. Serialize emits the field only when
    /// non-default; parse recovers the value.
    /// </summary>
    [Fact]
    public void EngineVersionCompat_Roundtrips_NonDefault()
    {
        ModuleRules input = new()
        {
            Name = "X",
            Tier = ModuleTier.Engine,
            ModuleType = ModuleType.Runtime,
            EngineVersionCompat = "^0.1",
        };

        string toml = BuildTomlSerializer.Serialize(input);
        Assert.Contains("engine_version_compat = \"^0.1\"", toml);

        ModuleRules output = BuildTomlParser.Parse(toml);
        Assert.Equal("^0.1", output.EngineVersionCompat);
    }
}
