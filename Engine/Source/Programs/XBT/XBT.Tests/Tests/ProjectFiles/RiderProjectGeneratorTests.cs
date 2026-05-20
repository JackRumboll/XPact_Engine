// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Discovery;
using Simgenics.XPact.XBT.Manifest;
using Simgenics.XPact.XBT.ProjectFiles;
using Simgenics.XPact.XBT.Toolchain;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.ProjectFiles;

/// <summary>
/// Verifies <see cref="RiderProjectGenerator"/> emits a minimal valid
/// <c>.idea/</c> tree at the engine root per
/// <c>/Documents/XBT.html</c> Rev 4 Section 14.2. Tests:
/// <list type="number">
///   <item>modules.xml lists every module.</item>
///   <item>Each module gets a .iml with include paths preserved.</item>
///   <item>The XML is well-formed (parses cleanly).</item>
///   <item>Re-running produces byte-identical output (determinism).</item>
/// </list>
/// </summary>
public sealed class RiderProjectGeneratorTests : IDisposable
{
    private readonly string _engineRoot;
    private readonly XClangToolChain _toolchain;

    public RiderProjectGeneratorTests()
    {
        _engineRoot = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.Rider",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_engineRoot);

        _toolchain = new XClangToolChain(
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

    /// <summary>
    /// <c>.idea/modules.xml</c> lists every module via a
    /// <c>&lt;module&gt;</c> element with <c>fileurl</c> and
    /// <c>filepath</c> attributes referencing the per-module .iml.
    /// </summary>
    [Fact]
    public async Task ModulesXml_ListsEveryDiscoveredModule()
    {
        List<ModuleRecord> modules = new()
        {
            CreateModule("XCore"),
            CreateModule("XRender"),
            CreateModule("XScoring"),
        };

        RiderProjectGenerator gen = new();
        GenerationContext ctx = MakeContext(modules);

        GenerationResult result = await gen.GenerateAsync(ctx, CancellationToken.None);
        Assert.True(result.Succeeded, result.ErrorMessage);

        string modulesXml = File.ReadAllText(Path.Combine(_engineRoot, ".idea", "modules.xml"));
        XDocument doc = XDocument.Parse(modulesXml);

        // Locate <component name="ProjectModuleManager"> -> <modules>
        // -> <module ...> children.
        IEnumerable<XElement> moduleNodes = doc.Descendants("module")
            .Where(e => e.Attribute("filepath") is not null);
        List<string> filepaths = moduleNodes
            .Select(e => e.Attribute("filepath")!.Value)
            .ToList();
        Assert.Equal(3, filepaths.Count);
        Assert.Contains(filepaths, fp => fp.EndsWith("XCore.iml"));
        Assert.Contains(filepaths, fp => fp.EndsWith("XRender.iml"));
        Assert.Contains(filepaths, fp => fp.EndsWith("XScoring.iml"));
    }

    /// <summary>
    /// Each module gets a <c>.iml</c> file whose include-path metadata
    /// matches the <c>ModuleRules</c>'s declared include paths.
    /// </summary>
    [Fact]
    public async Task PerModule_Iml_PreservesIncludePaths()
    {
        ModuleRecord rec = CreateModule("XScoring");
        rec.Rules.PublicIncludePaths.Add("Engine/Source/Runtime/XScoring/Public");
        rec.Rules.PrivateIncludePaths.Add("Engine/Source/Runtime/XScoring/Private");
        rec.Rules.PrivateDefinitions.Add("XSCORING_INTERNAL=1");

        RiderProjectGenerator gen = new();
        GenerationContext ctx = MakeContext(new List<ModuleRecord> { rec });

        GenerationResult result = await gen.GenerateAsync(ctx, CancellationToken.None);
        Assert.True(result.Succeeded, result.ErrorMessage);

        string imlPath = Path.Combine(_engineRoot, ".idea", "XScoring.iml");
        Assert.True(File.Exists(imlPath), $"Expected {imlPath} to be written.");

        XDocument iml = XDocument.Load(imlPath);

        // Include paths under our custom <component name="XBT.ModuleInfo">.
        IEnumerable<XElement> includePaths = iml.Descendants("includePath");
        List<string> emittedIncludes = includePaths
            .Select(e => e.Attribute("path")!.Value)
            .ToList();
        Assert.Contains("Engine/Source/Runtime/XScoring/Public", emittedIncludes);
        Assert.Contains("Engine/Source/Runtime/XScoring/Private", emittedIncludes);

        // Definitions preserved.
        IEnumerable<XElement> defs = iml.Descendants("define");
        List<string> emittedDefs = defs
            .Select(e => e.Attribute("value")!.Value)
            .ToList();
        Assert.Contains("XSCORING_INTERNAL=1", emittedDefs);
    }

    /// <summary>
    /// Every emitted XML file parses cleanly via <see cref="XDocument.Load"/>.
    /// This is the well-formedness contract: a broken Rider .idea/ would
    /// kill the IDE's project view at load time.
    /// </summary>
    [Fact]
    public async Task EmittedXml_IsWellFormed()
    {
        List<ModuleRecord> modules = new()
        {
            CreateModule("XCore"),
            CreateModule("XScoring"),
        };

        RiderProjectGenerator gen = new();
        GenerationContext ctx = MakeContext(modules);
        GenerationResult result = await gen.GenerateAsync(ctx, CancellationToken.None);
        Assert.True(result.Succeeded, result.ErrorMessage);

        string ideaDir = Path.Combine(_engineRoot, ".idea");

        // modules.xml.
        XDocument modulesXmlDoc = XDocument.Load(Path.Combine(ideaDir, "modules.xml"));
        Assert.NotNull(modulesXmlDoc.Root);

        // workspace.xml.
        XDocument workspaceXmlDoc = XDocument.Load(Path.Combine(ideaDir, "workspace.xml"));
        Assert.NotNull(workspaceXmlDoc.Root);

        // Per-module .iml files.
        foreach (ModuleRecord rec in modules)
        {
            string imlPath = Path.Combine(ideaDir, rec.Rules.Name + ".iml");
            XDocument imlDoc = XDocument.Load(imlPath);
            Assert.NotNull(imlDoc.Root);
            Assert.Equal("module", imlDoc.Root!.Name.LocalName);
        }
    }

    /// <summary>
    /// Determinism per Section 14: re-running the generator produces
    /// byte-identical output. We compare every emitted file's bytes
    /// across two runs.
    /// </summary>
    [Fact]
    public async Task ReRun_ProducesByteIdenticalOutput()
    {
        List<ModuleRecord> modules = new()
        {
            CreateModule("XRender"),
            CreateModule("XAudio"),
            CreateModule("XScoring"),
        };
        modules[0].Rules.PublicIncludePaths.Add("Engine/Source/Runtime/XRender/Public");
        modules[1].Rules.PrivateDefinitions.Add("XAUDIO_BACKEND=OpenAL");

        RiderProjectGenerator gen = new();
        GenerationContext ctx = MakeContext(modules);

        await gen.GenerateAsync(ctx, CancellationToken.None);
        Dictionary<string, byte[]> first = ReadIdeaFiles();

        await gen.GenerateAsync(ctx, CancellationToken.None);
        Dictionary<string, byte[]> second = ReadIdeaFiles();

        Assert.Equal(first.Keys.OrderBy(k => k, StringComparer.Ordinal),
                     second.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (string key in first.Keys)
        {
            Assert.Equal(first[key], second[key]);
        }
    }

    // ----------------------------------------------------------------------
    // Helpers.
    // ----------------------------------------------------------------------

    private GenerationContext MakeContext(List<ModuleRecord> modules)
    {
        return new GenerationContext(
            EngineRoot: _engineRoot,
            StudioRoot: null,
            ProjectRoot: null,
            Target: new TargetRules
            {
                Name = "EditorTarget",
                TargetType = BuildTargetType.Editor,
                Platform = Platform.Linux,
                Configuration = BuildConfiguration.Development,
                StationRole = StationRole.Engineer,
            },
            Modules: modules,
            Plugins: Array.Empty<PluginEntry>(),
            ToolChain: _toolchain,
            LogChannel: "Test");
    }

    private ModuleRecord CreateModule(string moduleName)
    {
        string moduleDir = Path.Combine(_engineRoot, "Engine", "Source", "Runtime", moduleName);
        Directory.CreateDirectory(moduleDir);
        string descPath = Path.Combine(moduleDir, moduleName + ".Build.toml");
        File.WriteAllText(descPath, "# stub");

        ModuleRules rules = new()
        {
            Name = moduleName,
            Tier = ModuleTier.Engine,
            ModuleType = ModuleType.Runtime,
            Languages = Languages.Cpp,
        };
        IoHash hash = IoHash.Compute(System.Text.Encoding.UTF8.GetBytes("descriptor-stub-" + moduleName));
        return new ModuleRecord(rules, descPath, hash, OwningPluginName: null);
    }

    private Dictionary<string, byte[]> ReadIdeaFiles()
    {
        string ideaDir = Path.Combine(_engineRoot, ".idea");
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach (string path in Directory.GetFiles(ideaDir))
        {
            // Key on the filename (not the full path), so the test is
            // resilient to absolute-path differences across two runs.
            files[Path.GetFileName(path)] = File.ReadAllBytes(path);
        }
        return files;
    }
}
