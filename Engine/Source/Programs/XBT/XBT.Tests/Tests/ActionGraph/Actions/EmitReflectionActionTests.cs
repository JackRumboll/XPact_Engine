// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Simgenics.XPact.XBT.ActionGraph;
using Simgenics.XPact.XBT.ActionGraph.Actions;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.ActionGraph.Actions;

/// <summary>
/// Coverage for <see cref="EmitReflectionAction"/> per XBT.html Rev 10
/// Section 9.4 + XHT.html Rev 8 Section 9.3.1 (pre-discovery rule).
/// </summary>
public sealed class EmitReflectionActionTests : IDisposable
{
    private readonly string _scratchDir;

    public EmitReflectionActionTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.EmitReflectionAction",
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
        }
    }

    /// <summary>
    /// Construction surfaces every spec-required field.
    /// </summary>
    [Fact]
    public void Construction_WithValidInputs_RecordsSurfaceFields()
    {
        FileItem h1 = MakeFile("XValve.h", "#pragma once\n");
        string xhtExe = MakeFile("xht.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        EmitReflectionAction action = new(
            moduleName: "XScoring",
            xhtExecutablePath: xhtExe,
            manifestJsonPath: manifest,
            outputDirectory: _scratchDir,
            reflectionInputs: new[] { h1 },
            reflectionHeaderRelativePaths: new[] { "Public/XValve.h" });

        Assert.Equal(XActionType.EmitReflectionAction, action.ActionType);
        Assert.Equal("XScoring", action.ModuleName);
        Assert.Equal(xhtExe, action.XhtExecutablePath);
        Assert.Equal(manifest, action.ManifestJsonPath);
        Assert.Equal(_scratchDir, action.OutputDirectory);
        Assert.Equal("XHT.Emit", action.CommandDescription);
        Assert.True(action.CanExecuteRemotely);
    }

    /// <summary>
    /// Command line shape: <c>emit-module -Manifest=... -Module=... -Out=...</c>.
    /// </summary>
    [Fact]
    public void CommandArguments_MatchExpectedShape()
    {
        FileItem h1 = MakeFile("XValve.h", "#pragma once\n");
        string xhtExe = MakeFile("xht.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        EmitReflectionAction action = new(
            moduleName: "XScoring",
            xhtExecutablePath: xhtExe,
            manifestJsonPath: manifest,
            outputDirectory: _scratchDir,
            reflectionInputs: new[] { h1 },
            reflectionHeaderRelativePaths: new[] { "Public/XValve.h" });

        Assert.Equal(4, action.CommandArguments.Count);
        Assert.Equal("emit-module", action.CommandArguments[0]);
        Assert.Equal($"-Manifest={manifest}", action.CommandArguments[1]);
        Assert.Equal("-Module=XScoring", action.CommandArguments[2]);
        Assert.Equal($"-Out={_scratchDir}", action.CommandArguments[3]);
    }

    /// <summary>
    /// Pre-discovery: a module with 0 reflection headers still produces
    /// 2 outputs (<c>.init.gen.cpp</c> + <c>.gen.manifest</c>) per
    /// XHT.html Section 9.3.1.
    /// </summary>
    [Fact]
    public void Module_WithZeroHeaders_Produces_TwoFiles_InitAndManifest()
    {
        string xhtExe = MakeFile("xht.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        EmitReflectionAction action = new(
            moduleName: "XEmpty",
            xhtExecutablePath: xhtExe,
            manifestJsonPath: manifest,
            outputDirectory: _scratchDir,
            reflectionInputs: Array.Empty<FileItem>(),
            reflectionHeaderRelativePaths: Array.Empty<string>());

        Assert.Equal(2, action.ProducedItems.Count);
        HashSet<string> producedNames = action.ProducedItems
            .Select(f => Path.GetFileName(f.FullPath))
            .ToHashSet();
        Assert.Contains("XEmpty.init.gen.cpp", producedNames);
        Assert.Contains("XEmpty.gen.manifest", producedNames);
    }

    /// <summary>
    /// Pre-discovery: a module with 3 reflection headers produces
    /// 3 × (.gen.h + .gen.cpp) + 1 × .init.gen.cpp + 1 × .gen.manifest
    /// = 8 produced items total.
    /// </summary>
    [Fact]
    public void Module_WithThreeHeaders_Produces_EightFiles()
    {
        FileItem a = MakeFile("A.h", "#pragma once\n");
        FileItem b = MakeFile("B.h", "#pragma once\n");
        FileItem c = MakeFile("C.h", "#pragma once\n");
        string xhtExe = MakeFile("xht.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        EmitReflectionAction action = new(
            moduleName: "XScoring",
            xhtExecutablePath: xhtExe,
            manifestJsonPath: manifest,
            outputDirectory: _scratchDir,
            reflectionInputs: new[] { a, b, c },
            reflectionHeaderRelativePaths: new[] { "Public/A.h", "Public/B.h", "Private/C.h" });

        // 3 .gen.h + 3 .gen.cpp + 1 .init.gen.cpp + 1 .gen.manifest = 8.
        Assert.Equal(8, action.ProducedItems.Count);
        HashSet<string> producedNames = action.ProducedItems
            .Select(f => Path.GetFileName(f.FullPath))
            .ToHashSet();
        Assert.Contains("A.gen.h", producedNames);
        Assert.Contains("A.gen.cpp", producedNames);
        Assert.Contains("B.gen.h", producedNames);
        Assert.Contains("B.gen.cpp", producedNames);
        Assert.Contains("C.gen.h", producedNames);
        Assert.Contains("C.gen.cpp", producedNames);
        Assert.Contains("XScoring.init.gen.cpp", producedNames);
        Assert.Contains("XScoring.gen.manifest", producedNames);
    }

    /// <summary>
    /// Output paths match the names <see cref="XhtOutputNaming"/>
    /// produces -- the XBT-side mirror of XHT.Emitter's naming logic.
    /// </summary>
    [Fact]
    public void ProducedItems_MatchXhtOutputNaming()
    {
        FileItem h1 = MakeFile("XValve.h", "#pragma once\n");
        string xhtExe = MakeFile("xht.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        EmitReflectionAction action = new(
            moduleName: "XScoring",
            xhtExecutablePath: xhtExe,
            manifestJsonPath: manifest,
            outputDirectory: _scratchDir,
            reflectionInputs: new[] { h1 },
            reflectionHeaderRelativePaths: new[] { "Public/XValve.h" });

        string expectedGenH = Path.Combine(_scratchDir,
            XhtOutputNaming.GenHeaderFileName("Public/XValve.h"));
        string expectedGenCpp = Path.Combine(_scratchDir,
            XhtOutputNaming.GenSourceFileName("Public/XValve.h"));
        string expectedInit = Path.Combine(_scratchDir,
            XhtOutputNaming.ModuleInitFileName("XScoring", "XScoring"));
        string expectedManifest = Path.Combine(_scratchDir,
            XhtOutputNaming.GenManifestFileName("XScoring", "XScoring"));

        HashSet<string> paths = action.ProducedItems.Select(f => f.FullPath).ToHashSet();
        Assert.Contains(expectedGenH.Replace('\\', '/'),
            paths.Select(p => p.Replace('\\', '/')));
        Assert.Contains(expectedGenCpp.Replace('\\', '/'),
            paths.Select(p => p.Replace('\\', '/')));
        Assert.Contains(expectedInit.Replace('\\', '/'),
            paths.Select(p => p.Replace('\\', '/')));
        Assert.Contains(expectedManifest.Replace('\\', '/'),
            paths.Select(p => p.Replace('\\', '/')));
    }

    /// <summary>
    /// Prerequisites include every reflection input + the XHT exe + the
    /// manifest, sorted ordinal.
    /// </summary>
    [Fact]
    public void PrerequisiteItems_IncludeInputsAndExeAndManifest_SortedOrdinal()
    {
        FileItem h1 = MakeFile("XValve.h", "#pragma once\n");
        FileItem cs1 = MakeFile("Rubric.cs", "class X {}\n");
        string xhtExe = MakeFile("xht.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        EmitReflectionAction action = new(
            moduleName: "XScoring",
            xhtExecutablePath: xhtExe,
            manifestJsonPath: manifest,
            outputDirectory: _scratchDir,
            reflectionInputs: new[] { cs1, h1 },
            reflectionHeaderRelativePaths: new[] { "Public/XValve.h" });

        for (int i = 1; i < action.PrerequisiteItems.Count; i++)
        {
            Assert.True(
                string.CompareOrdinal(
                    action.PrerequisiteItems[i - 1].FullPath,
                    action.PrerequisiteItems[i].FullPath) < 0);
        }

        HashSet<string> paths = action.PrerequisiteItems.Select(f => f.FullPath).ToHashSet();
        Assert.Contains(h1.FullPath, paths);
        Assert.Contains(cs1.FullPath, paths);
        Assert.Contains(xhtExe, paths);
        Assert.Contains(manifest, paths);
    }

    /// <summary>
    /// <see cref="EmitReflectionAction.CommandVersion"/> is stable
    /// across repeated reconstruction with identical inputs.
    /// </summary>
    [Fact]
    public void CommandVersion_IsStableAcrossReconstruction()
    {
        FileItem h1 = MakeFile("XValve.h", "#pragma once\n");
        string xhtExe = MakeFile("xht.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        EmitReflectionAction a = new(
            "XScoring", xhtExe, manifest, _scratchDir,
            new[] { h1 }, new[] { "Public/XValve.h" });
        EmitReflectionAction b = new(
            "XScoring", xhtExe, manifest, _scratchDir,
            new[] { h1 }, new[] { "Public/XValve.h" });

        Assert.Equal(a.CommandVersion, b.CommandVersion);
    }

    /// <summary>
    /// CommandVersion differs when the header set changes (because the
    /// pre-discovered output set differs).
    /// </summary>
    [Fact]
    public void CommandVersion_DiffersByHeaderSet()
    {
        FileItem h1 = MakeFile("A.h", "#pragma once\n");
        string xhtExe = MakeFile("xht.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        EmitReflectionAction a = new(
            "XScoring", xhtExe, manifest, _scratchDir,
            new[] { h1 }, new[] { "Public/A.h" });
        EmitReflectionAction b = new(
            "XScoring", xhtExe, manifest, _scratchDir,
            new[] { h1 }, new[] { "Public/A.h", "Public/B.h" });

        Assert.NotEqual(a.CommandVersion, b.CommandVersion);
    }

    /// <summary>
    /// Audit fix R7-C6: two headers with the same filename stem in
    /// different directories fail the build with exit 50
    /// (ManifestMalformed) at action-construct time. The prior
    /// behaviour silently elided one of the headers, losing its
    /// reflection metadata at run time. Fail-loud is the right
    /// default per the engineering principles: a configuration
    /// defect must surface at the earliest possible point.
    /// </summary>
    [Fact]
    public void DuplicateHeaderStem_ThrowsXBTException()
    {
        string xhtExe = MakeFile("xht.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        Simgenics.XPact.XBT.Core.XBTException ex =
            Assert.Throws<Simgenics.XPact.XBT.Core.XBTException>(() => new EmitReflectionAction(
                moduleName: "XScoring",
                xhtExecutablePath: xhtExe,
                manifestJsonPath: manifest,
                outputDirectory: _scratchDir,
                reflectionInputs: Array.Empty<FileItem>(),
                reflectionHeaderRelativePaths: new[] { "Public/X.h", "Private/X.h" }));

        Assert.Equal(50, ex.ExitCode);
        Assert.Contains("Public/X.h", ex.Message);
        Assert.Contains("Private/X.h", ex.Message);
        Assert.Contains("X.gen.h", ex.Message);
    }

    /// <summary>
    /// <see cref="EmitReflectionAction.ReflectionHeaderRelativePaths"/>
    /// is sorted ordinal + deduplicated so the recorded header set is
    /// stable for cache-key purposes.
    /// </summary>
    [Fact]
    public void ReflectionHeaderRelativePaths_AreSortedAndDeduplicated()
    {
        string xhtExe = MakeFile("xht.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        EmitReflectionAction action = new(
            moduleName: "X",
            xhtExecutablePath: xhtExe,
            manifestJsonPath: manifest,
            outputDirectory: _scratchDir,
            reflectionInputs: Array.Empty<FileItem>(),
            reflectionHeaderRelativePaths: new[] { "Public/Z.h", "Public/A.h", "Public/M.h", "Public/A.h" });

        // After sort + dedupe: 3 unique paths.
        Assert.Equal(3, action.ReflectionHeaderRelativePaths.Count);
        Assert.Equal("Public/A.h", action.ReflectionHeaderRelativePaths[0]);
        Assert.Equal("Public/M.h", action.ReflectionHeaderRelativePaths[1]);
        Assert.Equal("Public/Z.h", action.ReflectionHeaderRelativePaths[2]);
    }

    /// <summary>
    /// Generated CPP filename base overrides the module name for the
    /// per-module .init.gen.cpp + .gen.manifest.
    /// </summary>
    [Fact]
    public void GeneratedCppFilenameBase_OverridesModuleName()
    {
        FileItem h1 = MakeFile("X.h", "#pragma once\n");
        string xhtExe = MakeFile("xht.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        EmitReflectionAction action = new(
            moduleName: "XScoring",
            xhtExecutablePath: xhtExe,
            manifestJsonPath: manifest,
            outputDirectory: _scratchDir,
            reflectionInputs: new[] { h1 },
            reflectionHeaderRelativePaths: new[] { "Public/X.h" },
            generatedCppFilenameBase: "XCustomBase");

        HashSet<string> producedNames = action.ProducedItems
            .Select(f => Path.GetFileName(f.FullPath))
            .ToHashSet();
        Assert.Contains("XCustomBase.init.gen.cpp", producedNames);
        Assert.Contains("XCustomBase.gen.manifest", producedNames);
        // Module name is no longer in the produced filenames.
        Assert.DoesNotContain("XScoring.init.gen.cpp", producedNames);
        Assert.DoesNotContain("XScoring.gen.manifest", producedNames);
    }

    private FileItem MakeFile(string name, string content)
    {
        string path = Path.Combine(_scratchDir, name);
        File.WriteAllText(path, content);
        return FileItem.GetItemByPath(path);
    }
}
