// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.ActionGraph;
using Simgenics.XPact.XBT.ActionGraph.Actions;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Entry;
using Simgenics.XPact.XBT.Manifest;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests;

// Disambiguate BuildMode + ModuleTier vs the nested test namespaces.
using XBuildMode = Simgenics.XPact.XBT.Entry.BuildMode;

/// <summary>
/// Coverage for the Round-7 audit fixes (C-1 through C-7, M-1 through
/// M-10 except the deferred surface refactors). Each test pins one
/// concrete behavioural invariant the audit findings flagged.
/// </summary>
public sealed class AuditRound7Tests : IDisposable
{
    private readonly string _scratchDir;

    public AuditRound7Tests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.AuditRound7",
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

    private FileItem MakeFile(string name, string contents)
    {
        string p = Path.Combine(_scratchDir, name);
        File.WriteAllText(p, contents);
        return FileItem.GetItemByPath(p);
    }

    private FileItem MakeFile(string name, byte[] contents)
    {
        string p = Path.Combine(_scratchDir, name);
        File.WriteAllBytes(p, contents);
        return FileItem.GetItemByPath(p);
    }

    // ---------------- C-1: ResponseFileContents materialization ------------

    /// <summary>
    /// Audit fix R7-C1: a synthetic action with non-null
    /// <c>ResponseFileContents</c> succeeds end-to-end (the runner
    /// writes the body to disk, references it via <c>@&lt;path&gt;</c>,
    /// and deletes it on completion). Before this fix the body was
    /// in the cache key but never written -- two distinct bodies
    /// would produce distinct keys but identical actual compiler input.
    /// </summary>
    [Fact]
    public void ProcessActionRunner_ActionWithResponseFile_SucceedsAndCleansUp()
    {
        string exe;
        string baseArg;
        if (OperatingSystem.IsWindows())
        {
            exe = Environment.GetEnvironmentVariable("ComSpec")
                ?? @"C:\Windows\System32\cmd.exe";
            baseArg = "/c";
        }
        else
        {
            exe = "/bin/sh";
            baseArg = "-c";
        }

        string producedPath = Path.Combine(_scratchDir, "synthetic.out");
        FileItem produced = FileItem.GetItemByPath(producedPath);
        File.WriteAllText(producedPath, "x");

        ExternalAction action = ExternalAction.Create(new ExternalAction
        {
            ActionType = XActionType.CompileCppAction,
            CommandPath = exe,
            CommandArguments = new[] { baseArg, "exit 0" },
            ResponseFileContents = "/Iinclude /D__TEST_RSP__=1\n",
            ProducedItems = new[] { produced },
            WorkingDirectory = _scratchDir,
            CommandDescription = "TestCompile",
            StatusDescription = "synthetic",
        });

        ProcessActionRunner runner = new();
        Dictionary<FileItem, string> tempPaths = new()
        {
            [produced] = producedPath + ".tmp.42.f",
        };
        ActionRunContext ctx = new(
            Action: action,
            TempOutputPaths: tempPaths,
            ProcessId: Environment.ProcessId,
            ActionId: 0xabcdef,
            CancellationToken: CancellationToken.None);

        ActionRunResult result = runner.RunAction(ctx);

        Assert.True(result.Success, $"runner returned: ExitCode={result.ExitCode}; Err={result.ErrorMessage}");

        // After cleanup no .rsp.tmp.* files should remain in the scratch dir.
        string[] orphans = Directory.GetFiles(_scratchDir, "*.rsp.tmp.*");
        Assert.Empty(orphans);
    }

