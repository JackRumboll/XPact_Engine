// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Manifest;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Configuration;

/// <summary>
/// Exercises the integration between <see cref="BuildTomlParser"/> and
/// the sandboxed <see cref="StarlarkEvaluator"/> through the
/// <c>@expr:&lt;identifier&gt;</c> sentinel pattern. Per Toolchain
/// Contract Rev 13 Section 9.6 / <c>/Documents/XBT.html</c> Rev 4
/// Section 3.3.
/// </summary>
/// <remarks>
/// <para>
/// Tests primarily exercise the in-process <see cref="BuildTomlParser.Parse"/>
/// overload that accepts a pre-loaded expression dictionary so the
/// fixtures don't have to touch disk; the on-disk path
/// (<see cref="BuildTomlParser.ParseFile(string, TargetRules?)"/>) is
/// exercised by the orphan-detection test and by
/// <see cref="ResolveExpr_FromActualSidecar_File_Path_Works"/> which
/// pairs a real <c>.Build.toml</c> with a real <c>.Build.expr</c> on
/// disk.
/// </para>
/// </remarks>
public sealed class BuildExprIntegrationTests : IDisposable
{
    private readonly string _scratchDir;

    public BuildExprIntegrationTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.BuildExprIntegration",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratchDir))
            {
                Directory.Delete(_scratchDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort.
        }
    }

    private static TargetRules StandardTarget(
        Platform platform = Platform.Win64,
        BuildConfiguration config = BuildConfiguration.Development,
        StationRole stationRole = StationRole.Engineer,
        bool fipsMode = false,
        string architecture = "x86_64")
        => new()
        {
            Name = "TestTarget",
            TargetType = BuildTargetType.Editor,
            Configuration = config,
            Platform = platform,
            StationRole = stationRole,
            FipsMode = fipsMode,
            Architecture = architecture,
        };

    // -----------------------------------------------------------------
    // 1. .Build.toml parses correctly with NO .Build.expr present.
    // -----------------------------------------------------------------

    [Fact]
    public void NoBuildExpr_BehaviourUnchanged()
    {
        // The original test-suite behaviour: a plain TOML with no
        // expressions is parsed identically whether or not a target
        // is supplied. The substitution pass should be a no-op when
        // the TOML has no @expr refs.
        const string toml = """
            name = "XCore"
            tier = "Engine"
            module_type = "Runtime"
            public_definitions = ["XCORE_API=DLLEXPORT", "XCORE_INTERNAL"]
            """;

        // Without target.
        ModuleRules withoutTarget = BuildTomlParser.Parse(toml);
        Assert.Equal("XCore", withoutTarget.Name);
        Assert.Equal(2, withoutTarget.PublicDefinitions.Count);

        // With target but no expressions.
        ModuleRules withTarget = BuildTomlParser.Parse(
            toml, sourcePath: null, defaultModuleName: null, target: StandardTarget(), expressions: null);
        Assert.Equal("XCore", withTarget.Name);
        Assert.Equal(2, withTarget.PublicDefinitions.Count);
        Assert.Equal(withoutTarget.PublicDefinitions, withTarget.PublicDefinitions);
    }

    // -----------------------------------------------------------------
    // 2. @expr ref with no .Build.expr file -> exit 30, message names the file.
    // -----------------------------------------------------------------

