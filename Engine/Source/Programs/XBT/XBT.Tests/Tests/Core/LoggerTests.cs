// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Core;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Core;

/// <summary>
/// Tests for the streaming JSON error channel and the human stderr
/// surface per <c>/Documents/XBT.html</c> Rev 4 Section 21.2.
/// </summary>
/// <remarks>
/// <para>
/// The tests share <see cref="Logger"/> process-wide state (the JSON
/// stream, the once-only repo-root warning). This class is decorated
/// with the xUnit collection attribute so the runner serialises these
/// tests with respect to each other; xUnit's default is per-class
/// parallelism, but these tests need a stable global state.
/// </para>
/// </remarks>
[Collection(nameof(LoggerTests))]
[CollectionDefinition(nameof(LoggerTests), DisableParallelization = true)]
public sealed class LoggerTests : IDisposable
{
    public LoggerTests()
    {
        // Each test starts with the JSON channel disabled. Tests opt in
        // explicitly via __SetJsonStreamForTesting.
        Logger.DisableJsonChannel();
        Logger.__ResetWarningLatchForTesting();
    }

    public void Dispose()
    {
        Logger.DisableJsonChannel();
        Logger.__ResetWarningLatchForTesting();
        RepoRoot.ResetForTesting();
    }

    /// <summary>
    /// Capture a block of stderr output by temporarily redirecting
    /// <see cref="Console.Error"/> to a <see cref="StringWriter"/>.
    /// Restores the original writer on dispose.
    /// </summary>
    private sealed class StderrCapture : IDisposable
    {
        private readonly TextWriter _previous;
        public StringWriter Writer { get; }

        public StderrCapture()
        {
            _previous = Console.Error;
            Writer = new StringWriter();
            Console.SetError(Writer);
        }

        public string Captured => Writer.ToString();

        public void Dispose()
        {
            Console.SetError(_previous);
            Writer.Dispose();
        }
    }

