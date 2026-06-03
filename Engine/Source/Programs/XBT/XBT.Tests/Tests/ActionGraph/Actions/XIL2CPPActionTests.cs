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
/// Coverage for <see cref="XIL2CPPAction"/> per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 9.8 (the XIL2CPP per-module
/// transpile action) + <c>/Documents/XBT.html</c> Rev 11 Section 5.1.
/// Phase 6.a: the action is a parse/bind VALIDATION pass (no C++ emit), so
/// it declares no produced items and opts out of the action-history cache;
/// these tests pin that contract alongside the subprocess + prerequisite
/// shape.
/// </summary>
public sealed class XIL2CPPActionTests : IDisposable
{
    private readonly string _scratchDir;

    public XIL2CPPActionTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.XIL2CPPAction",
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

        XIL2CPPAction action = new(
            moduleName: "XScoring",
            xil2cppExecutablePath: xil2cppExe,
            manifestJsonPath: manifest,
            intermediateDirectory: intermediate,
            sourceFiles: new[] { cs1, cs2 });

        Assert.Equal(XActionType.XIL2CPPAction, action.ActionType);
        Assert.Equal("XScoring", action.ModuleName);
        Assert.Equal("XScoring", action.Module);
        Assert.Equal(xil2cppExe, action.CommandPath);
        Assert.Equal(xil2cppExe, action.Xil2CppExecutablePath);
        Assert.Equal(manifest, action.ManifestJsonPath);
        Assert.Equal(intermediate, action.IntermediateDirectory);
        Assert.Equal("XIL2CPP.Transpile", action.CommandDescription);
        Assert.Equal("XScoring", action.StatusDescription);
        Assert.True(action.CanExecuteRemotely);
        Assert.Equal(1.0, action.Weight);
    }

    /// <summary>
    /// The action type occupies slot 4 (the canonical XIL2CPPAction enum
    /// slot per XActionType.cs).
    /// </summary>
    [Fact]
    public void ActionType_IsXIL2CPPAction_Slot4()
    {
        FileItem cs1 = MakeFile("A.cs", "class X {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        XIL2CPPAction action = new(
            "XMod", xil2cppExe, manifest, _scratchDir, new[] { cs1 });

        Assert.Equal(XActionType.XIL2CPPAction, action.ActionType);
        Assert.Equal(4, (int)action.ActionType);
    }

    /// <summary>
    /// Phase 6.a honesty: the transpile-module pass emits no C++ yet, so the
    /// action declares NO produced items (it does not fabricate a
    /// <c>.cs.cpp</c> output the subprocess never writes) and opts out of
    /// the action-history cache so the validation re-runs every build.
    /// </summary>
    [Fact]
    public void ProducedItems_Empty_AndDoesNotUseActionHistory()
    {
        FileItem cs1 = MakeFile("A.cs", "class X {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        XIL2CPPAction action = new(
            "XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs1 });

        Assert.Empty(action.ProducedItems);
        Assert.False(action.bUseActionHistory);
    }

    /// <summary>
    /// Command arguments encode the XIL2CPP public CLI shape:
    /// <c>transpile-module -Manifest=... -Module=...</c>. No <c>-Out=</c>
    /// is emitted because Phase 6.a transpile-module writes no file.
    /// </summary>
    [Fact]
    public void CommandArguments_MatchExpectedShape()
    {
        FileItem cs1 = MakeFile("A.cs", "class X {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        XIL2CPPAction action = new(
            "XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs1 });

        Assert.Equal(3, action.CommandArguments.Count);
        Assert.Equal("transpile-module", action.CommandArguments[0]);
        Assert.Equal($"-Manifest={manifest}", action.CommandArguments[1]);
        Assert.Equal("-Module=XScoring", action.CommandArguments[2]);
        Assert.DoesNotContain(action.CommandArguments, a => a.StartsWith("-Out=", StringComparison.Ordinal));
    }

    /// <summary>
    /// Prerequisites include every source file, each dependency module's
    /// reference DLL, the XIL2CPP executable, and the manifest, sorted
    /// ordinal so the IExternalAction invariant holds.
    /// </summary>
    [Fact]
    public void PrerequisiteItems_IncludeSourcesDepsExeAndManifest_SortedOrdinal()
    {
        FileItem cs1 = MakeFile("Widget.cs", "class X {}\n");
        FileItem cs2 = MakeFile("Rubric.cs", "class Y {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;
        string depDll = MakeFile("XDep.refonly.dll", "fake dll").FullPath;

        XIL2CPPAction action = new(
            moduleName: "XScoring",
            xil2cppExecutablePath: xil2cppExe,
            manifestJsonPath: manifest,
            intermediateDirectory: _scratchDir,
            sourceFiles: new[] { cs2, cs1 }, // unsorted input
            dependencyReferences: new[]
            {
                new XIL2CPPAction.DependencyReference("XDep", depDll),
            });

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
    /// <see cref="XIL2CPPAction.CacheKeyComponents"/> with the same shape
    /// <see cref="ReferenceCompileCSharpAction"/> emits (Section 9.8).
    /// </summary>
    [Fact]
    public void CacheKeyComponents_IncludeDependencyRefonlyIdentity()
    {
        FileItem cs1 = MakeFile("A.cs", "class X {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;
        string depDll = MakeFile("XDep.refonly.dll", "fake dll").FullPath;

        XIL2CPPAction action = new(
            "XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs1 },
            dependencyReferences: new[]
            {
                new XIL2CPPAction.DependencyReference("XDep", depDll),
            });

        string component = Assert.Single(action.CacheKeyComponents);
        Assert.Equal($"RefOnlyDep=XDep@{depDll}", component);
    }

    /// <summary>
    /// With no dependencies the cache-key component list is empty.
    /// </summary>
    [Fact]
    public void CacheKeyComponents_Empty_WhenNoDependencies()
    {
        FileItem cs1 = MakeFile("A.cs", "class X {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        XIL2CPPAction action = new(
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

        XIL2CPPAction action = new(
            "XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs1 },
            dependencyReferences: new[]
            {
                new XIL2CPPAction.DependencyReference("XZeta", zDll),
                new XIL2CPPAction.DependencyReference("XAlpha", aDll),
                new XIL2CPPAction.DependencyReference("XZeta", zDll), // duplicate
            });

        Assert.Equal(2, action.DependencyReferences.Count);
        Assert.Equal("XAlpha", action.DependencyReferences[0].ModuleName);
        Assert.Equal("XZeta", action.DependencyReferences[1].ModuleName);
    }

    /// <summary>
    /// <see cref="XIL2CPPAction.CommandVersion"/> is stable across repeated
    /// reconstructions with identical inputs (sources supplied in different
    /// orders converge on the same hash).
    /// </summary>
    [Fact]
    public void CommandVersion_IsStableAcrossReconstruction()
    {
        FileItem cs1 = MakeFile("Widget.cs", "class X {}\n");
        FileItem cs2 = MakeFile("Rubric.cs", "class Y {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;
        string depDll = MakeFile("XDep.refonly.dll", "dep").FullPath;
        XIL2CPPAction.DependencyReference[] deps =
        {
            new("XDep", depDll),
        };

        XIL2CPPAction a = new(
            "XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs1, cs2 }, deps);
        XIL2CPPAction b = new(
            "XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs2, cs1 }, deps);

        Assert.Equal(a.CommandVersion, b.CommandVersion);
    }

    /// <summary>
    /// Two actions for different modules produce different
    /// <see cref="XIL2CPPAction.CommandVersion"/> values.
    /// </summary>
    [Fact]
    public void CommandVersion_DiffersByModuleName()
    {
        FileItem cs1 = MakeFile("A.cs", "class X {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        XIL2CPPAction a = new("XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs1 });
        XIL2CPPAction b = new("XOther", xil2cppExe, manifest, _scratchDir, new[] { cs1 });

        Assert.NotEqual(a.CommandVersion, b.CommandVersion);
    }

    /// <summary>
    /// The command version is sensitive to the build configuration.
    /// </summary>
    [Fact]
    public void CommandVersion_DiffersByConfiguration()
    {
        FileItem cs1 = MakeFile("A.cs", "class X {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        XIL2CPPAction debug = new(
            "XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs1 },
            configuration: BuildConfiguration.Debug);
        XIL2CPPAction dev = new(
            "XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs1 },
            configuration: BuildConfiguration.Development);

        Assert.NotEqual(debug.CommandVersion, dev.CommandVersion);
    }

    /// <summary>
    /// The command version is sensitive to the target platform.
    /// </summary>
    [Fact]
    public void CommandVersion_DiffersByPlatform()
    {
        FileItem cs1 = MakeFile("A.cs", "class X {}\n");
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        XIL2CPPAction win = new(
            "XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs1 },
            platform: Platform.Win64);
        XIL2CPPAction linux = new(
            "XScoring", xil2cppExe, manifest, _scratchDir, new[] { cs1 },
            platform: Platform.Linux);

        Assert.NotEqual(win.CommandVersion, linux.CommandVersion);
    }

    /// <summary>
    /// Construction with an empty source list is legal (a module with no C#
    /// sources still parses to an empty compilation). Prerequisites still
    /// include the XIL2CPP exe + manifest.
    /// </summary>
    [Fact]
    public void Construction_WithEmptySourceList_StillResolvesPrereqs()
    {
        string xil2cppExe = MakeFile("xil2cpp.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        XIL2CPPAction action = new(
            moduleName: "XEmpty",
            xil2cppExecutablePath: xil2cppExe,
            manifestJsonPath: manifest,
            intermediateDirectory: _scratchDir,
            sourceFiles: Array.Empty<FileItem>());

        Assert.Empty(action.ProducedItems);
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
            new XIL2CPPAction(
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
