// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using Simgenics.XPact.XBT.Entry;
using Simgenics.XPact.XBT.Manifest;
using Xunit;

// The C# namespace Simgenics.XPact.XBT.Tests.Tests.BuildMode collides
// with the imported type name `Manifest` -- when type-binding inside
// our namespace, C# sees `Manifest` and tries to resolve it as a
// namespace first. Alias to the fully-qualified record so the test
// code is unambiguous and readable.
using ManifestRecord = Simgenics.XPact.XBT.Manifest.Manifest;

namespace Simgenics.XPact.XBT.Tests.Tests.BuildMode;

/// <summary>
/// End-to-end tests for Step 0.5 addendum gap fix #1: the manifest is
/// emitted at <c>Intermediate/Build/&lt;Target&gt;/&lt;Config&gt;/Manifest.json</c>
/// and the FlatBuffers binary sidecar lands at
/// <c>Manifest.fbs.bin</c> alongside it. Both forms deserialise
/// cleanly and the deserialised <see cref="Manifest"/> reflects the
/// HelloWorld fixture's <c>HelloModule</c>.
/// </summary>
/// <remarks>
/// <para>
/// The fixture is the same one consumed by
/// <see cref="HelloWorldSmokeTest"/>; we copy it into a per-test
/// scratch directory so manifest writes do not collide between tests.
/// </para>
/// <para>
/// Per Step 0.5 addendum Section 3.3 the manifest must be emitted by
/// every successful BuildMode invocation. The tests do NOT require a
/// host compiler -- the manifest emission runs before action emission
/// and before any compiler is spawned. A host without an MSVC
/// toolchain will still emit the manifest, even though the subsequent
/// compile/link actions fail or short-circuit.
/// </para>
/// <para>
/// Audit fix R5-M3: this class is in the
/// <see cref="Simgenics.XPact.XBT.Tests.ToolchainSelfHashCollection"/>
/// serial collection because <c>BuildMode.Run</c> transitively
/// instantiates a toolchain (which reads
/// <c>ToolchainSelfHash.XbtBinaryHash</c>). Even on hosts where the
/// compile / link path short-circuits, the toolchain construction +
/// emit-phase still flows through the override-sensitive code.
/// </para>
/// </remarks>
[Trait("Category", "SmokeBuild")]
[Collection(nameof(Simgenics.XPact.XBT.Tests.ToolchainSelfHashCollection))]
public sealed class ManifestEmissionTests : IDisposable
{
    private readonly string _scratchEngine;

