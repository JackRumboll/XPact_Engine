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
/// Verifies <see cref="ValidateCopyrightAction"/> against the file-extension
/// matrix in <c>/Documents/XBT.html</c> Rev 4 Section 11 + Toolchain
/// Contract Rev 13 Section 10.1 step 5.
/// </summary>
/// <remarks>
/// <para>
/// Each test stages source files in a per-test scratch directory, points
/// the action at them, runs the validator, and inspects the per-file
/// outcome and the aggregate <see cref="ValidationReport"/>.
/// </para>
/// </remarks>
public sealed class ValidateCopyrightActionTests : IDisposable
{
    private const string GoodHeaderCs = "// Copyright Simgenics. All Rights Reserved.";
    private const string GoodHeaderHash = "# Copyright Simgenics. All Rights Reserved.";
    private const string GoodHeaderHtml = "<!-- Copyright Simgenics. All Rights Reserved. -->";

    private readonly string _scratchDir;

    public ValidateCopyrightActionTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.ValidateCopyright",
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
    /// Every file in the input set carries the Simgenics header: the
    /// validator reports zero failures.
    /// </summary>
    [Fact]
    public void EveryFileHasHeader_ReportsZeroFailures()
    {
        FileItem a = MakeFile("Good.cs", GoodHeaderCs + "\npublic class Good {}\n");
        FileItem b = MakeFile("Good.h", "// Copyright Simgenics. All Rights Reserved.\n#pragma once\n");
        FileItem c = MakeFile("Good.cpp", "// Copyright Simgenics. All Rights Reserved.\nvoid Foo(){}\n");

        ValidateCopyrightAction action = new(
            new[] { a, b, c },
            _scratchDir,
            BuildConfiguration.Development,
            Platform.Win64);

        ValidationReport report = action.Run();
        Assert.True(report.Success);
        Assert.Empty(report.Failures);
        Assert.Equal(3, report.FilesChecked);
    }

    /// <summary>
    /// One .cs file is missing the header: the validator reports exactly
    /// one failure whose text names the offending file.
    /// </summary>
    [Fact]
    public void MissingHeaderOnCsFile_ReportsOneFailureNamingTheFile()
    {
        FileItem good = MakeFile("Good.cs", GoodHeaderCs + "\npublic class Good {}\n");
        FileItem bad = MakeFile("Bad.cs", "namespace Bad;\npublic class Bad {}\n");

        ValidateCopyrightAction action = new(
            new[] { good, bad },
            _scratchDir,
            BuildConfiguration.Development,
            Platform.Win64);

        ValidationReport report = action.Run();
        Assert.False(report.Success);
        Assert.Single(report.Failures);
        Assert.Contains(bad.FullPath, report.Failures[0]);
        Assert.Equal(2, report.FilesChecked);
        Assert.Equal(40, ValidateCopyrightAction.MissingHeaderExitCode);
    }

    /// <summary>
    /// HTML file with the Simgenics header on line 2 (after a doctype):
    /// per XBT.html Section 11, the header must be the first non-empty
    /// line; line 2 fails.
    /// </summary>
    [Fact]
    public void HtmlFile_HeaderOnLine2_FailsBecauseFirstNonEmptyLineMustCarryHeader()
    {
        FileItem html = MakeFile(
            "Doc.html",
            "<!DOCTYPE html>\n<!-- Copyright Simgenics. All Rights Reserved. -->\n<html></html>\n");

        ValidateCopyrightAction action = new(
            new[] { html },
            _scratchDir,
            BuildConfiguration.Development,
            Platform.Win64);

        ValidationReport report = action.Run();
        Assert.False(report.Success);
        Assert.Single(report.Failures);
        Assert.Contains(html.FullPath, report.Failures[0]);
    }

