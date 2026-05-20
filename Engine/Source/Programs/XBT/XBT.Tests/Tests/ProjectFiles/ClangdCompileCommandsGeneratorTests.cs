// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Discovery;
using Simgenics.XPact.XBT.Manifest;
using Simgenics.XPact.XBT.ProjectFiles;
using Simgenics.XPact.XBT.Toolchain;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.ProjectFiles;

/// <summary>
/// Verifies <see cref="ClangdCompileCommandsGenerator"/> emits a
/// well-formed <c>compile_commands.json</c> per the LLVM JSON
/// Compilation Database spec, with the documented MSVC -&gt; Clang
/// flag translation and deterministic output. Per
/// <c>/Documents/XBT.html</c> Rev 4 Section 14.3.
/// </summary>
public sealed class ClangdCompileCommandsGeneratorTests : IDisposable
{
    private readonly string _engineRoot;
    private readonly XMSVCToolChain _msvcToolchain;
    private readonly XClangToolChain _clangToolchain;

    public ClangdCompileCommandsGeneratorTests()
    {
        _engineRoot = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.Clangd",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_engineRoot);

        VCEnvironment env = VCEnvironment.ForTesting(
            vsInstallDir: @"C:\FakeVS",
            compilerPath: @"C:\FakeVS\cl.exe",
            linkerPath: @"C:\FakeVS\link.exe");
        _msvcToolchain = new XMSVCToolChain(env, repoRoot: _engineRoot);
        _clangToolchain = new XClangToolChain(
            clangPath: "/usr/bin/clang++",
            clangVersion: "18.0.0",
            platform: Platform.Linux,
            repoRoot: _engineRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_engineRoot))
            {
                Directory.Delete(_engineRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    // ----------------------------------------------------------------------
    // 1. Empty target -> empty JSON array `[]`.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task EmptyTarget_EmitsEmptyJsonArray()
    {
        ClangdCompileCommandsGenerator gen = new();
        GenerationContext ctx = MakeContext(modules: new List<ModuleRecord>());

        GenerationResult result = await gen.GenerateAsync(ctx, CancellationToken.None);
        Assert.True(result.Succeeded, result.ErrorMessage);

        string emitted = File.ReadAllText(Path.Combine(_engineRoot, ClangdCompileCommandsGenerator.OutputFileName));
        // The JSON array `[]` may have a trailing newline; trim and check.
        Assert.Equal("[]", emitted.Trim());
    }

    // ----------------------------------------------------------------------
    // 2. Target with one module + one source file -> one JSON entry.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task SingleSource_EmitsOneEntry()
    {
        ModuleRecord rec = CreateModuleWithCppSources(
            moduleName: "XScoring",
            sources: new[] { "XScoring.cpp" });

        ClangdCompileCommandsGenerator gen = new();
        GenerationContext ctx = MakeContext(
            modules: new List<ModuleRecord> { rec },
            toolchain: _clangToolchain);

        GenerationResult result = await gen.GenerateAsync(ctx, CancellationToken.None);
        Assert.True(result.Succeeded, result.ErrorMessage);

        List<JsonElement> entries = ParseOutputAsArray();
        Assert.Single(entries);

        JsonElement entry = entries[0];
        Assert.True(entry.TryGetProperty("directory", out JsonElement dir));
        Assert.True(entry.TryGetProperty("command", out JsonElement cmd));
        Assert.True(entry.TryGetProperty("file", out JsonElement file));

        // Directory must be the engine root (with forward slashes).
        Assert.Equal(_engineRoot.Replace('\\', '/'), dir.GetString());

        // File must end with XScoring.cpp.
        Assert.EndsWith("XScoring.cpp", file.GetString());

        // Command must contain the source file path.
        Assert.Contains("XScoring.cpp", cmd.GetString());
    }

    // ----------------------------------------------------------------------
    // 3. Two source files -> two entries, sorted alphabetically.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task TwoSources_EmitsTwoEntriesAlphabeticallySorted()
    {
        ModuleRecord rec = CreateModuleWithCppSources(
            moduleName: "XCore",
            sources: new[] { "ZBeta.cpp", "AAlpha.cpp" });

        ClangdCompileCommandsGenerator gen = new();
        GenerationContext ctx = MakeContext(
            modules: new List<ModuleRecord> { rec },
            toolchain: _clangToolchain);

        GenerationResult result = await gen.GenerateAsync(ctx, CancellationToken.None);
        Assert.True(result.Succeeded, result.ErrorMessage);

        List<JsonElement> entries = ParseOutputAsArray();
        Assert.Equal(2, entries.Count);

        // Ordinal sort: AAlpha < ZBeta.
        string firstFile = entries[0].GetProperty("file").GetString()!;
        string secondFile = entries[1].GetProperty("file").GetString()!;
        Assert.EndsWith("AAlpha.cpp", firstFile);
        Assert.EndsWith("ZBeta.cpp", secondFile);
        Assert.True(string.CompareOrdinal(firstFile, secondFile) < 0);
    }

    // ----------------------------------------------------------------------
    // 4. MSVC /DFOO=1 -> -DFOO=1 translation.
    // ----------------------------------------------------------------------

    [Fact]
    public void TranslateMsvcFlag_DefineWithValue_ProducesClangEquivalent()
    {
        Assert.Equal("-DFOO=1", ClangdCompileCommandsGenerator.TranslateMsvcFlag("/DFOO=1"));
        Assert.Equal("-DSIM_PATH", ClangdCompileCommandsGenerator.TranslateMsvcFlag("/DSIM_PATH"));
        Assert.Equal("-DXPACT_STATION_ROLE=Engineer", ClangdCompileCommandsGenerator.TranslateMsvcFlag("/DXPACT_STATION_ROLE=Engineer"));
    }

    [Fact]
    public async Task SingleSource_MsvcToolchain_TranslatesDefineFlag()
    {
        // Build a module that contributes a /DFOO definition, then run
        // the MSVC-targeted toolchain through the generator to confirm
        // the emitted command uses Clang's -D form.
        ModuleRules rules = NewModuleRules("XScoring");
        rules.PrivateDefinitions.Add("FOO=1");

        string moduleDir = Path.Combine(_engineRoot, "Engine", "Source", "Runtime", "XScoring");
        Directory.CreateDirectory(moduleDir);
        string descPath = Path.Combine(moduleDir, "XScoring.Build.toml");
        File.WriteAllText(descPath, "# stub");
        string srcPath = Path.Combine(moduleDir, "Private", "XScoring.cpp");
        Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);
        File.WriteAllText(srcPath, "// src");

        ModuleRecord rec = new(rules, descPath, IoHash.Compute(System.Text.Encoding.UTF8.GetBytes("desc")), OwningPluginName: null);

        ClangdCompileCommandsGenerator gen = new();
        GenerationContext ctx = MakeContext(
            modules: new List<ModuleRecord> { rec },
            toolchain: _msvcToolchain);

        GenerationResult result = await gen.GenerateAsync(ctx, CancellationToken.None);
        Assert.True(result.Succeeded, result.ErrorMessage);

        List<JsonElement> entries = ParseOutputAsArray();
        Assert.Single(entries);
        string cmd = entries[0].GetProperty("command").GetString()!;
        // The MSVC toolchain emits /DFOO=1; the clangd generator
        // translates to -DFOO=1.
        Assert.Contains("-DFOO=1", cmd);
        Assert.DoesNotContain("/DFOO=1", cmd);
    }

    // ----------------------------------------------------------------------
    // 5. MSVC /I include\path -> -I include/path translation
    //    (forward-slash path normalisation for portability).
    // ----------------------------------------------------------------------

    [Fact]
    public void TranslateMsvcFlag_IncludePath_ProducesClangEquivalentWithForwardSlashes()
    {
        Assert.Equal("-Iinclude/path", ClangdCompileCommandsGenerator.TranslateMsvcFlag(@"/Iinclude\path"));
        Assert.Equal("-IC:/repo/Engine/Source/Runtime/XCore/Public",
            ClangdCompileCommandsGenerator.TranslateMsvcFlag(@"/IC:\repo\Engine\Source\Runtime\XCore\Public"));
    }

    [Fact]
    public async Task SingleSource_MsvcToolchain_TranslatesIncludePath()
    {
        ModuleRules rules = NewModuleRules("XScoring");
        rules.PublicIncludePaths.Add(@"Engine\Source\Runtime\XCore\Public");

        string moduleDir = Path.Combine(_engineRoot, "Engine", "Source", "Runtime", "XScoring");
        Directory.CreateDirectory(moduleDir);
        string descPath = Path.Combine(moduleDir, "XScoring.Build.toml");
        File.WriteAllText(descPath, "# stub");
        string srcPath = Path.Combine(moduleDir, "Private", "XScoring.cpp");
        Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);
        File.WriteAllText(srcPath, "// src");

        ModuleRecord rec = new(rules, descPath, IoHash.Compute(System.Text.Encoding.UTF8.GetBytes("desc")), OwningPluginName: null);

        ClangdCompileCommandsGenerator gen = new();
        GenerationContext ctx = MakeContext(
            modules: new List<ModuleRecord> { rec },
            toolchain: _msvcToolchain);

        GenerationResult result = await gen.GenerateAsync(ctx, CancellationToken.None);
        Assert.True(result.Succeeded, result.ErrorMessage);

        List<JsonElement> entries = ParseOutputAsArray();
        string cmd = entries[0].GetProperty("command").GetString()!;
        Assert.Contains("-IEngine/Source/Runtime/XCore/Public", cmd);
        Assert.DoesNotContain(@"/IEngine\Source", cmd);
    }

