// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Text;
using System.Threading;
using Simgenics.XPact.XBT.Core;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Core;

/// <summary>
/// Verifies the invariants <see cref="FileItem"/> publishes:
/// per-path cache identity, lazy content-hash with memoization, and the
/// critical <strong>"mtime change must NOT change ContentHash"</strong>
/// rule -- the Toolchain Contract Rev 13 Section 2.1 ban on
/// timestamp-based invalidation enforced at the cache layer.
/// </summary>
public sealed class FileItemTests : IDisposable
{
    private readonly string _scratchDir;

    public FileItemTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.FileItem",
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
            // Best-effort; never let a temp-cleanup failure flake a test.
        }
    }

    /// <summary>
    /// Encoding used for scratch fixtures. Plain UTF-8 with no BOM -- the
    /// .NET default Encoding.UTF8 prepends a BOM which would make the
    /// on-disk byte count and hash unpredictable for short literal strings.
    /// </summary>
    private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Write a file inside the scratch dir and return its absolute path.</summary>
    private string WriteScratch(string fileName, string contents)
    {
        string path = Path.Combine(_scratchDir, fileName);
        File.WriteAllText(path, contents, s_utf8NoBom);
        return path;
    }

    /// <summary>Overwrite an existing scratch file with no BOM.</summary>
    private static void RewriteScratch(string path, string contents)
    {
        File.WriteAllText(path, contents, s_utf8NoBom);
    }

    [Fact]
    public void GetItemByPath_ReturnsSameInstance_ForSamePath()
    {
        string path = WriteScratch("identity.txt", "alpha");

        FileItem first  = FileItem.GetItemByPath(path);
        FileItem second = FileItem.GetItemByPath(path);

        Assert.Same(first, second);
    }

    [Fact]
    public void GetItemByPath_NormalisesPathBeforeCacheLookup()
    {
        string path = WriteScratch("normalise.txt", "alpha");

        FileItem direct       = FileItem.GetItemByPath(path);
        FileItem withSubdirUp = FileItem.GetItemByPath(
            Path.Combine(_scratchDir, "..", Path.GetFileName(_scratchDir), "normalise.txt"));

        // Both routes resolve to the same canonical absolute path; the cache
        // is keyed on that canonical form, so the instances must be identical.
        Assert.Same(direct, withSubdirUp);
    }

    [Fact]
    public void ContentHash_Matches_BlakeOfFileContents()
    {
        const string contents = "Mining training fixture file.";
        string path = WriteScratch("blake.txt", contents);

        FileItem item     = FileItem.GetItemByPath(path);
        IoHash   expected = IoHash.Compute(Encoding.UTF8.GetBytes(contents));

        Assert.Equal(expected, item.ContentHash);
    }

    [Fact]
    public void ContentHash_IsLazy_ComputedAtMostOnce()
    {
        string path = WriteScratch("lazy.txt", "lazy-payload");
        FileItem item = FileItem.GetItemByPath(path);

        // First access reads the file and computes; mutate the bytes BEFORE
        // first access happens. (We did not call ContentHash yet, so the
        // mutation will be the hash source.)
        RewriteScratch(path, "mutated-payload");

        IoHash firstHash = item.ContentHash;
        // The first hash should reflect the mutated bytes -- because the
        // hash was lazy, not eager. If FileItem had eagerly hashed at
        // construction we'd see the original "lazy-payload" digest.
        IoHash expected = IoHash.Compute(Encoding.UTF8.GetBytes("mutated-payload"));
        Assert.Equal(expected, firstHash);

        // Repeat access returns the cached value identity -- the file can
        // be mutated again and we still see the first-observed digest.
        RewriteScratch(path, "second-mutation");
        IoHash secondHash = item.ContentHash;
        Assert.Equal(firstHash, secondHash);
    }

    /// <summary>
    /// The cache invariant the Toolchain Contract Rev 13 Section 2.1 demands:
    /// modification time MUST NOT influence what <see cref="FileItem.ContentHash"/>
    /// returns. After re-fetching the FileItem (via cache + manual invalidation)
    /// the hash recomputes from disk content -- not from a stat-based shortcut.
    /// </summary>
    [Fact]
    public void MtimeChange_DoesNotChange_ContentHash()
    {
        const string contents = "content-addressable";
        string path = WriteScratch("mtime-invariant.txt", contents);

        FileItem first = FileItem.GetItemByPath(path);
        IoHash firstHash = first.ContentHash;
        DateTime firstMtime = first.LastWriteTimeUtc;

        // Wait long enough that the OS records a different mtime, then
        // rewrite the SAME bytes. If FileItem.ContentHash were a function of
        // mtime, this would change.
        Thread.Sleep(50);
        RewriteScratch(path, contents);
        // Touch the mtime forwards explicitly so the system clock skew
        // does not let the value collapse to the original.
        File.SetLastWriteTimeUtc(path, firstMtime.AddSeconds(2));

        // Drop the in-memory caches so the next ContentHash access re-reads
        // disk; the cache invariant says re-read of same bytes yields same
        // hash regardless of mtime.
        first.Invalidate();

        IoHash refreshedHash = first.ContentHash;
        DateTime refreshedMtime = first.LastWriteTimeUtc;

        Assert.Equal(firstHash, refreshedHash);
        Assert.NotEqual(firstMtime, refreshedMtime);
    }

    [Fact]
    public void ContentHash_ChangesWhen_BytesChange()
    {
        string path = WriteScratch("bytes-changed.txt", "original");
        FileItem item = FileItem.GetItemByPath(path);
        IoHash before = item.ContentHash;

        // Mutate bytes AND invalidate the cache so the next read materialises.
        RewriteScratch(path, "different");
        item.Invalidate();

        IoHash after = item.ContentHash;

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Length_ReflectsFileBytesOnDisk()
    {
        const string contents = "twelve bytes";
        string path = WriteScratch("len.txt", contents);
        FileItem item = FileItem.GetItemByPath(path);
        Assert.Equal(contents.Length, item.Length);
    }
}