    [Fact]
    public void ExprRef_WithoutExprDictionary_FailsWithExit30()
    {
        const string toml = """
            name = "XCore"
            tier = "Engine"
            module_type = "Runtime"
            short_name = "@expr:choose_short_name"
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(() =>
            BuildTomlParser.Parse(
                toml,
                sourcePath: "TestModule.Build.toml",
                defaultModuleName: "XCore",
                target: StandardTarget(),
                expressions: null));

        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("@expr:choose_short_name", ex.Message);
        Assert.Contains("no .Build.expr", ex.Message);
        // The diagnostic surfaces the TOML file path.
        Assert.Contains("TestModule.Build.toml", ex.Message);
    }

    // -----------------------------------------------------------------
    // 3. @expr:<undefined> with .Build.expr lacking that name -> exit 30.
    // -----------------------------------------------------------------

    [Fact]
    public void ExprRef_UndefinedIdentifier_FailsWithExit30()
    {
        const string toml = """
            name = "XCore"
            tier = "Engine"
            module_type = "Runtime"
            short_name = "@expr:missing_name"
            """;

        Dictionary<string, string> expressions = new(StringComparer.Ordinal)
        {
            // Defined: 'present_name'. Referenced: 'missing_name'.
            ["present_name"] = "\"Whatever\"",
        };

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(() =>
            BuildTomlParser.Parse(
                toml,
                sourcePath: "TestModule.Build.toml",
                defaultModuleName: "XCore",
                target: StandardTarget(),
                expressions: expressions));

        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("missing_name", ex.Message);
        Assert.Contains("not defined", ex.Message);
    }

    // -----------------------------------------------------------------
    // 4. Simple string substitution.
    // -----------------------------------------------------------------

    [Fact]
    public void ExprRef_StringField_Substitutes_LiteralResult()
    {
        const string toml = """
            name = "XCore"
            tier = "Engine"
            module_type = "Runtime"
            short_name = "@expr:simple"
            """;

        Dictionary<string, string> expressions = new(StringComparer.Ordinal)
        {
            ["simple"] = "'FixedString'",
        };

        ModuleRules rules = BuildTomlParser.Parse(
            toml,
            sourcePath: null,
            defaultModuleName: null,
            target: StandardTarget(),
            expressions: expressions);

        Assert.Equal("FixedString", rules.ShortName);
    }

    // -----------------------------------------------------------------
    // 5. Conditional expression resolves differently per platform.
    // -----------------------------------------------------------------

    [Fact]
    public void ExprRef_ConditionalOnPlatform_BranchesCorrectly()
    {
        const string toml = """
            name = "XCore"
            tier = "Engine"
            module_type = "Runtime"
            short_name = "@expr:conditional"
            """;

        Dictionary<string, string> expressions = new(StringComparer.Ordinal)
        {
            ["conditional"] = "'WinShortName' if target.platform == 'Win64' else 'NixShortName'",
        };

        ModuleRules win = BuildTomlParser.Parse(
            toml, null, null, StandardTarget(platform: Platform.Win64), expressions);
        Assert.Equal("WinShortName", win.ShortName);

        ModuleRules linux = BuildTomlParser.Parse(
            toml, null, null, StandardTarget(platform: Platform.Linux), expressions);
        Assert.Equal("NixShortName", linux.ShortName);
    }

    // -----------------------------------------------------------------
    // 6. List<string> field whose @expr returns a 3-element list -> spliced.
    // -----------------------------------------------------------------

    [Fact]
    public void ExprRef_ListField_ScalarRef_ReturningList_Splices()
    {
        // The @expr is used as the entire VALUE of public_definitions
        // (a scalar TOML string at a list-typed field). Result must be
        // a list of three strings.
        const string toml = """
            name = "XCore"
            tier = "Engine"
            module_type = "Runtime"
            public_definitions = "@expr:windows_defs"
            """;

        Dictionary<string, string> expressions = new(StringComparer.Ordinal)
        {
            ["windows_defs"] = "['X_PLAT_WIN=1', 'X_HAS_WINSDK=1', 'X_DESKTOP=1']",
        };

        ModuleRules rules = BuildTomlParser.Parse(
            toml, null, null, StandardTarget(), expressions);

        Assert.Equal(3, rules.PublicDefinitions.Count);
        Assert.Equal("X_PLAT_WIN=1", rules.PublicDefinitions[0]);
        Assert.Equal("X_HAS_WINSDK=1", rules.PublicDefinitions[1]);
        Assert.Equal("X_DESKTOP=1", rules.PublicDefinitions[2]);
    }

    // -----------------------------------------------------------------
    // 7. List<string> field, element-level @expr returning a single string
    //    -> 1:1 replacement.
    // -----------------------------------------------------------------

    [Fact]
    public void ExprRef_ListField_ElementRef_ReturningString_ReplacesOneToOne()
    {
        const string toml = """
            name = "XCore"
            tier = "Engine"
            module_type = "Runtime"
            public_definitions = ["X_STATIC=1", "@expr:platform_define", "X_FOOTER=1"]
            """;

        Dictionary<string, string> expressions = new(StringComparer.Ordinal)
        {
            ["platform_define"] = "'X_PLATFORM_WIN=1' if target.platform == 'Win64' else 'X_PLATFORM_NIX=1'",
        };

        ModuleRules rules = BuildTomlParser.Parse(
            toml, null, null, StandardTarget(platform: Platform.Win64), expressions);

        // The list keeps 3 elements; the middle one is the substituted one.
        Assert.Equal(3, rules.PublicDefinitions.Count);
        Assert.Equal("X_STATIC=1", rules.PublicDefinitions[0]);
        Assert.Equal("X_PLATFORM_WIN=1", rules.PublicDefinitions[1]);
        Assert.Equal("X_FOOTER=1", rules.PublicDefinitions[2]);
    }

    // -----------------------------------------------------------------
    // 8. List<string> with @expr returning a non-string-list type -> mismatch.
    // -----------------------------------------------------------------

    [Fact]
    public void ExprRef_ListElement_ReturningBool_TypeMismatch_Fails()
    {
        const string toml = """
            name = "XCore"
            tier = "Engine"
            module_type = "Runtime"
            public_definitions = ["@expr:wrong_type"]
            """;

        Dictionary<string, string> expressions = new(StringComparer.Ordinal)
        {
            ["wrong_type"] = "True",
        };

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(() =>
            BuildTomlParser.Parse(toml, null, null, StandardTarget(), expressions));

        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("Type mismatch", ex.Message);
        Assert.Contains("@expr:wrong_type", ex.Message);
        Assert.Contains("boolean", ex.Message);
    }

    // -----------------------------------------------------------------
    // 9. String field with @expr returning a list -> mismatch.
    // -----------------------------------------------------------------

    [Fact]
    public void ExprRef_StringField_ReturningList_TypeMismatch_Fails()
    {
        const string toml = """
            name = "XCore"
            tier = "Engine"
            module_type = "Runtime"
            short_name = "@expr:returns_list"
            """;

        Dictionary<string, string> expressions = new(StringComparer.Ordinal)
        {
            ["returns_list"] = "['a', 'b']",
        };

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(() =>
            BuildTomlParser.Parse(toml, null, null, StandardTarget(), expressions));

        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("Type mismatch", ex.Message);
        Assert.Contains("@expr:returns_list", ex.Message);
        Assert.Contains("short_name", ex.Message);
        Assert.Contains("string", ex.Message);
    }

    // -----------------------------------------------------------------
    // 10. .Build.expr with an unused name -> warning, build still succeeds.
    // -----------------------------------------------------------------

    [Fact]
    public void UnusedExpressionName_Warns_DoesNotFail()
    {
        const string toml = """
            name = "XCore"
            tier = "Engine"
            module_type = "Runtime"
            short_name = "@expr:used_name"
            """;

        Dictionary<string, string> expressions = new(StringComparer.Ordinal)
        {
            ["used_name"] = "'WasUsed'",
            ["unused_name"] = "'NeverReferenced'",
        };

        // Should NOT throw. The unused-expression warning is emitted
        // via Logger.Warning; verifying the warning text itself
        // requires hooking the JSON channel which is overkill for this
        // test -- the absence of an exception confirms the spec's
        // "warn but don't fail" behaviour.
        ModuleRules rules = BuildTomlParser.Parse(
            toml, null, null, StandardTarget(), expressions);
        Assert.Equal("WasUsed", rules.ShortName);
    }

    // -----------------------------------------------------------------
    // 11. Orphan .Build.expr (no .Build.toml in dir) -> exit 30.
    // -----------------------------------------------------------------

    [Fact]
    public void OrphanBuildExpr_NoMatchingToml_Throws()
    {
        // Set up a fixture directory with ONLY a .Build.expr (no .toml).
        string moduleDir = Path.Combine(_scratchDir, "Orphan");
        Directory.CreateDirectory(moduleDir);
        File.WriteAllText(
            Path.Combine(moduleDir, "Orphan.Build.expr"),
            "lonely = \"True\"\n");

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(() =>
            BuildTomlParser.ValidateNoOrphanBuildExpr(moduleDir));

        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("Orphan .Build.expr", ex.Message);
        Assert.Contains("Orphan.Build.expr", ex.Message);
    }

    // -----------------------------------------------------------------
    // 12. Invalid expression identifier (regex fail) -> exit 30.
    // -----------------------------------------------------------------

    [Fact]
    public void InvalidExpressionIdentifier_FailsWithExit30()
    {
        // "1bad_start" violates the [A-Za-z_] first-char rule.
        const string exprToml = """
            "1bad_start" = "True"
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(() =>
            BuildExprFile.Parse(exprToml, sourcePath: "TestModule.Build.expr"));

        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("Invalid expression name", ex.Message);
        Assert.Contains("1bad_start", ex.Message);
    }