    // ----------------------------------------------------------------------
    // 6. SimPath module on the Clang toolchain emits the canonical
    //    SimPath flag set (-ffp-contract=off, -mno-fma, etc.).
    // ----------------------------------------------------------------------

    [Fact]
    public async Task SimPathModule_ClangToolchain_EmitsSimPathDeterminismFlags()
    {
        ModuleRules rules = new()
        {
            Name = "XPhysics",
            Tier = ModuleTier.Engine,
            ModuleType = ModuleType.Runtime,
            Languages = Languages.Cpp,
            SimPath = true,
            FPSemantics = FPSemantics.Precise,
        };

        string moduleDir = Path.Combine(_engineRoot, "Engine", "Source", "Runtime", "XPhysics");
        Directory.CreateDirectory(moduleDir);
        string descPath = Path.Combine(moduleDir, "XPhysics.Build.toml");
        File.WriteAllText(descPath, "# stub");
        string srcPath = Path.Combine(moduleDir, "Private", "XPhysics.cpp");
        Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);
        File.WriteAllText(srcPath, "// src");

        ModuleRecord rec = new(rules, descPath, IoHash.Compute(System.Text.Encoding.UTF8.GetBytes("desc")), OwningPluginName: null);

        ClangdCompileCommandsGenerator gen = new();
        GenerationContext ctx = MakeContext(
            modules: new List<ModuleRecord> { rec },
            toolchain: _clangToolchain);

