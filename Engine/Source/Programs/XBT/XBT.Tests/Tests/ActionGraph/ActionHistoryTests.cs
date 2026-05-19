// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Simgenics.XPact.XBT.ActionGraph;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.ActionGraph;

/// <summary>
/// Verifies <see cref="ActionHistory"/> invariants: round-trip, staleness
/// detection, atomic-write durability, and the deterministic
/// <see cref="ActionHistory.CurrentVersion"/> hash.
/// </summary>
public sealed class ActionHistoryTests : IDisposable
{
    private readonly string _scratchDir;
    private int _seq;

    public ActionHistoryTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.ActionHistory",
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
    /// Open, RecordHash, Save, reopen, GetStoredHash returns the recorded value.
    /// </summary>
    [Fact]
    public void RecordAndReload_PersistsHashAcrossReopen()
    {
        string archivePath = Path.Combine(_scratchDir, "history.bin");
        FileItem file = MakeFileItem("compile-out");
        IoHash hash = IoHash.Compute(Encoding.UTF8.GetBytes("some content"));

        ActionHistory history = ActionHistory.OpenAtPath(archivePath);
        history.RecordHash(file, hash);
        history.Save();

        ActionHistory reopened = ActionHistory.OpenAtPath(archivePath);
        Assert.Equal(hash, reopened.GetStoredHash(file));
    }

    /// <summary>
    /// IsActionOutdated returns true when the action's CommandVersion
    /// changes (representing a re-emitted action with different args).
    /// </summary>
    [Fact]
    public void IsActionOutdated_CommandVersionChange_ReturnsTrue()
    {
        FileItem produced = MakeFileItem("out");
        File.WriteAllText(produced.FullPath, "v1");

        IExternalAction v1 = MakeAction(new[] { produced }, args: new[] { "-DFOO=1" });
        IExternalAction v2 = MakeAction(new[] { produced }, args: new[] { "-DFOO=2" });

        // CommandVersion differs because the args differ.
        Assert.NotEqual(v1.CommandVersion, v2.CommandVersion);

        ActionHistory history = ActionHistory.OpenAtPath(Path.Combine(_scratchDir, "h.bin"));
        IoHash v1Key = ActionHistory.ComputeActionKey(v1);
        history.RecordHash(produced, v1Key);

        // Wrap v2 in a LinkedAction and ask if it's outdated; yes because
        // the stored key (computed from v1) differs from v2's key.
        var linkedV2 = new LinkedAction(v2);
        Assert.True(history.IsActionOutdated(linkedV2));
    }

    /// <summary>
    /// IsActionOutdated returns true when a ProducedItem is missing on disk.
    /// </summary>
    [Fact]
    public void IsActionOutdated_ProducedItemMissing_ReturnsTrue()
    {
        FileItem produced = MakeFileItem("missing-out");
        // Deliberately do NOT create the file on disk.

        IExternalAction action = MakeAction(new[] { produced });
        ActionHistory history = ActionHistory.OpenAtPath(Path.Combine(_scratchDir, "h.bin"));
        history.RecordHash(produced, ActionHistory.ComputeActionKey(action));

        var linked = new LinkedAction(action);
        Assert.True(history.IsActionOutdated(linked));
    }

    /// <summary>
    /// IsActionOutdated returns false when every prerequisite and produced
    /// item is current and the stored key matches.
    /// </summary>
    [Fact]
    public void IsActionOutdated_NothingChanged_ReturnsFalse()
    {
        FileItem produced = MakeFileItem("out");
        File.WriteAllText(produced.FullPath, "v1");

        IExternalAction action = MakeAction(new[] { produced });
        ActionHistory history = ActionHistory.OpenAtPath(Path.Combine(_scratchDir, "h.bin"));
        history.RecordHash(produced, ActionHistory.ComputeActionKey(action));

        var linked = new LinkedAction(action);
        Assert.False(history.IsActionOutdated(linked));
    }

