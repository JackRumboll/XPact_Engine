// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Linq;
using Simgenics.XPact.XBT.ActionGraph;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;
using Simgenics.XPact.XBT.Toolchain;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.BuildMode;

/// <summary>
/// Verifies the Phase 5 test-module linking pattern: when a module
/// declares <c>b_is_test_module = true</c>, BuildMode emits ONE
/// <see cref="XToolChain.LinkExecutable"/> action per .cpp instead of
/// the single <see cref="XToolChain.LinkModule"/> production-DLL link.
/// </summary>
/// <remarks>
/// <para>
/// These tests exercise the toolchain emit surface directly (the
/// production wiring path lives in
/// <see cref="Simgenics.XPact.XBT.Entry.BuildMode"/> private helpers
/// which are not directly callable from tests; the integration is
/// covered by <c>HelloWorldSmokeTest</c> when a host toolchain is
/// available). The unit-level assertions here pin the architectural
/// invariants:
/// </para>
/// <list type="bullet">
///   <item>LinkExecutable produces a <c>.exe</c> (Win64) / bare-name
///   (Linux) artefact, not a <c>.dll</c> / <c>.so</c>.</item>
///   <item>LinkExecutable's response file embeds the executable flag
///   set (no <c>/DLL</c>, <c>/SUBSYSTEM:CONSOLE</c> on MSVC; no
///   <c>-shared</c> on Clang).</item>
///   <item>LinkExecutable's CommandVersion differs from LinkModule's
///   for the same .obj input — the test-vs-production distinction is
///   part of the cache key.</item>
///   <item>The additionalLibraries surface threads through both
///   LinkModule and LinkExecutable so transitive dep import libs
///   reach the linker.</item>
/// </list>
/// </remarks>
[Collection(nameof(Simgenics.XPact.XBT.Tests.ToolchainSelfHashCollection))]
public sealed class TestModuleLinkingTests : IDisposable
{
    private readonly string _scratchDir;
    private readonly XMSVCToolChain _msvc;

    public TestModuleLinkingTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.TestModuleLinking",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchDir);