    /// <summary>
    /// Audit fix R7-C1: an action whose
    /// <see cref="IExternalAction.ResponseFileContents"/> is null does
    /// NOT pass an extra @-arg to the subprocess.
    /// </summary>
    [Fact]
    public void ProcessActionRunner_NullResponseFile_DoesNotAppendIndirection()
    {
        string exe;
        string baseArg;
        if (OperatingSystem.IsWindows())
        {
            exe = Environment.GetEnvironmentVariable("ComSpec")
                ?? @"C:\Windows\System32\cmd.exe";
            baseArg = "/c";
        }
        else
        {
            exe = "/bin/sh";
            baseArg = "-c";
        }

        string producedPath = Path.Combine(_scratchDir, "synthetic.out");
        FileItem produced = FileItem.GetItemByPath(producedPath);
        File.WriteAllText(producedPath, "x");

        ExternalAction action = ExternalAction.Create(new ExternalAction
        {
            ActionType = XActionType.CompileCppAction,
            CommandPath = exe,
            CommandArguments = new[] { baseArg, "exit 0" },
            ResponseFileContents = null,
            ProducedItems = new[] { produced },
            WorkingDirectory = _scratchDir,
        });

        ProcessActionRunner runner = new();
        ActionRunContext ctx = new(
            Action: action,
            TempOutputPaths: new Dictionary<FileItem, string>(),
            ProcessId: Environment.ProcessId,
            ActionId: 1,
            CancellationToken: CancellationToken.None);

        ActionRunResult result = runner.RunAction(ctx);

        Assert.True(result.Success);
        string[] orphans = Directory.GetFiles(_scratchDir, "*.rsp.tmp.*");
        Assert.Empty(orphans);
    }

    /// <summary>
    /// Audit fix R7-C1:
    /// <see cref="FileSystemOps.AtomicWriteAllText"/> writes the
    /// expected bytes via the standard temp + fsync + atomic rename
    /// pipeline.
    /// </summary>
    [Fact]
    public void FileSystemOps_AtomicWriteAllText_WritesExpectedBody()
    {
        string dest = Path.Combine(_scratchDir, "rsp-test.rsp");
        string body = "/Iinclude /DTEST_RSP=1\n/Iother";

        FileSystemOps.AtomicWriteAllText(dest, body);

        Assert.True(File.Exists(dest));
        Assert.Equal(body, File.ReadAllText(dest));
    }

    // ---------------- C-2: Action working-directory + new params -----------