    // -----------------------------------------------------------------
    // Additional coverage -- end-to-end through ParseFile so the on-disk
    // sidecar lookup logic (LocateBuildExpr) is exercised too.
    // -----------------------------------------------------------------

    [Fact]
    public void ResolveExpr_FromActualSidecar_File_Path_Works()
    {
        // Write a real .Build.toml + .Build.expr pair to disk and call
        // the file-based ParseFile overload. Confirms the file lookup
        // (LocateBuildExpr) picks up the sibling expr file with the
        // matching stem.
        string moduleDir = Path.Combine(_scratchDir, "XSidecar");
        Directory.CreateDirectory(moduleDir);

        string tomlPath = Path.Combine(moduleDir, "XSidecar.Build.toml");
        string exprPath = Path.Combine(moduleDir, "XSidecar.Build.expr");

        File.WriteAllText(
            tomlPath,
            """
            name = "XSidecar"
            tier = "Engine"
            module_type = "Runtime"
            short_name = "@expr:choose_short"
            """);
        File.WriteAllText(
            exprPath,
            """
            choose_short = "'XS' if target.platform == 'Win64' else 'XSL'"
            """);

        ModuleRules rules = BuildTomlParser.ParseFile(
            tomlPath, target: StandardTarget(platform: Platform.Win64));
        Assert.Equal("XS", rules.ShortName);

        ModuleRules rulesLinux = BuildTomlParser.ParseFile(
            tomlPath, target: StandardTarget(platform: Platform.Linux));
        Assert.Equal("XSL", rulesLinux.ShortName);
    }

    [Fact]
    public void ParseFile_NoTarget_LeavesExprRefAsLiteralString()
    {
        // When no target is supplied, the @expr substitution pass is
        // skipped entirely. The TOML value "@expr:foo" comes through
        // verbatim as a string field. This protects every existing test
        // / call site that pre-dates the @expr feature.
        string moduleDir = Path.Combine(_scratchDir, "XLiteral");
        Directory.CreateDirectory(moduleDir);
        string tomlPath = Path.Combine(moduleDir, "XLiteral.Build.toml");
        File.WriteAllText(
            tomlPath,
            """
            name = "XLiteral"
            tier = "Engine"
            module_type = "Runtime"
            short_name = "@expr:foo"
            """);

        // Pass target = null. No exception, value is literal.
        ModuleRules rules = BuildTomlParser.ParseFile(tomlPath, target: null);
        Assert.Equal("@expr:foo", rules.ShortName);
    }
}