        GenerationResult result = await gen.GenerateAsync(ctx, CancellationToken.None);
        Assert.True(result.Succeeded, result.ErrorMessage);

        List<JsonElement> entries = ParseOutputAsArray();
        string cmd = entries[0].GetProperty("command").GetString()!;
        Assert.Contains("-ffp-contract=off", cmd);
        Assert.Contains("-mno-fma", cmd);
        // Banned: -ffast-math must never appear on a SimPath module.
        Assert.DoesNotContain("-ffast-math", cmd);
    }

    // ----------------------------------------------------------------------
    // 7. Determinism: re-running the generator yields byte-identical
    //    output. (Bonus assertion beyond the documented six.)
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ReRun_ProducesByteIdenticalOutput()
    {
        ModuleRecord rec = CreateModuleWithCppSources(
            moduleName: "XCore",
            sources: new[] { "Beta.cpp", "Alpha.cpp" });

        ClangdCompileCommandsGenerator gen = new();
        GenerationContext ctx = MakeContext(
            modules: new List<ModuleRecord> { rec },
            toolchain: _clangToolchain);

        await gen.GenerateAsync(ctx, CancellationToken.None);
        byte[] first = File.ReadAllBytes(Path.Combine(_engineRoot, ClangdCompileCommandsGenerator.OutputFileName));

        await gen.GenerateAsync(ctx, CancellationToken.None);
        byte[] second = File.ReadAllBytes(Path.Combine(_engineRoot, ClangdCompileCommandsGenerator.OutputFileName));

        Assert.Equal(first, second);
    }

    // ----------------------------------------------------------------------
    // Helpers.
    // ----------------------------------------------------------------------

    private GenerationContext MakeContext(
        List<ModuleRecord> modules,
        XToolChain? toolchain = null)
    {
        return new GenerationContext(
            EngineRoot: _engineRoot,
            StudioRoot: null,
            ProjectRoot: null,
            Target: new TargetRules
            {
                Name = "EditorTarget",
                TargetType = BuildTargetType.Editor,
                Platform = (toolchain ?? _clangToolchain).Platform,
                Configuration = BuildConfiguration.Development,
                StationRole = StationRole.Engineer,
            },
            Modules: modules,
            Plugins: Array.Empty<PluginEntry>(),
            ToolChain: toolchain ?? _clangToolchain,
            LogChannel: "Test");
    }

    private ModuleRecord CreateModuleWithCppSources(string moduleName, string[] sources)
    {
        string moduleDir = Path.Combine(_engineRoot, "Engine", "Source", "Runtime", moduleName);
        Directory.CreateDirectory(moduleDir);
        string descPath = Path.Combine(moduleDir, moduleName + ".Build.toml");
        File.WriteAllText(descPath, "# stub");

        foreach (string src in sources)
        {
            string srcPath = Path.Combine(moduleDir, "Private", src);
            Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);
            File.WriteAllText(srcPath, "// stub source");
        }

        ModuleRules rules = NewModuleRules(moduleName);
        IoHash hash = IoHash.Compute(System.Text.Encoding.UTF8.GetBytes("descriptor-stub"));
        return new ModuleRecord(rules, descPath, hash, OwningPluginName: null);
    }

    private static ModuleRules NewModuleRules(string name)
    {
        return new ModuleRules
        {
            Name = name,
            Tier = ModuleTier.Engine,
            ModuleType = ModuleType.Runtime,
            Languages = Languages.Cpp,
        };
    }

    private List<JsonElement> ParseOutputAsArray()
    {
        string emitted = File.ReadAllText(Path.Combine(_engineRoot, ClangdCompileCommandsGenerator.OutputFileName));
        JsonDocument doc = JsonDocument.Parse(emitted);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        return doc.RootElement.EnumerateArray().ToList();
    }
}