    public ManifestEmissionTests()
    {
        string sourceFixture = LocateFixture();
        _scratchEngine = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.ManifestEmission",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchEngine);
        CopyTree(sourceFixture, _scratchEngine);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratchEngine))
            {
                Directory.Delete(_scratchEngine, recursive: true);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Step 0.5 addendum fix #1 acceptance: BuildMode writes both forms
    /// of the manifest at the documented path, both deserialise, and the
    /// HelloModule appears in the modules list.
    /// </summary>
    [Fact]
    public void BuildMode_Emits_Manifest_Json_And_Fbs_To_Intermediate_Build_Dir()
    {
        string engineRoot = Path.Combine(_scratchEngine, "Engine");

        BuildOptions options = new()
        {
            TargetName = "HelloModule",
            Configuration = BuildConfiguration.Development,
            Platform = Platform.Win64,
            EngineRoot = engineRoot,
        };

        // BuildMode.Run is the programmatic test seam; we do not require
        // the build to succeed end-to-end (the host may lack MSVC).
        Simgenics.XPact.XBT.Entry.BuildMode.Run(options, CancellationToken.None);

        string manifestJson = Path.Combine(
            engineRoot, "Intermediate", "Build", "HelloModule", "Development", "Manifest.json");
        string manifestFbs = Path.Combine(
            engineRoot, "Intermediate", "Build", "HelloModule", "Development", "Manifest.fbs.bin");

        Assert.True(File.Exists(manifestJson),
            $"Expected Manifest.json at {manifestJson}.");
        Assert.True(File.Exists(manifestFbs),
            $"Expected Manifest.fbs.bin at {manifestFbs}.");

        // JSON deserialises cleanly under the hardened reader options.
        byte[] jsonBytes = File.ReadAllBytes(manifestJson);
        ManifestRecord fromJson = ManifestJson.DeserializeFromJson(jsonBytes);
        Assert.NotNull(fromJson);
        Assert.NotEmpty(fromJson.Modules);
        Assert.Contains(fromJson.Modules, m => m.Name == "HelloModule");

        // FBS sidecar deserialises cleanly and has the same module set.
        byte[] fbsBytes = File.ReadAllBytes(manifestFbs);
        ManifestRecord fromFbs = ManifestFbs.DeserializeFromFbs(
            fbsBytes, FbsVerifierLimits.ContractDefaults);
        Assert.NotNull(fromFbs);
        Assert.NotEmpty(fromFbs.Modules);
        Assert.Contains(fromFbs.Modules, m => m.Name == "HelloModule");

        // Wire surface: file_identifier "XMFT" at offset +4.
        Assert.True(fbsBytes.Length >= 8, "FBS buffer is too short.");
        Assert.Equal((byte)'X', fbsBytes[4]);
        Assert.Equal((byte)'M', fbsBytes[5]);
        Assert.Equal((byte)'F', fbsBytes[6]);
        Assert.Equal((byte)'T', fbsBytes[7]);
    }

    /// <summary>
    /// Step 0.5 addendum fix #2 acceptance: a module that contains a
    /// <c>.cs</c> file in its source tree has the file present in the
    /// manifest's <c>Module.CSharpSources</c> field.
    /// </summary>
    [Fact]
    public void BuildMode_Manifest_Includes_CSharpSources_For_Modules_With_Dot_Cs_Files()
    {
        string engineRoot = Path.Combine(_scratchEngine, "Engine");
        string moduleDir = Path.Combine(
            engineRoot, "Source", "Runtime", "HelloModule");
        string privateDir = Path.Combine(moduleDir, "Private");
        Directory.CreateDirectory(privateDir);

        // Add a .cs file under the module tree. EnumerateModuleFiles
        // walks the module directory recursively for *.cs and must pick
        // this up. The fixture's HelloModule is TOML-defined so we do
        // NOT add a HelloModule.Build.cs (a real .Build.cs would be
        // routed to the Roslyn-fallback module compiler; we are testing
        // the source-set enumerator, not the descriptor parser).
        string csFile = Path.Combine(privateDir, "HelloFeature.cs");
        File.WriteAllText(csFile,
            "// Copyright Simgenics. All Rights Reserved.\n" +
            "namespace HelloModule { internal static class Feature { } }\n");

        BuildOptions options = new()
        {
            TargetName = "HelloModule",
            Configuration = BuildConfiguration.Development,
            Platform = Platform.Win64,
            EngineRoot = engineRoot,
        };
        Simgenics.XPact.XBT.Entry.BuildMode.Run(options, CancellationToken.None);

        string manifestJson = Path.Combine(
            engineRoot, "Intermediate", "Build", "HelloModule", "Development", "Manifest.json");
        Assert.True(File.Exists(manifestJson),
            $"Expected Manifest.json at {manifestJson}.");

        ManifestRecord manifest = ManifestJson.DeserializeFromJson(File.ReadAllBytes(manifestJson));
        Simgenics.XPact.XBT.Manifest.Module helloModule =
            manifest.Modules.Single(m => m.Name == "HelloModule");

        // The .cs file the test authored is in the manifest's CSharpSources.
        Assert.Contains(helloModule.CSharpSources,
            p => p.EndsWith("HelloFeature.cs", StringComparison.Ordinal));

        // The same .cs file also appears in SourceFiles with IsCSharp=true.
        Assert.Contains(helloModule.SourceFiles,
            sf => sf.IsCSharp && sf.RelativePath.EndsWith("HelloFeature.cs", StringComparison.Ordinal));
    }

    /// <summary>
    /// Determinism: running BuildMode twice on the same fixture produces
    /// byte-identical Manifest.fbs.bin files. Reproducibility envelope
    /// per Toolchain Contract Rev 13 Section 2.1.
    /// </summary>
    [Fact]
    public void BuildMode_Manifest_Is_Deterministic()
    {
        string engineRoot = Path.Combine(_scratchEngine, "Engine");

        BuildOptions options = new()
        {
            TargetName = "HelloModule",
            Configuration = BuildConfiguration.Development,
            Platform = Platform.Win64,
            EngineRoot = engineRoot,
        };

        Simgenics.XPact.XBT.Entry.BuildMode.Run(options, CancellationToken.None);
        string manifestFbs = Path.Combine(
            engineRoot, "Intermediate", "Build", "HelloModule", "Development", "Manifest.fbs.bin");
        string manifestJson = Path.Combine(
            engineRoot, "Intermediate", "Build", "HelloModule", "Development", "Manifest.json");
        byte[] firstFbs = File.ReadAllBytes(manifestFbs);
        byte[] firstJson = File.ReadAllBytes(manifestJson);

        // Second pass against the same fixture.
        Simgenics.XPact.XBT.Entry.BuildMode.Run(options, CancellationToken.None);
        byte[] secondFbs = File.ReadAllBytes(manifestFbs);
        byte[] secondJson = File.ReadAllBytes(manifestJson);

        Assert.Equal(firstFbs.Length, secondFbs.Length);
        Assert.True(firstFbs.AsSpan().SequenceEqual(secondFbs),
            "Manifest.fbs.bin differed between two runs (non-deterministic emission).");
        Assert.Equal(firstJson.Length, secondJson.Length);
        Assert.True(firstJson.AsSpan().SequenceEqual(secondJson),
            "Manifest.json differed between two runs (non-deterministic emission).");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static string LocateFixture()
    {
        string asmDir = Path.GetDirectoryName(typeof(ManifestEmissionTests).Assembly.Location)!;
        string outputFixture = Path.Combine(asmDir, "Fixtures", "HelloWorldEngine");
        if (Directory.Exists(outputFixture))
        {
            return outputFixture;
        }

        DirectoryInfo? cursor = new(asmDir);
        for (int i = 0; i < 8 && cursor is not null; i++)
        {
            if (string.Equals(cursor.Name, "XBT.Tests", StringComparison.OrdinalIgnoreCase))
            {
                string fixture = Path.Combine(cursor.FullName, "Fixtures", "HelloWorldEngine");
                if (Directory.Exists(fixture))
                {
                    return fixture;
                }
            }
            cursor = cursor.Parent;
        }
        throw new InvalidOperationException(
            $"Could not locate the HelloWorldEngine fixture above {asmDir}.");
    }

    private static void CopyTree(string source, string dest)
    {
        foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(dest, rel));
        }
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, file);
            File.Copy(file, Path.Combine(dest, rel), overwrite: true);
        }
    }
}
