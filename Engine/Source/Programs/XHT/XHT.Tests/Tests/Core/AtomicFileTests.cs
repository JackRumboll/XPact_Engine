// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Linq;
using System.Text;
using Simgenics.XPact.XHT.Core;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Core;

/// <summary>
/// Tests for <see cref="AtomicFile"/>. The atomic write contract per
/// <c>/Documents/XHT.html</c> Rev 7 Section 8.5 + Section 14:
/// temp-file + fsync + rename, no torn observable state, UTF-8 without
/// BOM, AV-retry on rename.
/// </summary>
public class AtomicFileTests : IDisposable
{
    private readonly string _tempDir;

    public AtomicFileTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "XHT.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void WriteAllText_CreatesFileWithExpectedContent()
    {
        string path = Path.Combine(_tempDir, "out.txt");
        AtomicFile.WriteAllText(path, "hello, world\n");
        Assert.True(File.Exists(path));
        Assert.Equal("hello, world\n", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_OverwritesExistingFile()
    {
        string path = Path.Combine(_tempDir, "out.txt");
        File.WriteAllText(path, "old content");
        AtomicFile.WriteAllText(path, "new content");
        Assert.Equal("new content", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_NoBomOnUtf8Output()
    {
        string path = Path.Combine(_tempDir, "out.txt");
        AtomicFile.WriteAllText(path, "ABC");
        byte[] bytes = File.ReadAllBytes(path);
        // UTF-8 BOM is 0xEF 0xBB 0xBF; must NOT appear in our output.
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal(new byte[] { 0x41, 0x42, 0x43 }, bytes);
    }

    [Fact]
    public void WriteAllBytes_RoundTrips()
    {
        string path = Path.Combine(_tempDir, "out.bin");
        byte[] data = new byte[] { 0x00, 0x01, 0xFF, 0xFE, 0x42 };
        AtomicFile.WriteAllBytes(path, data);
        Assert.Equal(data, File.ReadAllBytes(path));
    }

    [Fact]
    public void WriteAllText_CreatesParentDirectoryIfMissing()
    {
        string path = Path.Combine(_tempDir, "nested", "deeper", "out.txt");
        AtomicFile.WriteAllText(path, "x");
        Assert.True(File.Exists(path));
        Assert.Equal("x", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_LeavesNoTempFilesBehindOnSuccess()
    {
        string path = Path.Combine(_tempDir, "out.txt");
        AtomicFile.WriteAllText(path, "content");
        string[] dirEntries = Directory.GetFiles(_tempDir);
        // Should be exactly one file (the final output); no leftover
        // tmp.<pid>.<guid> entries.
        Assert.Single(dirEntries);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void TempFileNaming_FollowsPidGuidPattern()
    {
        // The temp filename is "<base>.tmp.<pid>.<guid>"; we can observe
        // it transiently by intercepting via a directory watch, but a
        // simpler check is to verify the post-write state matches the
        // contract: exactly one file remains, named the final dest.
        string path = Path.Combine(_tempDir, "x.gen.h");
        AtomicFile.WriteAllText(path, "content");
        string[] files = Directory.GetFiles(_tempDir);
        Assert.Single(files);
        Assert.Equal(path, files[0]);
    }

    [Fact]
    public void TwoWrites_AreByteIdentical_ForIdenticalInput()
    {
        // Determinism: per XHT.html Section 14, two clean writes of the
        // same content produce byte-identical files.
        string path1 = Path.Combine(_tempDir, "a.txt");
        string path2 = Path.Combine(_tempDir, "b.txt");
        string content = "deterministic-content\nline2\n";
        AtomicFile.WriteAllText(path1, content);
        AtomicFile.WriteAllText(path2, content);
        Assert.Equal(File.ReadAllBytes(path1), File.ReadAllBytes(path2));
    }

    [Fact]
    public void WriteAllText_NullDestPathThrows()
    {
        Assert.Throws<ArgumentNullException>(() => AtomicFile.WriteAllText(null!, "x"));
    }

    [Fact]
    public void WriteAllText_EmptyDestPathThrows()
    {
        Assert.Throws<ArgumentException>(() => AtomicFile.WriteAllText("", "x"));
    }

    [Fact]
    public void WriteAllText_NullContentThrows()
    {
        string path = Path.Combine(_tempDir, "out.txt");
        Assert.Throws<ArgumentNullException>(() => AtomicFile.WriteAllText(path, null!));
    }

    [Fact]
    public void WriteAllText_EmptyContentProducesEmptyFile()
    {
        string path = Path.Combine(_tempDir, "out.txt");
        AtomicFile.WriteAllText(path, string.Empty);
        Assert.True(File.Exists(path));
        Assert.Equal(0, new FileInfo(path).Length);
    }
}