    /// <summary>
    /// Files under <c>/Engine/Source/ThirdParty/</c> are excluded even
    /// without the Simgenics header.
    /// </summary>
    [Fact]
    public void ThirdPartyFile_IsSkipped()
    {
        // Construct a synthetic ThirdParty path inside the scratch dir.
        string tpDir = Path.Combine(_scratchDir, "Engine", "Source", "ThirdParty", "ZLib");
        Directory.CreateDirectory(tpDir);
        string tpFile = Path.Combine(tpDir, "zlib.h");
        File.WriteAllText(tpFile, "/* zlib upstream header, no Simgenics copyright */\n");
        FileItem item = FileItem.GetItemByPath(tpFile);

        ValidateCopyrightAction action = new(
            new[] { item },
            _scratchDir,
            BuildConfiguration.Development,
            Platform.Win64);

        ValidationReport report = action.Run();
        Assert.True(report.Success);
        Assert.Empty(report.Failures);
        // Because the file is filtered out at construction time, it
        // never enters PrerequisiteItems and is not counted in
        // FilesChecked.
        Assert.Equal(0, report.FilesChecked);
        Assert.Empty(action.PrerequisiteItems);
    }

    /// <summary>
    /// Generated files under <c>obj/</c> and <c>bin/</c> are excluded.
    /// </summary>
    [Fact]
    public void ObjAndBinFiles_AreSkipped()
    {
        string objDir = Path.Combine(_scratchDir, "obj", "Debug", "net8.0");
        Directory.CreateDirectory(objDir);
        string objFile = Path.Combine(objDir, "Generated.cs");
        File.WriteAllText(objFile, "// generated, no Simgenics header\n");
        FileItem objItem = FileItem.GetItemByPath(objFile);

        string binDir = Path.Combine(_scratchDir, "bin", "Debug");
        Directory.CreateDirectory(binDir);
        string binFile = Path.Combine(binDir, "Output.cs");
        File.WriteAllText(binFile, "// generated, no Simgenics header\n");
        FileItem binItem = FileItem.GetItemByPath(binFile);

        ValidateCopyrightAction action = new(
            new[] { objItem, binItem },
            _scratchDir,
            BuildConfiguration.Development,
            Platform.Win64);

        ValidationReport report = action.Run();
        Assert.True(report.Success);
        Assert.Empty(action.PrerequisiteItems);
    }

    /// <summary>
    /// Visual Studio solution files (<c>.sln</c>) require the header on
    /// line 2 (the format header is line 1). Per XBT.html Section 22.1
    /// special case + CopyrightHeaderTests precedent.
    /// </summary>
    [Fact]
    public void SlnFile_HeaderOnLine2_Passes()
    {
        FileItem sln = MakeFile(
            "XBT.sln",
            "\nMicrosoft Visual Studio Solution File, Format Version 12.00\n# Copyright Simgenics. All Rights Reserved.\n");

        ValidateCopyrightAction action = new(
            new[] { sln },
            _scratchDir,
            BuildConfiguration.Development,
            Platform.Win64);

        ValidationReport report = action.Run();
        Assert.True(report.Success);
        Assert.Empty(report.Failures);
    }

    /// <summary>
    /// FlatSharp-generated files (any file path containing
    /// <c>FlatSharp.generated.cs</c>) are excluded even without the
    /// Simgenics header.
    /// </summary>
    [Fact]
    public void FlatSharpGeneratedFiles_AreSkipped()
    {
        string flatSharpDir = Path.Combine(_scratchDir, "Generated");
        Directory.CreateDirectory(flatSharpDir);
        string flatSharpFile = Path.Combine(flatSharpDir, "Manifest.FlatSharp.generated.cs");
        File.WriteAllText(flatSharpFile, "// FlatSharp-generated, upstream preamble\nnamespace FlatSharp;\n");
        FileItem item = FileItem.GetItemByPath(flatSharpFile);

        ValidateCopyrightAction action = new(
            new[] { item },
            _scratchDir,
            BuildConfiguration.Development,
            Platform.Win64);

        ValidationReport report = action.Run();
        Assert.True(report.Success);
        Assert.Empty(action.PrerequisiteItems);
    }