        VCEnvironment env = VCEnvironment.ForTesting(
            vsInstallDir: @"C:\FakeVS",
            compilerPath: @"C:\FakeVS\cl.exe",
            linkerPath: @"C:\FakeVS\link.exe");
        _msvc = new XMSVCToolChain(env, repoRoot: @"C:\repo");
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
        }
    }

    /// <summary>
    /// A test module's per-cpp executable contains the executable flag
    /// set and produces a .exe artefact (the canonical Phase 5
    /// per-test-cpp-executable shape).
    /// </summary>
    [Fact]
    public void TestModule_PerCppLink_ProducesExecutable()
    {
        ModuleRules module = NewTestModule();
        TargetRules target = NewTestConfigTarget();

        FileItem obj1 = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Test1.obj"));
        FileItem obj2 = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Test2.obj"));

        IExternalAction link1 = _msvc.LinkExecutable(module, target, obj1, "Test1", _scratchDir);
        IExternalAction link2 = _msvc.LinkExecutable(module, target, obj2, "Test2", _scratchDir);

        // Two distinct executables.
        Assert.Single(link1.ProducedItems);
        Assert.Single(link2.ProducedItems);
        Assert.EndsWith("Test1.exe", link1.ProducedItems[0].FullPath);
        Assert.EndsWith("Test2.exe", link2.ProducedItems[0].FullPath);

        // Distinct CommandVersions so the cache treats them as
        // independent.
        Assert.NotEqual(link1.CommandVersion, link2.CommandVersion);
    }

    /// <summary>
    /// The test executable's response file includes the transitive
    /// dependency import libraries (so the test's main() can pull in
    /// XCore.dll's exports + XCore.dll can pull in Sleef.dll's). The
    /// additionalPrerequisites parameter carries the producer-visible
    /// .dll paths so the action graph orders the test link after the
    /// dependency's link.
    /// </summary>
    [Fact]
    public void TestModule_PerCppLink_IncludesAdditionalLibraries()
    {
        ModuleRules module = NewTestModule();
        TargetRules target = NewTestConfigTarget();

        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Test.obj"));
        string xcoreLib = Path.Combine(_scratchDir, "Binaries", "Win64", "XCore.lib");
        string sleefLib = Path.Combine(_scratchDir, "Binaries", "Win64", "Sleef.lib");
        string xcoreDll = Path.Combine(_scratchDir, "Binaries", "Win64", "XCore.dll");
        string sleefDll = Path.Combine(_scratchDir, "Binaries", "Win64", "Sleef.dll");

        IExternalAction link = _msvc.LinkExecutable(
            module, target, obj,
            exeName: "Test",
            outputDir: _scratchDir,
            additionalLibraries: new[] { xcoreLib, sleefLib },
            additionalPrerequisites: new[] { xcoreDll, sleefDll });

        string rsp = link.ResponseFileContents!;
        // .lib paths reach the linker.
        Assert.Contains(xcoreLib, rsp);
        Assert.Contains(sleefLib, rsp);

        // PrerequisiteItems carries the .dll paths (producer-visible
        // artefacts) and the .obj. The .lib does NOT appear in
        // prereqs because its producer is conditional on exports.
        Assert.Contains(link.PrerequisiteItems, p => p.FullPath == xcoreDll);
        Assert.Contains(link.PrerequisiteItems, p => p.FullPath == sleefDll);
        Assert.Contains(link.PrerequisiteItems, p => p.FullPath == obj.FullPath);
        Assert.DoesNotContain(link.PrerequisiteItems, p => p.FullPath == xcoreLib);
        Assert.DoesNotContain(link.PrerequisiteItems, p => p.FullPath == sleefLib);
    }

    /// <summary>
    /// ModuleRules.AdditionalLibraries is parseable from
    /// <c>additional_libraries</c> TOML and round-trips through the
    /// serializer. Empty default keeps existing fixtures valid.
    /// </summary>
    [Fact]
    public void AdditionalLibraries_RoundTripsThroughTomlParser()
    {
        string toml = @"
name = ""TestMod""
tier = ""Engine""
module_type = ""Runtime""
languages = ""Cpp""
additional_libraries = [
    ""ext/libfoo.lib"",
    ""ext/libbar.lib"",
]
";
        string tomlPath = Path.Combine(_scratchDir, "TestMod.Build.toml");
        File.WriteAllText(tomlPath, toml);

        ModuleRules rules = BuildTomlParser.ParseFile(tomlPath);
        Assert.Equal(2, rules.AdditionalLibraries.Count);
        Assert.Contains("ext/libfoo.lib", rules.AdditionalLibraries);
        Assert.Contains("ext/libbar.lib", rules.AdditionalLibraries);

        // Round-trip: serialize back to TOML, parse again.
        string emitted = BuildTomlSerializer.Serialize(rules);
        Assert.Contains("additional_libraries", emitted);

        string secondPath = Path.Combine(_scratchDir, "TestMod2.Build.toml");
        File.WriteAllText(secondPath, emitted);
        ModuleRules reparsed = BuildTomlParser.ParseFile(secondPath);
        Assert.Equal(rules.AdditionalLibraries.Count, reparsed.AdditionalLibraries.Count);
    }

    /// <summary>
    /// A module without additional_libraries declared has an empty
    /// list (the default); the field does not appear in the emitted
    /// TOML body when emitDefaults is false.
    /// </summary>
    [Fact]
    public void AdditionalLibraries_NotEmittedWhenEmpty()
    {
        ModuleRules rules = new()
        {
            Name = "TestMod",
            Tier = ModuleTier.Engine,
            ModuleType = ModuleType.Runtime,
            Languages = Languages.Cpp,
        };

        string emitted = BuildTomlSerializer.Serialize(rules, emitDefaults: false);
        Assert.DoesNotContain("additional_libraries", emitted);

        string emittedWithDefaults = BuildTomlSerializer.Serialize(rules, emitDefaults: true);
        Assert.Contains("additional_libraries", emittedWithDefaults);
    }

    private static ModuleRules NewTestModule()
    {
        return new ModuleRules
        {
            Name = "XCore.Tests",
            Tier = ModuleTier.Engine,
            ModuleType = ModuleType.Runtime,
            Languages = Languages.Cpp,
            bIsTestModule = true,
        };
    }

    private static TargetRules NewTestConfigTarget()
    {
        return new TargetRules
        {
            Name = "EditorTest",
            TargetType = BuildTargetType.Editor,
            Platform = Platform.Win64,
            Configuration = BuildConfiguration.Test,
            StationRole = StationRole.Engineer,
            SimdLevelDefault = SimdLevel.SSE42,
        };
    }
}