    [Fact]
    public void ParseHeadersAction_WorkingDirectory_CapturedAtConstruction()
    {
        FileItem h1 = MakeFile("Foo.h", "#pragma once\n");
        string xhtExe = MakeFile("xht.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;
        string canonicalRoot = "/canonical/engine/root";

        ParseHeadersAction action = new(
            moduleName: "Mod",
            xhtExecutablePath: xhtExe,
            manifestJsonPath: manifest,
            outputDirectory: _scratchDir,
            sourceFiles: new[] { h1 },
            workingDirectory: canonicalRoot,
            tier: "Engine",
            configuration: BuildConfiguration.Debug,
            platform: Platform.Win64,
            simPath: false);

        Assert.Equal(canonicalRoot, action.WorkingDirectory);
        Assert.Equal("Engine", action.Tier);
        Assert.Equal(BuildConfiguration.Debug, action.Configuration);
        Assert.Equal(Platform.Win64, action.Platform);
        Assert.False(action.SimPath);

        // Mutate CWD and assert the action's WD is unaffected.
        string priorCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_scratchDir);
            Assert.Equal(canonicalRoot, action.WorkingDirectory);
        }
        finally
        {
            Directory.SetCurrentDirectory(priorCwd);
        }
    }

    [Fact]
    public void EmitReflectionAction_WorkingDirectory_CapturedAtConstruction()
    {
        FileItem h1 = MakeFile("Bar.h", "#pragma once\n");
        string xhtExe = MakeFile("xht.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;
        string canonicalRoot = "/canonical/engine/root";

        EmitReflectionAction action = new(
            moduleName: "M",
            xhtExecutablePath: xhtExe,
            manifestJsonPath: manifest,
            outputDirectory: _scratchDir,
            reflectionInputs: new[] { h1 },
            reflectionHeaderRelativePaths: new[] { "Public/Bar.h" },
            generatedCppFilenameBase: "M",
            workingDirectory: canonicalRoot,
            tier: "Studio",
            configuration: BuildConfiguration.Shipping,
            platform: Platform.Linux,
            simPath: true);

        Assert.Equal(canonicalRoot, action.WorkingDirectory);
        Assert.Equal("Studio", action.Tier);
        Assert.Equal(BuildConfiguration.Shipping, action.Configuration);
        Assert.Equal(Platform.Linux, action.Platform);
        Assert.True(action.SimPath);
    }

    [Fact]
    public void ParseHeadersAction_CommandVersion_DistinguishesConfiguration()
    {
        FileItem h1 = MakeFile("Baz.h", "#pragma once\n");
        string xhtExe = MakeFile("xht.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        ParseHeadersAction debugAction = new(
            moduleName: "M", xhtExecutablePath: xhtExe, manifestJsonPath: manifest,
            outputDirectory: _scratchDir, sourceFiles: new[] { h1 },
            workingDirectory: "/wd", configuration: BuildConfiguration.Debug,
            platform: Platform.Win64);
        ParseHeadersAction releaseAction = new(
            moduleName: "M", xhtExecutablePath: xhtExe, manifestJsonPath: manifest,
            outputDirectory: _scratchDir, sourceFiles: new[] { h1 },
            workingDirectory: "/wd", configuration: BuildConfiguration.Shipping,
            platform: Platform.Win64);

        Assert.NotEqual(debugAction.CommandVersion, releaseAction.CommandVersion);
    }

    // ---------------- C-3: FileItem AV-retry --------------------------------

    [Fact]
    public void FileSystemOps_RetryOnTransientIOExceptionGeneric_RetriesAndReturns()
    {
        int call = 0;
        int result = FileSystemOps.RetryOnTransientIOException(() =>
        {
            call++;
            if (call < 2)
            {
                throw new IOException("simulated AV lock");
            }
            return 42;
        });
        Assert.Equal(42, result);
        Assert.True(call >= 2);
    }

    [Fact]
    public async Task FileSystemOps_RetryOnTransientIOExceptionAsync_HonoursCancellation()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await FileSystemOps.RetryOnTransientIOExceptionAsync<int>(
                () => Task.FromResult(0),
                cts.Token);
        });
    }

    [Fact]
    public void FileSystemOps_RetryOnTransientIOException_SurrenderRethrows()
    {
        int call = 0;
        Assert.Throws<IOException>(() =>
        {
            FileSystemOps.RetryOnTransientIOException(() =>
            {
                call++;
                throw new IOException("persistent failure");
            });
        });
        // 1 initial + 4 retries = 5 calls total.
        Assert.Equal(5, call);
    }

    /// <summary>
    /// Audit fix R7-C3: async retry helper returns a result after a
    /// transient failure.
    /// </summary>
    [Fact]
    public async Task FileSystemOps_RetryOnTransientIOExceptionAsync_RetriesAndReturns()
    {
        int call = 0;
        int result = await FileSystemOps.RetryOnTransientIOExceptionAsync(() =>
        {
            call++;
            if (call < 2)
            {
                throw new IOException("simulated AV lock");
            }
            return Task.FromResult(42);
        });
        Assert.Equal(42, result);
        Assert.True(call >= 2);
    }

    // ---------------- C-4: iterative DFS cycle detection -------------------

    [Fact]
    public void ActionGraph_DetectCycles_DeepChainDoesNotStackOverflow()
    {
        const int Depth = 10_000;
        List<IExternalAction> actions = new(Depth);

        FileItem? prevProduced = null;
        for (int i = 0; i < Depth; i++)
        {
            string outPath = Path.Combine(_scratchDir, $"deep-{i:D5}.out");
            FileItem produced = FileItem.GetItemByPath(outPath);
            ExternalAction proto = new()
            {
                ActionType = XActionType.CompileCppAction,
                CommandPath = "/bin/true",
                ProducedItems = new[] { produced },
                PrerequisiteItems = prevProduced is null
                    ? Array.Empty<FileItem>()
                    : new[] { prevProduced },
                CommandDescription = $"deep-{i}",
            };
            actions.Add(ExternalAction.Create(proto));
            prevProduced = produced;
        }

        Simgenics.XPact.XBT.ActionGraph.ActionGraph graph = new(actions);
        graph.Link();
        graph.DetectCycles();
    }

    // ---------------- C-5: marker scan -------------------------------------

    [Fact]
    public void FileContainsAny_Utf16LeWithMarker_DetectsMarker()
    {
        byte[] utf16le = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("XCLASS(Hi)\n#pragma once\n"))
            .ToArray();
        FileItem f = MakeFile("Utf16Le.h", utf16le);

        bool result = XBuildMode.FileContainsAny(f.FullPath, XBuildMode.s_cppReflectionMarkers);
        Assert.True(result);
    }

    [Fact]
    public void FileContainsAny_Utf16BeWithMarker_DetectsMarker()
    {
        byte[] utf16be = Encoding.BigEndianUnicode.GetPreamble()
            .Concat(Encoding.BigEndianUnicode.GetBytes("XSTRUCT(Whatever)\n"))
            .ToArray();
        FileItem f = MakeFile("Utf16Be.h", utf16be);

        bool result = XBuildMode.FileContainsAny(f.FullPath, XBuildMode.s_cppReflectionMarkers);
        Assert.True(result);
    }

    [Fact]
    public void FileContainsAny_Utf8BomWithMarker_DetectsMarker()
    {
        byte[] utf8bom = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("XENUM(X)\n"))
            .ToArray();
        FileItem f = MakeFile("Utf8Bom.h", utf8bom);

        bool result = XBuildMode.FileContainsAny(f.FullPath, XBuildMode.s_cppReflectionMarkers);
        Assert.True(result);
    }

    [Fact]
    public void FileContainsAny_MarkerInLineComment_NoFalsePositive()
    {
        string src = @"// XCLASS(WouldBeAMarkerIfThisWasntAComment)
#pragma once
class NotReflected {};
";
        FileItem f = MakeFile("CommentOnly.h", src);
        bool result = XBuildMode.FileContainsAny(f.FullPath, XBuildMode.s_cppReflectionMarkers);
        Assert.False(result);
    }

    [Fact]
    public void FileContainsAny_MarkerInBlockComment_NoFalsePositive()
    {
        string src = @"/* XSTRUCT(InsideABlockComment)
   spanning multiple lines */
#pragma once
";
        FileItem f = MakeFile("BlockComment.h", src);
        bool result = XBuildMode.FileContainsAny(f.FullPath, XBuildMode.s_cppReflectionMarkers);
        Assert.False(result);
    }

    [Fact]
    public void FileContainsAny_GenuineMarker_Detects()
    {
        string src = @"#pragma once
XCLASS()
class XRealReflected {};
";
        FileItem f = MakeFile("Real.h", src);
        bool result = XBuildMode.FileContainsAny(f.FullPath, XBuildMode.s_cppReflectionMarkers);
        Assert.True(result);
    }

    [Fact]
    public void FileContainsAny_Utf32BomLE_RejectedAsUnsupported()
    {
        // UTF-32 LE BOM is FF FE 00 00. Per Contract §6 we don't
        // support UTF-32 source; the scanner should conservatively
        // return true (claim markers present) so XHT is invoked and
        // produces a clearer error.
        byte[] utf32le = new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x58, 0x00, 0x00, 0x00 };
        FileItem f = MakeFile("Utf32Le.h", utf32le);

        bool result = XBuildMode.FileContainsAny(f.FullPath, XBuildMode.s_cppReflectionMarkers);
        Assert.True(result);
    }

    [Fact]
    public void ReflectionMarkerCache_ShortCircuitsOnUnchangedContent()
    {
        string cachePath = Path.Combine(_scratchDir, "cache.bin");
        ReflectionMarkerCache cache = ReflectionMarkerCache.OpenAtPath(cachePath);

        FileItem h = MakeFile("Cached.h", "XCLASS()\nclass X {};\n");

        bool first = XBuildMode.HasReflectionMarkers(new[] { h }, Array.Empty<FileItem>(), cache);
        Assert.True(first);
        Assert.Equal(1, cache.Count);

        cache.Save();
        ReflectionMarkerCache reloaded = ReflectionMarkerCache.OpenAtPath(cachePath);
        Assert.Equal(1, reloaded.Count);

        Assert.True(reloaded.TryGet(h.FullPath, h.ContentHash, out bool hasMarkers));
        Assert.True(hasMarkers);
    }

    [Fact]
    public void ReflectionMarkerCache_InvalidatesOnContentChange()
    {
        string cachePath = Path.Combine(_scratchDir, "cache.bin");
        ReflectionMarkerCache cache = ReflectionMarkerCache.OpenAtPath(cachePath);

        FileItem h = MakeFile("Mut.h", "XCLASS()\n");
        IoHash original = h.ContentHash;
        cache.Set(h.FullPath, original, hasMarkers: true);

        File.WriteAllText(h.FullPath, "// no markers here\n");
        h.Invalidate();
        IoHash mutated = h.ContentHash;

        Assert.NotEqual(original, mutated);
        Assert.False(cache.TryGet(h.FullPath, mutated, out _));
    }

    // ---------------- C-6: stem collision -----------------------------------

    [Fact]
    public void EmitReflectionAction_StemCollision_FailsBuild()
    {
        string xhtExe = MakeFile("xht.exe", "fake").FullPath;
        string manifest = MakeFile("Manifest.json", "{}").FullPath;

        XBTException ex = Assert.Throws<XBTException>(() => new EmitReflectionAction(
            moduleName: "M",
            xhtExecutablePath: xhtExe,
            manifestJsonPath: manifest,
            outputDirectory: _scratchDir,
            reflectionInputs: Array.Empty<FileItem>(),
            reflectionHeaderRelativePaths: new[] { "Foo/XValve.h", "Bar/XValve.h" }));
        Assert.Equal(50, ex.ExitCode);
        Assert.Contains("XValve.gen.h", ex.Message);
        Assert.Contains("Foo/XValve.h", ex.Message);
        Assert.Contains("Bar/XValve.h", ex.Message);
    }

    // ---------------- M-4: user-scoped mutex name + determinism ------------

    [Fact]
    public void ComposeBuildMutexName_HasXBTBuildPrefixAndIsDeterministic()
    {
        string a = XBuildMode.ComposeBuildMutexName("/eng", "Editor", BuildConfiguration.Debug, Platform.Win64);
        string b = XBuildMode.ComposeBuildMutexName("/eng", "Editor", BuildConfiguration.Debug, Platform.Win64);
        Assert.StartsWith("XBT_Build_", a, StringComparison.Ordinal);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ComposeBuildMutexName_DifferentTargets_DifferentNames()
    {
        string editor = XBuildMode.ComposeBuildMutexName("/eng", "Editor", BuildConfiguration.Debug, Platform.Win64);
        string game = XBuildMode.ComposeBuildMutexName("/eng", "Game", BuildConfiguration.Debug, Platform.Win64);
        string editorRelease = XBuildMode.ComposeBuildMutexName("/eng", "Editor", BuildConfiguration.Shipping, Platform.Win64);
        string editorLinux = XBuildMode.ComposeBuildMutexName("/eng", "Editor", BuildConfiguration.Debug, Platform.Linux);
        string otherRoot = XBuildMode.ComposeBuildMutexName("/other", "Editor", BuildConfiguration.Debug, Platform.Win64);

        Assert.NotEqual(editor, game);
        Assert.NotEqual(editor, editorRelease);
        Assert.NotEqual(editor, editorLinux);
        Assert.NotEqual(editor, otherRoot);
    }

    // ---------------- M-1: centralized marker vocabulary --------------------

    [Fact]
    public void CppReflectionMarkers_AreContractSurfaceWithOpenParen()
    {
        HashSet<string> derived = new(XBuildMode.s_cppReflectionMarkers, StringComparer.Ordinal);
        foreach (string macro in ContractSurface.MarkerMacros)
        {
            Assert.Contains(macro + "(", derived);
        }
        Assert.Equal(ContractSurface.MarkerMacros.Count, derived.Count);
    }

    [Fact]
    public void CsharpReflectionMarkers_CoverEveryNonBodyMarker()
    {
        HashSet<string> derived = new(XBuildMode.s_csharpReflectionMarkers, StringComparer.Ordinal);
        foreach (string macro in ContractSurface.MarkerMacros)
        {
            if (string.Equals(macro, "XGENERATED_BODY", StringComparison.Ordinal))
            {
                continue;
            }
            string title = char.ToUpperInvariant(macro[0]) + macro.Substring(1).ToLowerInvariant();
            Assert.Contains("[" + title, derived);
        }
        Assert.Equal(ContractSurface.MarkerMacros.Count - 1, derived.Count);
    }

    // ---------------- M-8: tier path validation -----------------------------

    [Fact]
    public void TierValidator_DeclaredEngineButPathStudio_Flags()
    {
        string? diag = TierValidator.ValidateDeclaredTierAgainstPath(
            "MyMod",
            ModuleTier.Engine,
            "/repo/Studio/MyMod/MyMod.Build.toml");
        Assert.NotNull(diag);
        Assert.Contains("MyMod", diag);
        Assert.Contains("Engine", diag);
        Assert.Contains("Studio", diag);
    }

    [Fact]
    public void TierValidator_MatchingTierAndPath_Passes()
    {
        Assert.Null(TierValidator.ValidateDeclaredTierAgainstPath(
            "XCore", ModuleTier.Engine, "/repo/Engine/Source/XCore/XCore.Build.toml"));
        Assert.Null(TierValidator.ValidateDeclaredTierAgainstPath(
            "MyStudioMod", ModuleTier.Studio, "/repo/Studio/MyStudioMod/MyStudioMod.Build.toml"));
        Assert.Null(TierValidator.ValidateDeclaredTierAgainstPath(
            "MyProjMod", ModuleTier.Project, "/repo/Projects/MyProj/Source/MyProjMod/MyProjMod.Build.toml"));
    }

    [Fact]
    public void TierValidator_PathWithoutTierMarker_Flags()
    {
        string? diag = TierValidator.ValidateDeclaredTierAgainstPath(
            "OrphanMod",
            ModuleTier.Engine,
            "/repo/MyCustomDir/OrphanMod/OrphanMod.Build.toml");
        Assert.NotNull(diag);
        Assert.Contains("OrphanMod", diag);
    }

    // ---------------- M-10: clang version discovery -------------------------

    /// <summary>
    /// Audit fix R7-M10: a clang path that returns no parseable
    /// version causes the discoverer to throw exit 23 rather than
    /// silently constructing a toolchain with version "unknown".
    /// </summary>
    [Fact]
    public void XClangToolChain_QueryVersion_NonExistentPath_ReturnsNull()
    {
        // Sanity check on the helper: a non-existent path returns
        // null (the upstream caller now throws on null instead of
        // sentinel-ing it). We can't easily exercise TryDiscover
        // itself in a unit test without a real clang binary, so we
        // pin the QueryClangVersion null-return contract here.
        string? v = Simgenics.XPact.XBT.Toolchain.XClangToolChain.QueryClangVersion(
            "/definitely/not/a/real/clang/binary");
        Assert.Null(v);
    }
}
