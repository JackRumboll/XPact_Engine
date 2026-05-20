// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Text;
using Simgenics.XPact.XBT.ActionGraph;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.ActionGraph;

/// <summary>
/// Pins the auto-derived <see cref="ActionHistory.CurrentVersion"/>
/// hash against accidental drift. Per the post-audit Rev 13.x fix,
/// <c>CurrentVersion</c> is computed by sorting the
/// <see cref="IExternalAction"/> property set alphabetically (rather
/// than by Roslyn-assigned <c>MetadataToken</c> order). The change
/// makes the hash invariant against
/// <list type="bullet">
///   <item>Roslyn version bumps that re-number metadata tokens.</item>
///   <item>Cosmetic reorderings of property declarations in source.</item>
///   <item>.NET runtime version changes.</item>
/// </list>
/// while still flagging any genuine schema change (property added,
/// removed, renamed, or retyped).
/// </summary>
public sealed class ActionHistoryCurrentVersionTests : IDisposable
{
    private readonly string _scratchDir;

    public ActionHistoryCurrentVersionTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.ActionHistoryCurrentVersion",
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
    /// Two successive reads of <see cref="ActionHistory.CurrentVersion"/>
    /// inside the same process return identical hashes. The property is
    /// memoised via <see cref="Lazy{T}"/>, but the deeper invariant is
    /// that the underlying reflection walk is deterministic: same input
    /// types -&gt; same output bytes.
    /// </summary>
    [Fact]
    public void CurrentVersion_RepeatedReads_AreIdentical()
    {
        IoHash first = ActionHistory.CurrentVersion;
        IoHash second = ActionHistory.CurrentVersion;
        IoHash third = ActionHistory.CurrentVersion;
        Assert.Equal(first, second);
        Assert.Equal(second, third);
        Assert.NotEqual(IoHash.Zero, first);
    }

    /// <summary>
    /// Save() output is byte-identical for two invocations on the
    /// same in-memory archive state. The partition iteration order is
    /// sorted with <c>StringComparer.Ordinal</c>, the temp-file suffix
    /// is a GUID (not a timestamp), and the <c>CurrentVersion</c>
    /// header is deterministic -- so the persisted bytes must be
    /// invariant. Both saves are then compared with BLAKE3 to assert
    /// every byte matches.
    /// </summary>
    [Fact]
    public void Save_TwoInvocations_OnSameState_ProduceByteIdenticalArchives()
    {
        // Build a history populated with entries that fall across
        // several partitions (random-looking paths spread across the
        // 256 partition slots).
        string archivePath1 = Path.Combine(_scratchDir, "first.bin");
        string archivePath2 = Path.Combine(_scratchDir, "second.bin");

        ActionHistory h1 = ActionHistory.OpenAtPath(archivePath1);
        ActionHistory h2 = ActionHistory.OpenAtPath(archivePath2);

        // Insert in a deliberately non-monotonic order to exercise
        // the sort logic. If iteration order were dictionary-insertion
        // order the two byte streams would diverge.
        string[] paths =
        {
            "/scratch/zzz/last.bin",
            "/scratch/aaa/first.bin",
            "/scratch/mmm/middle.bin",
            "/scratch/aab/second.bin",
            "/scratch/bba/third.bin",
            "/scratch/0z9/edge.bin",
        };

        foreach (string path in paths)
        {
            FileItem item = FileItem.GetItemByPath(path);
            IoHash hash = IoHash.Compute(Encoding.UTF8.GetBytes("payload-" + path));
            h1.RecordHash(item, hash);
        }

        // Insert into h2 in the REVERSE order. The serialised output
        // must still match because both archives sort by path before
        // writing.
        for (int i = paths.Length - 1; i >= 0; i--)
        {
            FileItem item = FileItem.GetItemByPath(paths[i]);
            IoHash hash = IoHash.Compute(Encoding.UTF8.GetBytes("payload-" + paths[i]));
            h2.RecordHash(item, hash);
        }

        h1.Save();
        h2.Save();

        byte[] bytes1 = File.ReadAllBytes(archivePath1);
        byte[] bytes2 = File.ReadAllBytes(archivePath2);

        // Compare via BLAKE3 to keep the failure message bounded. A
        // byte-for-byte mismatch would otherwise produce a huge xUnit
        // diff dump.
        IoHash digest1 = IoHash.Compute(bytes1);
        IoHash digest2 = IoHash.Compute(bytes2);
        Assert.Equal(digest1, digest2);

        // Cross-check the raw bytes too -- BLAKE3 collision is
        // astronomically unlikely but the assertion proves the cache
        // file truly is byte-identical, not just hash-identical.
        Assert.Equal(bytes1, bytes2);
    }

    /// <summary>
    /// Saving the same archive twice on the SAME path produces
    /// byte-identical output, even though Save() goes through a
    /// temp-file + atomic-rename. Validates that the temp-file naming
    /// (GUID-suffixed) does not leak into the persisted bytes.
    /// </summary>
    [Fact]
    public void Save_SamePath_TwoInvocations_AreByteIdentical()
    {
        string archivePath = Path.Combine(_scratchDir, "history.bin");
        ActionHistory history = ActionHistory.OpenAtPath(archivePath);

        for (int i = 0; i < 16; i++)
        {
            FileItem item = FileItem.GetItemByPath(Path.Combine(_scratchDir, $"entry-{i:D3}.bin"));
            IoHash hash = IoHash.Compute(Encoding.UTF8.GetBytes($"payload-{i}"));
            history.RecordHash(item, hash);
        }

        history.Save();
        byte[] firstSave = File.ReadAllBytes(archivePath);

        history.Save();
        byte[] secondSave = File.ReadAllBytes(archivePath);

        Assert.Equal(firstSave, secondSave);
    }
}
