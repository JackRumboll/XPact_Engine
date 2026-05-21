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
/// Coverage for <see cref="ParseHeadersAction"/> per XBT.html Rev 10
/// Section 9.4 (XHT pass-1 action).
/// </summary>
public sealed class ParseHeadersActionTests : IDisposable
{
    private readonly string _scratchDir;

    public ParseHeadersActionTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.ParseHeadersAction",
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
        FileItem h1 = MakeFile("XValve.h", "#pragma once\n");
        FileItem cs1 = MakeFile("ScoringRubric.cs", "class X {}\n");
        string xhtExe = MakeFile("xht.exe", "fake xht").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        ParseHeadersAction action = new(
            moduleName: "XScoring",
            xhtExecutablePath: xhtExe,
            manifestJsonPath: manifest,
            outputDirectory: _scratchDir,
            sourceFiles: new[] { h1, cs1 });

        Assert.Equal(XActionType.ParseHeadersAction, action.ActionType);
        Assert.Equal("XScoring", action.ModuleName);
        Assert.Equal("XScoring", action.Module);
        Assert.Equal(xhtExe, action.CommandPath);
        Assert.Equal(xhtExe, action.XhtExecutablePath);
        Assert.Equal(manifest, action.ManifestJsonPath);
        Assert.Equal(_scratchDir, action.OutputDirectory);
        Assert.Equal("XHT.Parse", action.CommandDescription);
        Assert.True(action.CanExecuteRemotely);
        Assert.Equal(1.0, action.Weight);
    }

    /// <summary>
    /// Command arguments encode the XHT public CLI shape:
    /// <c>parse-module -Manifest=... -Module=... -Out=...</c>.
    /// </summary>
    [Fact]
    public void CommandArguments_MatchExpectedShape()
    {
        FileItem h1 = MakeFile("XValve.h", "#pragma once\n");
        string xhtExe = MakeFile("xht.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        ParseHeadersAction action = new(
            moduleName: "XScoring",
            xhtExecutablePath: xhtExe,
            manifestJsonPath: manifest,
            outputDirectory: _scratchDir,
            sourceFiles: new[] { h1 });

        Assert.Equal(4, action.CommandArguments.Count);
        Assert.Equal("parse-module", action.CommandArguments[0]);
        Assert.Equal($"-Manifest={manifest}", action.CommandArguments[1]);
        Assert.Equal("-Module=XScoring", action.CommandArguments[2]);
        Assert.Equal($"-Out={_scratchDir}", action.CommandArguments[3]);
    }

    /// <summary>
    /// Prerequisites include every supplied source file, the XHT
    /// executable, and the manifest. The list is sorted ordinal so the
    /// IExternalAction invariant holds.
    /// </summary>
    [Fact]
    public void PrerequisiteItems_IncludeSourcesAndXhtAndManifest_SortedOrdinal()
    {
        FileItem h1 = MakeFile("XValve.h", "#pragma once\n");
        FileItem cs1 = MakeFile("Rubric.cs", "class X {}\n");
        string xhtExe = MakeFile("xht.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        ParseHeadersAction action = new(
            moduleName: "XScoring",
            xhtExecutablePath: xhtExe,
            manifestJsonPath: manifest,
            outputDirectory: _scratchDir,
            sourceFiles: new[] { cs1, h1 }); // unsorted input

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
        Assert.Contains(h1.FullPath, prereqPaths);
        Assert.Contains(cs1.FullPath, prereqPaths);
        Assert.Contains(xhtExe, prereqPaths);
        Assert.Contains(manifest, prereqPaths);
    }

    /// <summary>
    /// ProducedItems contains exactly one entry: the per-module
    /// <c>{Module}.tokens.bin</c>.
    /// </summary>
    [Fact]
    public void ProducedItems_ContainsModuleTokensBin()
    {
        FileItem h1 = MakeFile("XValve.h", "#pragma once\n");
        string xhtExe = MakeFile("xht.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        ParseHeadersAction action = new(
            moduleName: "XScoring",
            xhtExecutablePath: xhtExe,
            manifestJsonPath: manifest,
            outputDirectory: _scratchDir,
            sourceFiles: new[] { h1 });

        Assert.Single(action.ProducedItems);
        string producedPath = action.ProducedItems[0].FullPath;
        Assert.EndsWith("XScoring.tokens.bin", producedPath.Replace('\\', '/'));
        Assert.Contains(_scratchDir.Replace('\\', '/'), producedPath.Replace('\\', '/'));
    }

    /// <summary>
    /// <see cref="ParseHeadersAction.CommandVersion"/> is stable across
    /// repeated reconstructions with identical inputs.
    /// </summary>
    [Fact]
    public void CommandVersion_IsStableAcrossReconstruction()
    {
        FileItem h1 = MakeFile("XValve.h", "#pragma once\n");
        FileItem cs1 = MakeFile("Rubric.cs", "class X {}\n");
        string xhtExe = MakeFile("xht.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        ParseHeadersAction a = new("XScoring", xhtExe, manifest, _scratchDir, new[] { h1, cs1 });
        ParseHeadersAction b = new("XScoring", xhtExe, manifest, _scratchDir, new[] { h1, cs1 });

        Assert.Equal(a.CommandVersion, b.CommandVersion);
    }

    /// <summary>
    /// Two actions for different modules produce different
    /// <see cref="ParseHeadersAction.CommandVersion"/> values.
    /// </summary>
    [Fact]
    public void CommandVersion_DiffersByModuleName()
    {
        FileItem h1 = MakeFile("XValve.h", "#pragma once\n");
        string xhtExe = MakeFile("xht.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        ParseHeadersAction a = new("XScoring", xhtExe, manifest, _scratchDir, new[] { h1 });
        ParseHeadersAction b = new("XOther", xhtExe, manifest, _scratchDir, new[] { h1 });

        Assert.NotEqual(a.CommandVersion, b.CommandVersion);
    }

    /// <summary>
    /// The action is constructed with empty source list (degenerate but
    /// legal: XHT processes the module from the manifest alone). The
    /// surface fields still resolve correctly.
    /// </summary>
    [Fact]
    public void Construction_WithEmptySourceList_StillResolvesProducedItem()
    {
        string xhtExe = MakeFile("xht.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        ParseHeadersAction action = new(
            moduleName: "XEmpty",
            xhtExecutablePath: xhtExe,
            manifestJsonPath: manifest,
            outputDirectory: _scratchDir,
            sourceFiles: Array.Empty<FileItem>());

        Assert.Single(action.ProducedItems);
        Assert.EndsWith("XEmpty.tokens.bin",
            action.ProducedItems[0].FullPath.Replace('\\', '/'));
        // Prerequisites still include the XHT exe + manifest.
        Assert.Equal(2, action.PrerequisiteItems.Count);
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
        string? moduleName, string? xhtExe, string? manifest, string? outDir)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new ParseHeadersAction(
                moduleName!, xhtExe!, manifest!, outDir!,
                Array.Empty<FileItem>()));
    }

    private FileItem MakeFile(string name, string content)
    {
        string path = Path.Combine(_scratchDir, name);
        File.WriteAllText(path, content);
        return FileItem.GetItemByPath(path);
    }
}
