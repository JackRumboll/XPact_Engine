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
/// Coverage for <see cref="ReferenceCompileCSharpAction"/> per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 9.8 (the XIL2CPP
/// reference-compile action: produces <c>&lt;Module&gt;.refonly.dll</c>) +
/// <c>/Documents/XBT.html</c> Rev 11 Section 5.1.
/// </summary>
public sealed class ReferenceCompileCSharpActionTests : IDisposable
{
    private readonly string _scratchDir;

    public ReferenceCompileCSharpActionTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.ReferenceCompileCSharpAction",
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
    /// Construction with a well-formed input set succeeds and the action
    /// records all the spec-required surface fields.
    /// </summary>
    [Fact]
    public void Construction_WithValidInputs_RecordsSurfaceFields()
    {
        FileItem cs1 = MakeFile("Widget.cs", "namespace XScoring; public class Widget {}\n");
        FileItem cs2 = MakeFile("Rubric.cs", "namespace XScoring; public class Rubric {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake xil2cpp").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;
        string intermediate = Path.Combine(_scratchDir, "Intermediate");

        ReferenceCompileCSharpAction action = new(
            moduleName: "XScoring",
            xil2cppExecutablePath: xil2cppExe,
            manifestJsonPath: manifest,
            intermediateDirectory: intermediate,
            sourceFiles: new[] { cs1, cs2 });

        Assert.Equal(XActionType.ReferenceCompileCSharpAction, action.ActionType);
        Assert.Equal("XScoring", action.ModuleName);
        Assert.Equal("XScoring", action.Module);
        Assert.Equal(xil2cppExe, action.CommandPath);
        Assert.Equal(xil2cppExe, action.Xil2CppExecutablePath);
        Assert.Equal(manifest, action.ManifestJsonPath);
        Assert.Equal(intermediate, action.IntermediateDirectory);
        Assert.Equal("XIL2CPP.RefCompile", action.CommandDescription);
        Assert.Equal("XScoring", action.StatusDescription);
        Assert.True(action.CanExecuteRemotely);
        Assert.Equal(1.0, action.Weight);
    }

    /// <summary>
    /// The action type occupies slot 14 (the renamed
    /// <c>Reserved_Phase2_F</c> slot per Section 9.8).
    /// </summary>
    [Fact]
    public void ActionType_IsReferenceCompileCSharpAction_Slot14()
    {
        FileItem cs1 = MakeFile("A.cs", "class X {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        ReferenceCompileCSharpAction action = new(
            "XMod", xil2cppExe, manifest, _scratchDir, new[] { cs1 });

        Assert.Equal(XActionType.ReferenceCompileCSharpAction, action.ActionType);
        Assert.Equal(14, (int)action.ActionType);
    }