    /// <summary>
    /// Cacheability: two action instances with the same inputs produce
    /// the same <see cref="IExternalAction.CommandVersion"/> (so
    /// ActionHistory hits on the second run). Then mutating one file's
    /// content yields a different CommandVersion (cache miss).
    /// When constructed with a marker file path,
    /// <see cref="IExternalAction.bUseActionHistory"/> is true; without
    /// one, false (the marker is the cache anchor).
    /// </summary>
    [Fact]
    public void Action_IsCacheable_CommandVersionStableUnlessInputsChange()
    {
        FileItem a = MakeFile("Cache.cs", GoodHeaderCs + "\npublic class A {}\n");
        string marker = Path.Combine(_scratchDir, "marker.txt");

        ValidateCopyrightAction action1 = new(
            new[] { a },
            _scratchDir,
            BuildConfiguration.Development,
            Platform.Win64,
            markerFilePath: marker);

        // FileItem caches the content hash; we read it once so action1's
        // CommandVersion captures the current hash, then invalidate +
        // mutate the file, then build action2 against a fresh FileItem.
        IoHash v1 = action1.CommandVersion;

        // Force a re-read by constructing a new FileItem instance after
        // mutating the file.
        a.Invalidate();
        ValidateCopyrightAction action2 = new(
            new[] { FileItem.GetItemByPath(a.FullPath) },
            _scratchDir,
            BuildConfiguration.Development,
            Platform.Win64,
            markerFilePath: marker);
        IoHash v2 = action2.CommandVersion;

        // Same inputs -> same key (instances share content).
        Assert.Equal(v1, v2);

        // Mutate the file content and rebuild a fresh action.
        File.WriteAllText(a.FullPath, GoodHeaderCs + "\npublic class A_modified {}\n");
        a.Invalidate();
        ValidateCopyrightAction action3 = new(
            new[] { FileItem.GetItemByPath(a.FullPath) },
            _scratchDir,
            BuildConfiguration.Development,
            Platform.Win64,
            markerFilePath: marker);
        IoHash v3 = action3.CommandVersion;

        Assert.NotEqual(v1, v3);

        // With a marker, the action participates in ActionHistory; the
        // marker is the cache anchor.
        Assert.True(action1.bUseActionHistory);
        Assert.NotEmpty(action1.ProducedItems);

        // Without a marker, the action runs unconditionally on every
        // build (no marker = no produced items = ActionHistory cannot
        // cache it).
        ValidateCopyrightAction noMarker = new(
            new[] { a },
            _scratchDir,
            BuildConfiguration.Development,
            Platform.Win64);
        Assert.False(noMarker.bUseActionHistory);
        Assert.Empty(noMarker.ProducedItems);
    }

    /// <summary>
    /// Build orchestration filter check: the static
    /// <see cref="ValidateCopyrightAction.ShouldValidate"/> predicate
    /// agrees with the construction-time filter (so the build can
    /// pre-filter the input set without allocating <see cref="FileItem"/>s).
    /// </summary>
    [Fact]
    public void ShouldValidate_FilterRules_MatchExpectedSets()
    {
        // Validated extensions
        Assert.True(ValidateCopyrightAction.ShouldValidate(@"C:\X\Source.cs"));
        Assert.True(ValidateCopyrightAction.ShouldValidate(@"C:\X\Source.cpp"));
        Assert.True(ValidateCopyrightAction.ShouldValidate(@"C:\X\Header.h"));
        Assert.True(ValidateCopyrightAction.ShouldValidate(@"C:\X\Module.Build.toml"));
        Assert.True(ValidateCopyrightAction.ShouldValidate(@"C:\X\Module.Build.cs"));
        Assert.True(ValidateCopyrightAction.ShouldValidate(@"C:\X\Engine.xengine"));

        // ThirdParty
        Assert.False(ValidateCopyrightAction.ShouldValidate(@"C:\X\Engine\Source\ThirdParty\zlib\zlib.h"));

        // Generated subtrees
        Assert.False(ValidateCopyrightAction.ShouldValidate(@"C:\X\bin\Release\Foo.cs"));
        Assert.False(ValidateCopyrightAction.ShouldValidate(@"C:\X\obj\Debug\Foo.cs"));

        // FlatSharp generated
        Assert.False(ValidateCopyrightAction.ShouldValidate(@"C:\X\Generated\Manifest.FlatSharp.generated.cs"));

        // Unknown extension
        Assert.False(ValidateCopyrightAction.ShouldValidate(@"C:\X\Notes.txt"));
    }

    // ----- Helpers -----

    private FileItem MakeFile(string name, string content)
    {
        string path = Path.Combine(_scratchDir, name);
        File.WriteAllText(path, content);
        return FileItem.GetItemByPath(path);
    }
}