    // -----------------------------------------------------------------
    // 1. DiagnosticRecord serialises to camelCase JSON.
    // -----------------------------------------------------------------
    [Fact]
    public void DiagnosticRecord_SerializesToCamelCaseJson()
    {
        using MemoryStream ms = new();
        Logger.__SetJsonStreamForTesting(ms, leaveOpen: true, usesSentinel: false);

        Logger.Emit(new DiagnosticRecord
        {
            Timestamp = new DateTimeOffset(2026, 5, 19, 14, 0, 42, TimeSpan.Zero),
            Level     = DiagnosticLevel.Error,
            Message   = "undefined reference to XSleef_sin",
            Action    = "compile",
            Module    = "XScoring",
            File      = "/no/such/path/XScoring.cpp",
            Line      = 42,
            Column    = 7,
            Tier      = "Engine",
            SimPath   = true,
            ExitCode  = 70,
        });

        string json = ReadAndTrim(ms);
        // Field names match the example in Section 21.2 verbatim.
        Assert.Contains("\"timestamp\":", json, StringComparison.Ordinal);
        Assert.Contains("\"level\":\"error\"", json, StringComparison.Ordinal);
        Assert.Contains("\"message\":", json, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"compile\"", json, StringComparison.Ordinal);
        Assert.Contains("\"module\":\"XScoring\"", json, StringComparison.Ordinal);
        Assert.Contains("\"file\":", json, StringComparison.Ordinal);
        Assert.Contains("\"line\":42", json, StringComparison.Ordinal);
        Assert.Contains("\"column\":7", json, StringComparison.Ordinal);
        Assert.Contains("\"tier\":\"Engine\"", json, StringComparison.Ordinal);
        Assert.Contains("\"simpath\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"exitCode\":70", json, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // 2. Sparse records omit null fields.
    // -----------------------------------------------------------------
    [Fact]
    public void DiagnosticRecord_NullFieldsOmitted()
    {
        using MemoryStream ms = new();
        Logger.__SetJsonStreamForTesting(ms, leaveOpen: true, usesSentinel: false);

        Logger.Emit(new DiagnosticRecord
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level     = DiagnosticLevel.Info,
            Message   = "hello",
        });

        string json = ReadAndTrim(ms);
        // Only required fields plus the message should appear.
        Assert.Contains("\"timestamp\":", json, StringComparison.Ordinal);
        Assert.Contains("\"level\":\"info\"", json, StringComparison.Ordinal);
        Assert.Contains("\"message\":\"hello\"", json, StringComparison.Ordinal);
        // Sparse: every optional field must be absent.
        Assert.DoesNotContain("\"action\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"module\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"file\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"line\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"column\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"tier\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"simpath\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"exitCode\"", json, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // 3. Human stderr carries the message.
    // -----------------------------------------------------------------
    [Fact]
    public void Logger_HumanStderr_HasMessage()
    {
        using StderrCapture cap = new();
        Logger.Info("hello");
        Assert.Contains("hello", cap.Captured, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // 4. JSON channel writes one line per diagnostic.
    // -----------------------------------------------------------------
    [Fact]
    public void Logger_JsonChannel_WritesOneLine()
    {
        using MemoryStream ms = new();
        Logger.__SetJsonStreamForTesting(ms, leaveOpen: true, usesSentinel: false);

        Logger.Info("solo");

        string raw = Encoding.UTF8.GetString(ms.ToArray());
        // Exactly one line terminated by a single newline.
        string[] lines = raw.Split('\n', StringSplitOptions.None);
        // Split produces an empty trailing entry after the final newline.
        Assert.Equal(2, lines.Length);
        Assert.Equal(string.Empty, lines[1]);

        // The non-empty line must be a valid JSON object containing "solo".
        using JsonDocument doc = JsonDocument.Parse(lines[0]);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.Equal("solo", doc.RootElement.GetProperty("message").GetString());
    }

    // -----------------------------------------------------------------
    // 5. Both channels populated by the same call.
    // -----------------------------------------------------------------
    [Fact]
    public void Logger_JsonChannel_AppearsAlongsideStderr()
    {
        using StderrCapture cap = new();
        using MemoryStream ms = new();
        Logger.__SetJsonStreamForTesting(ms, leaveOpen: true, usesSentinel: false);

        Logger.Warning("dual-stream");

        Assert.Contains("dual-stream", cap.Captured, StringComparison.Ordinal);

        string jsonLine = ReadAndTrim(ms);
        using JsonDocument doc = JsonDocument.Parse(jsonLine);
        Assert.Equal("dual-stream", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal("warning", doc.RootElement.GetProperty("level").GetString());
    }

    // -----------------------------------------------------------------
    // 6. JSON channel disabled by default; stderr still works.
    // -----------------------------------------------------------------
    [Fact]
    public void Logger_JsonChannel_Disabled()
    {
        // Constructor disabled the channel. Confirm IsJsonChannelEnabled.
        Assert.False(Logger.IsJsonChannelEnabled);

        using StderrCapture cap = new();
        Logger.Info("stderr-only");

        Assert.Contains("stderr-only", cap.Captured, StringComparison.Ordinal);
        Assert.False(Logger.IsJsonChannelEnabled);
    }

    // -----------------------------------------------------------------
    // 7. Thread-safe atomicity: 100 concurrent emits, each line valid JSON.
    // -----------------------------------------------------------------
    [Fact]
    public void Logger_Thread_Safe_Atomicity()
    {
        using MemoryStream ms = new();
        Logger.__SetJsonStreamForTesting(ms, leaveOpen: true, usesSentinel: false);

        const int threadCount = 10;
        const int perThread = 10;
        using CountdownEvent ready = new(threadCount);
        using ManualResetEventSlim go = new(initialState: false);

        Thread[] threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int tid = t;
            threads[t] = new Thread(() =>
            {
                ready.Signal();
                go.Wait();
                for (int i = 0; i < perThread; i++)
                {
                    Logger.Info($"thread {tid} message {i}");
                }
            });
            threads[t].Start();
        }

        ready.Wait();
        go.Set();
        foreach (Thread thread in threads) { thread.Join(); }

        string raw = Encoding.UTF8.GetString(ms.ToArray());
        string[] lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(threadCount * perThread, lines.Length);

        // Each line must parse cleanly as a complete JSON object with
        // a message field -- no interleaving across threads.
        foreach (string line in lines)
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            string msg = doc.RootElement.GetProperty("message").GetString()!;
            Assert.StartsWith("thread ", msg, StringComparison.Ordinal);
        }
    }

    // -----------------------------------------------------------------
    // 8. File path canonicalisation: repo-relative when under a tier root.
    // -----------------------------------------------------------------
    [Fact]
    public void Logger_FilePathCanonicalization_RepoRelative()
    {
        using MemoryStream ms = new();
        Logger.__SetJsonStreamForTesting(ms, leaveOpen: true, usesSentinel: false);

        // Prime the repo root to a known fixture so the test works
        // independently of where the test runner started.
        string fakeRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "XBT.Tests.FakeRepo"));
        RepoRoot.SetForTesting(fakeRoot);

        string absolute = Path.Combine(fakeRoot, "Engine", "Source", "Runtime", "XScoring", "Private", "XScoring.cpp");
        Logger.Error(
            "undefined reference",
            exitCode: 70,
            new DiagnosticContext
            {
                Action = "compile",
                Module = "XScoring",
                File   = absolute,
                Line   = 42,
            });

        string jsonLine = ReadAndTrim(ms);
        using JsonDocument doc = JsonDocument.Parse(jsonLine);
        string file = doc.RootElement.GetProperty("file").GetString()!;

        // Always forward-slash; relative under Engine/.
        Assert.Equal("Engine/Source/Runtime/XScoring/Private/XScoring.cpp", file);
    }

    // -----------------------------------------------------------------
    // 9. File path canonicalisation: fall back to absolute when no repo.
    // -----------------------------------------------------------------
    [Fact]
    public void Logger_FilePathCanonicalization_FallbackAbsolute()
    {
        using MemoryStream ms = new();
        Logger.__SetJsonStreamForTesting(ms, leaveOpen: true, usesSentinel: false);

        // Explicitly null root so the canonicaliser must fall back.
        RepoRoot.SetForTesting(null);

        string absolute = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "OutsideAnyRepo", "Engine", "Source", "stray.cpp"));
        Logger.Error(
            "stray diagnostic",
            exitCode: 1,
            new DiagnosticContext { File = absolute });

        string raw = Encoding.UTF8.GetString(ms.ToArray());
        // Two lines: the one-time warning, then the diagnostic. Or
        // just the diagnostic if no warning fires (latch already
        // tripped). Either way, the LAST line contains "stray".
        string[] lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string diagLine = lines.First(l => l.Contains("stray diagnostic", StringComparison.Ordinal));
        using JsonDocument doc = JsonDocument.Parse(diagLine);
        string file = doc.RootElement.GetProperty("file").GetString()!;

        // Forward-slash, but the full absolute path. The path includes
        // the OS-specific drive/root prefix that GetFullPath produced.
        Assert.True(
            Path.IsPathRooted(file),
            $"Expected absolute fallback, got '{file}'.");
        Assert.Contains("Engine/Source/stray.cpp", file, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', file);
    }

    // -----------------------------------------------------------------
    // 10. Windows sentinel-mode prefix is present.
    // -----------------------------------------------------------------
    [Fact]
    public void Logger_WindowsSentinel_PrefixPresent()
    {
        using MemoryStream ms = new();
        Logger.__SetJsonStreamForTesting(ms, leaveOpen: true, usesSentinel: true);

        Logger.Info("sentinel-line");

        string raw = Encoding.UTF8.GetString(ms.ToArray());
        Assert.StartsWith(Logger.SentinelPrefix, raw, StringComparison.Ordinal);

        // Strip the sentinel and verify the remainder is one valid JSON
        // line (followed by a single newline).
        string afterSentinel = raw[Logger.SentinelPrefix.Length..];
        string[] lines = afterSentinel.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        using JsonDocument doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("sentinel-line", doc.RootElement.GetProperty("message").GetString());
    }

    // -----------------------------------------------------------------
    // 11. ExitCode rules: only valid on Error.
    // -----------------------------------------------------------------
    [Fact]
    public void Logger_ExitCode_OnErrorOnly()
    {
        using MemoryStream ms = new();
        Logger.__SetJsonStreamForTesting(ms, leaveOpen: true, usesSentinel: false);

        // Info / Warning: never carry an exit code.
        Logger.Info("info-no-exit");
        Logger.Warning("warn-no-exit");

        // Error CAN carry an exit code.
        Logger.Error("err-with-exit", exitCode: 70);

        // Explicit Emit() with a non-Error level and a non-null ExitCode
        // is rejected.
        Assert.Throws<ArgumentException>(() =>
        {
            Logger.Emit(new DiagnosticRecord
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level     = DiagnosticLevel.Info,
                Message   = "should-throw",
                ExitCode  = 10,
            });
        });

        string raw = Encoding.UTF8.GetString(ms.ToArray());
        string[] lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);

        using JsonDocument info  = JsonDocument.Parse(lines[0]);
        using JsonDocument warn  = JsonDocument.Parse(lines[1]);
        using JsonDocument err   = JsonDocument.Parse(lines[2]);

        Assert.False(info.RootElement.TryGetProperty("exitCode", out _));
        Assert.False(warn.RootElement.TryGetProperty("exitCode", out _));
        Assert.Equal(70, err.RootElement.GetProperty("exitCode").GetInt32());
    }

    private static string ReadAndTrim(MemoryStream ms)
    {
        return Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\n', '\r');
    }
}