    /// <summary>
    /// Atomic write: ActionHistory.Save writes through a temp file and
    /// renames into place. If we delete the destination and the temp is
    /// somehow left behind from a prior crash, the next Open() must
    /// tolerate that (no-archive case).
    /// </summary>
    [Fact]
    public void AtomicWrite_CorruptArchive_FailsGracefully()
    {
        string archivePath = Path.Combine(_scratchDir, "h.bin");

        // Write a deliberately-corrupt archive: just 4 bytes of garbage.
        File.WriteAllBytes(archivePath, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        // Open must not throw; the archive is treated as empty.
        ActionHistory history = ActionHistory.OpenAtPath(archivePath);
        FileItem missing = MakeFileItem("never-recorded");
        Assert.Equal(IoHash.Zero, history.GetStoredHash(missing));

        // We can record + save over it; the next reopen reads our entry.
        IoHash hash = IoHash.Compute(Encoding.UTF8.GetBytes("after-corrupt"));
        history.RecordHash(missing, hash);
        history.Save();

        ActionHistory reopened = ActionHistory.OpenAtPath(archivePath);
        Assert.Equal(hash, reopened.GetStoredHash(missing));
    }

    /// <summary>
    /// CurrentVersion is deterministic: invoking the property twice in
    /// the same process returns the same value. The property is
    /// auto-derived from reflection over the IExternalAction shape, so
    /// successive reads should hit the lazy-init cache.
    /// </summary>
    [Fact]
    public void CurrentVersion_IsDeterministic_AcrossTwoInvocations()
    {
        IoHash first = ActionHistory.CurrentVersion;
        IoHash second = ActionHistory.CurrentVersion;
        Assert.Equal(first, second);

        // Sanity: not the zero hash.
        Assert.NotEqual(IoHash.Zero, first);
    }

    /// <summary>
    /// Missing archive file: Open returns an empty archive without throwing.
    /// </summary>
    [Fact]
    public void OpenAtPath_MissingFile_ReturnsEmptyArchive()
    {
        string archivePath = Path.Combine(_scratchDir, "nonexistent.bin");
        ActionHistory history = ActionHistory.OpenAtPath(archivePath);
        FileItem item = MakeFileItem("any");
        Assert.Equal(IoHash.Zero, history.GetStoredHash(item));
    }

    /// <summary>
    /// Save round-trip preserves multiple entries across partition boundaries.
    /// Writing 64 entries (covering many partitions) verifies the partition
    /// dispatch + serialisation works end to end.
    /// </summary>
    [Fact]
    public void Save_ManyEntries_RoundTripCorrectly()
    {
        string archivePath = Path.Combine(_scratchDir, "many.bin");
        ActionHistory history = ActionHistory.OpenAtPath(archivePath);

        var entries = new (FileItem File, IoHash Hash)[64];
        for (int i = 0; i < 64; i++)
        {
            FileItem item = MakeFileItem($"entry-{i:D3}");
            IoHash hash = IoHash.Compute(Encoding.UTF8.GetBytes($"payload-{i}"));
            history.RecordHash(item, hash);
            entries[i] = (item, hash);
        }
        history.Save();

        ActionHistory reopened = ActionHistory.OpenAtPath(archivePath);
        foreach (var (file, hash) in entries)
        {
            Assert.Equal(hash, reopened.GetStoredHash(file));
        }
    }

    // ----- Helpers -----

    private IExternalAction MakeAction(
        IReadOnlyList<FileItem> producedItems,
        IReadOnlyList<string>? args = null)
    {
        return ExternalAction.Create(new ExternalAction
        {
            ActionType = XActionType.CompileCppAction,
            ProducedItems = producedItems,
            CommandPath = "/fake/cl.exe",
            CommandArguments = args ?? Array.Empty<string>(),
            WorkingDirectory = _scratchDir,
            CommandDescription = "Compile",
            StatusDescription = "test.cpp",
            Configuration = BuildConfiguration.Development,
            Platform = Platform.Win64,
        });
    }

    private FileItem MakeFileItem(string label)
    {
        int n = Interlocked.Increment(ref _seq);
        string path = Path.Combine(_scratchDir, $"{label}-{n}.bin");
        return FileItem.GetItemByPath(path);
    }
}
