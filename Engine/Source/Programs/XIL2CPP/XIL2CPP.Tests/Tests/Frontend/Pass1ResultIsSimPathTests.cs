// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Text;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Manifest;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Frontend;

/// <summary>
/// Tests that <see cref="Pass1Result.IsSimPath"/> plumbs through from the
/// manifest module's <c>sim_path</c> flag (the load-bearing seam Pass 3's
/// sim-path analyzers gate on) per /Documents/XIL2CPP.html Rev 4 Section 3.2.
/// </summary>
public sealed class Pass1ResultIsSimPathTests : IDisposable
{
    private readonly string _root;

    public Pass1ResultIsSimPathTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "XIL2CPP-IsSimPath-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private void WriteSource(string relativeUnderRoot, string content)
    {
        string full = Path.Combine(_root, relativeUnderRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static XbtModule SimPathModule(string name, string baseDirectory, string[] csharpSources, bool simPath)
        => new(
            Name: name,
            Tier: ModuleTier.Engine,
            ModuleType: ModuleType.Runtime,
            Languages: Languages.CSharp,
            BaseDirectory: baseDirectory,
            SourceFiles: System.Array.Empty<XbtSourceFile>(),
            PublicHeaders: System.Array.Empty<string>(),
            PrivateHeaders: System.Array.Empty<string>(),
            InternalHeaders: System.Array.Empty<string>(),
            CSharpSources: csharpSources,
            IncludePaths: System.Array.Empty<string>(),
            PublicDefines: System.Array.Empty<string>(),
            ModuleDependencies: System.Array.Empty<XbtModuleDep>(),
            GeneratedCPPFilenameBase: name,
            SimPath: simPath,
            EngineVersionCompat: "0.1.0",
            SimdLevel: SimdLevel.Default,
            PCHUsage: PCHUsageMode.Default,
            ExcludeFromSharedPCH: false,
            AllowHotReload: false,
            IsTestModule: false,
            DeprecationMessage: null,
            MinimumToolchainVersion: null);

    [Fact]
    public void IsSimPath_True_WhenManifestModuleSimPathTrue()
    {
        WriteSource("Sim/A.cs", "namespace Sim; public class A { }");

        XbtModule module = SimPathModule("Sim", "Sim", new[] { "A.cs" }, simPath: true);
        XbtManifest manifest = FrontendTestHelpers.Manifest(_root, module);

        Pass1Result result = Pass1Driver.RunForModule(
            manifest, module, new Pass1Driver.Pass1Options(FrontendTestHelpers.BclReferences(), _ => null));

        Assert.True(result.IsSimPath);
    }

    [Fact]
    public void IsSimPath_False_WhenManifestModuleSimPathFalse()
    {
        WriteSource("NonSim/A.cs", "namespace NonSim; public class A { }");

        XbtModule module = SimPathModule("NonSim", "NonSim", new[] { "A.cs" }, simPath: false);
        XbtManifest manifest = FrontendTestHelpers.Manifest(_root, module);

        Pass1Result result = Pass1Driver.RunForModule(
            manifest, module, new Pass1Driver.Pass1Options(FrontendTestHelpers.BclReferences(), _ => null));

        Assert.False(result.IsSimPath);
    }

    [Fact]
    public void IsSimPath_DefaultsFalse_OnDirectConstruction()
    {
        // The IsSimPath ctor parameter is optional and defaults to false so
        // existing Pass-1 call sites keep non-sim-path behaviour.
        Pass1Result result = NormalizationTestHelpers.BuildPass1(
            "namespace M; public class A { }");
        Assert.False(result.IsSimPath);
    }
}