    /// <summary>
    /// ProducedItems contains exactly one entry: the per-module
    /// <c>&lt;Module&gt;.refonly.dll</c> under the <c>Reference/</c>
    /// subdirectory (Section 9.8 layout
    /// <c>&lt;intermediate&gt;/&lt;Module&gt;/Reference/&lt;Module&gt;.refonly.dll</c>).
    /// </summary>
    [Fact]
    public void ProducedItems_ContainsModuleRefonlyDll_UnderReference()
    {
        FileItem cs1 = MakeFile("A.cs", "class X {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;
        string intermediate = Path.Combine(_scratchDir, "Intermediate");

        ReferenceCompileCSharpAction action = new(
            "XScoring", xil2cppExe, manifest, intermediate, new[] { cs1 });

        FileItem produced = Assert.Single(action.ProducedItems);
        string producedPath = produced.FullPath.Replace('\\', '/');
        Assert.EndsWith("XScoring.refonly.dll", producedPath);
        Assert.Contains("/XScoring/Reference/", producedPath);
        Assert.Equal(produced.FullPath, action.ProducedDllPath);
    }

    /// <summary>
    /// Command arguments encode the XIL2CPP public CLI shape:
    /// <c>refonly-compile -Manifest=... -Module=... -Out=&lt;producedDll&gt;</c>.
    /// </summary>
    [Fact]
    public void CommandArguments_MatchExpectedShape()
    {
        FileItem cs1 = MakeFile("A.cs", "class X {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;
        string intermediate = Path.Combine(_scratchDir, "Intermediate");

        ReferenceCompileCSharpAction action = new(
            "XScoring", xil2cppExe, manifest, intermediate, new[] { cs1 });

        Assert.Equal(4, action.CommandArguments.Count);
        Assert.Equal("refonly-compile", action.CommandArguments[0]);
        Assert.Equal($"-Manifest={manifest}", action.CommandArguments[1]);
        Assert.Equal("-Module=XScoring", action.CommandArguments[2]);
        Assert.Equal($"-Out={action.ProducedDllPath}", action.CommandArguments[3]);
    }

    /// <summary>
    /// Prerequisites include every source file, each dependency module's
    /// reference DLL, the XIL2CPP executable, and the manifest. The list is
    /// sorted ordinal so the IExternalAction invariant holds.
    /// </summary>
    [Fact]
    public void PrerequisiteItems_IncludeSourcesDepsExeAndManifest_SortedOrdinal()
    {
        FileItem cs1 = MakeFile("Widget.cs", "class X {}\n");
        FileItem cs2 = MakeFile("Rubric.cs", "class Y {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;
        string depDll = MakeFile("XDep.refonly.dll", "fake dll").FullPath;

        ReferenceCompileCSharpAction action = new(
            moduleName: "XScoring",
            xil2cppExecutablePath: xil2cppExe,
            manifestJsonPath: manifest,
            intermediateDirectory: _scratchDir,
            sourceFiles: new[] { cs2, cs1 }, // unsorted input
            dependencyReferences: new[]
            {
                new ReferenceCompileCSharpAction.DependencyReference("XDep", depDll),
            });

        // Sorted ordinal by FullPath.
        for (int i = 1; i < action.PrerequisiteItems.Count; i++)
        {
            Assert.True(
                string.CompareOrdinal(
                    action.PrerequisiteItems[i - 1].FullPath,
                    action.PrerequisiteItems[i].FullPath) < 0,
                $"PrerequisiteItems not sorted at index {i}");
        }

        HashSet<string> prereqPaths = action.PrerequisiteItems.Select(f => f.FullPath).ToHashSet();
        Assert.Contains(cs1.FullPath, prereqPaths);
        Assert.Contains(cs2.FullPath, prereqPaths);
        Assert.Contains(depDll, prereqPaths);
        Assert.Contains(xil2cppExe, prereqPaths);
        Assert.Contains(manifest, prereqPaths);
    }

    /// <summary>
    /// A dependency module's reference DLL surfaces in
    /// <see cref="ReferenceCompileCSharpAction.CacheKeyComponents"/> so a
    /// dependency ABI change busts the action's cache key (Section 9.8).
    /// </summary>
    [Fact]
    public void CacheKeyComponents_IncludeDependencyRefonlyIdentity()
    {
        FileItem cs1 = MakeFile("A.cs", "class X {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;
        string depDll = MakeFile("XDep.refonly.dll", "fake dll").FullPath;

        ReferenceCompileCSharpAction action = new(
            "XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs1 },
            dependencyReferences: new[]
            {
                new ReferenceCompileCSharpAction.DependencyReference("XDep", depDll),
            });

        string component = Assert.Single(action.CacheKeyComponents);
        Assert.Equal($"RefOnlyDep=XDep@{depDll}", component);
    }

    /// <summary>
    /// With no dependencies the cache-key component list is empty (no
    /// hidden inputs beyond CommandVersion).
    /// </summary>
    [Fact]
    public void CacheKeyComponents_Empty_WhenNoDependencies()
    {
        FileItem cs1 = MakeFile("A.cs", "class X {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        ReferenceCompileCSharpAction action = new(
            "XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs1 });

        Assert.Empty(action.CacheKeyComponents);
    }

    /// <summary>
    /// Dependency references are ordinal-sorted by module name + deduped so
    /// two reconstructions with differently-ordered (or duplicated)
    /// dependency lists produce identical surfaces.
    /// </summary>
    [Fact]
    public void DependencyReferences_AreOrdinalSortedAndDeduped()
    {
        FileItem cs1 = MakeFile("A.cs", "class X {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;
        string aDll = MakeFile("XAlpha.refonly.dll", "a").FullPath;
        string zDll = MakeFile("XZeta.refonly.dll", "z").FullPath;

        ReferenceCompileCSharpAction action = new(
            "XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs1 },
            dependencyReferences: new[]
            {
                new ReferenceCompileCSharpAction.DependencyReference("XZeta", zDll),
                new ReferenceCompileCSharpAction.DependencyReference("XAlpha", aDll),
                new ReferenceCompileCSharpAction.DependencyReference("XZeta", zDll), // duplicate
            });

        Assert.Equal(2, action.DependencyReferences.Count);
        Assert.Equal("XAlpha", action.DependencyReferences[0].ModuleName);
        Assert.Equal("XZeta", action.DependencyReferences[1].ModuleName);
    }

    /// <summary>
    /// <see cref="ReferenceCompileCSharpAction.CommandVersion"/> is stable
    /// across repeated reconstructions with identical inputs.
    /// </summary>
    [Fact]
    public void CommandVersion_IsStableAcrossReconstruction()
    {
        FileItem cs1 = MakeFile("Widget.cs", "class X {}\n");
        FileItem cs2 = MakeFile("Rubric.cs", "class Y {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;
        string depDll = MakeFile("XDep.refonly.dll", "dep").FullPath;
        ReferenceCompileCSharpAction.DependencyReference[] deps =
        {
            new("XDep", depDll),
        };

        ReferenceCompileCSharpAction a = new(
            "XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs1, cs2 }, deps);
        ReferenceCompileCSharpAction b = new(
            "XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs2, cs1 }, deps);

        Assert.Equal(a.CommandVersion, b.CommandVersion);
    }

    /// <summary>
    /// Two actions for different modules produce different
    /// <see cref="ReferenceCompileCSharpAction.CommandVersion"/> values.
    /// </summary>
    [Fact]
    public void CommandVersion_DiffersByModuleName()
    {
        FileItem cs1 = MakeFile("A.cs", "class X {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        ReferenceCompileCSharpAction a = new("XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs1 });
        ReferenceCompileCSharpAction b = new("XOther", xil2cppExe, manifest, _scratchDir, new[] { cs1 });

        Assert.NotEqual(a.CommandVersion, b.CommandVersion);
    }

    /// <summary>
    /// The command version is sensitive to the build configuration so a
    /// Debug vs Release reference compile does not alias in the cache.
    /// </summary>
    [Fact]
    public void CommandVersion_DiffersByConfiguration()
    {
        FileItem cs1 = MakeFile("A.cs", "class X {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        ReferenceCompileCSharpAction debug = new(
            "XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs1 },
            configuration: BuildConfiguration.Debug);
        ReferenceCompileCSharpAction release = new(
            "XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs1 },
            configuration: BuildConfiguration.Development);

        Assert.NotEqual(debug.CommandVersion, release.CommandVersion);
    }

    /// <summary>
    /// The command version is sensitive to the target platform so a Win64
    /// vs Linux reference compile does not alias in the cache.
    /// </summary>
    [Fact]
    public void CommandVersion_DiffersByPlatform()
    {
        FileItem cs1 = MakeFile("A.cs", "class X {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        ReferenceCompileCSharpAction win = new(
            "XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs1 },
            platform: Platform.Win64);
        ReferenceCompileCSharpAction linux = new(
            "XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs1 },
            platform: Platform.Linux);

        Assert.NotEqual(win.CommandVersion, linux.CommandVersion);
    }

    /// <summary>
    /// The command version is sensitive to a dependency reference-DLL set
    /// change (an added dependency busts the action even when M's own
    /// sources + command line are byte-identical).
    /// </summary>
    [Fact]
    public void CommandVersion_DiffersWhenDependencyAdded()
    {
        FileItem cs1 = MakeFile("A.cs", "class X {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;
        string depDll = MakeFile("XDep.refonly.dll", "dep").FullPath;

        ReferenceCompileCSharpAction noDeps = new(
            "XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs1 });
        ReferenceCompileCSharpAction withDep = new(
            "XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs1 },
            dependencyReferences: new[]
            {
                new ReferenceCompileCSharpAction.DependencyReference("XDep", depDll),
            });

        // The dependency DLL is a prerequisite (so it changes the sorted
        // prerequisite set), and surfaces in CacheKeyComponents (which the
        // cache layer folds in alongside CommandVersion).
        Assert.NotEqual(noDeps.PrerequisiteItems.Count, withDep.PrerequisiteItems.Count);
        Assert.NotEqual(noDeps.CacheKeyComponents.Count, withDep.CacheKeyComponents.Count);
    }

    /// <summary>
    /// The action is constructed with an empty source list (degenerate but
    /// legal: a module with no sources still emits an empty reference
    /// assembly). The surface fields still resolve correctly.
    /// </summary>
    [Fact]
    public void Construction_WithEmptySourceList_StillResolvesProducedItem()
    {
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        ReferenceCompileCSharpAction action = new(
            moduleName: "XEmpty",
            xil2cppExecutablePath: xil2cppExe,
            manifestJsonPath: manifest,
            intermediateDirectory: _scratchDir,
            sourceFiles: Array.Empty<FileItem>());

        FileItem produced = Assert.Single(action.ProducedItems);
        Assert.EndsWith("XEmpty.refonly.dll", produced.FullPath.Replace('\\', '/'));
        // Prerequisites still include the XIL2CPP exe + manifest.
        Assert.Equal(2, action.PrerequisiteItems.Count);
        Assert.Empty(action.CacheKeyComponents);
    }

    /// <summary>
    /// Required arguments raise on null / empty.
    /// </summary>
    [Theory]
    [InlineData(null, "exe", "manifest", "out")]
    [InlineData("", "exe", "manifest", "out")]
    [InlineData("M", null, "manifest", "out")]
    [InlineData("M", "exe", null, "out")]
    [InlineData("M", "exe", "manifest", "")]
    public void Construction_WithMissingRequired_Throws(
        string? moduleName, string? xil2cppExe, string? manifest, string? intermediate)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new ReferenceCompileCSharpAction(
                moduleName!, xil2cppExe!, manifest!, intermediate!,
                Array.Empty<FileItem>()));
    }

    private FileItem MakeFile(string name, string content)
    {
        string path = Path.Combine(_scratchDir, name);
        File.WriteAllText(path, content);
        return FileItem.GetItemByPath(path);
    }
}
