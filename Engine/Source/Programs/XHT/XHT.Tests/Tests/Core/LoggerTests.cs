// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Simgenics.XPact.XHT.Core;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Core;

/// <summary>
/// Tests for <see cref="Logger"/>. Per <c>/Documents/XHT.html</c> Rev 7
/// Section 1.4 + Section 12: MSBuild text format on stderr, optional
/// streaming JSON channel, thread-safe counters.
/// </summary>
/// <remarks>
/// <see cref="Logger"/> is process-global, so tests share state. The
/// xUnit collection here serialises tests within the class so the JSON
/// channel + stderr override don't race against parallel tests.
/// </remarks>
[Collection(nameof(LoggerTests))]
[CollectionDefinition(nameof(LoggerTests), DisableParallelization = true)]
public sealed class LoggerTests : IDisposable
{
    public LoggerTests()
    {
        Logger.DisableJsonChannel();
        Logger.ResetCounters();
        Logger.__SetStderrForTesting(null);
    }

    public void Dispose()
    {
        Logger.DisableJsonChannel();
        Logger.ResetCounters();
        Logger.__SetStderrForTesting(null);
    }

    [Fact]
    public void Info_WritesMsBuildLineToStderr_AndDoesNotIncrementCounters()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        Logger.Info("hello, world");

        Assert.Contains("info XHT000: hello, world", sw.ToString());
        Assert.Equal(0, Logger.WarningCount);
        Assert.Equal(0, Logger.ErrorCount);
    }

    [Fact]
    public void Warning_IncrementsWarningCounter()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        Logger.Warning("warning text");

        Assert.Equal(1, Logger.WarningCount);
        Assert.Equal(0, Logger.ErrorCount);
        Assert.Contains("warning XHT000: warning text", sw.ToString());
    }

    [Fact]
    public void Error_IncrementsErrorCounter()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        Logger.Error("oh no");

        Assert.Equal(0, Logger.WarningCount);
        Assert.Equal(1, Logger.ErrorCount);
        Assert.Contains("error XHT000: oh no", sw.ToString());
    }

    [Fact]
    public void EmitDiagnostic_WithFile_FormatsAsMsBuildLine()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        Logger.EmitDiagnostic(new DiagnosticRecord(
            DiagnosticSeverity.Error,
            "XHT040",
            "Missing XGENERATED_BODY().",
            File: "X.h",
            Line: 14,
            Column: 1));

        Assert.Equal(
            "X.h(14,1): error XHT040: Missing XGENERATED_BODY()." + Environment.NewLine,
            sw.ToString());
    }

    [Fact]
    public void Counters_AreCumulativeUntilReset()
    {
        Logger.Warning("w1");
        Logger.Warning("w2");
        Logger.Error("e1");
        Logger.Error("e2");
        Logger.Error("e3");
        Assert.Equal(2, Logger.WarningCount);
        Assert.Equal(3, Logger.ErrorCount);

        Logger.ResetCounters();
        Assert.Equal(0, Logger.WarningCount);
        Assert.Equal(0, Logger.ErrorCount);
    }

    [Fact]
    public void JsonChannel_IsOffByDefault()
    {
        Assert.False(Logger.IsJsonChannelEnabled);
    }

    [Fact]
    public void ConfigureJsonChannel_TurnsChannelOn_AndEmitWritesBareJson()
    {
        using StringWriter stderr = new();
        Logger.__SetStderrForTesting(stderr);
        using MemoryStream ms = new();
        using StreamWriter jsonWriter = new(ms, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Logger.ConfigureJsonChannel(jsonWriter, useSentinel: false, leaveOpen: true);

        Assert.True(Logger.IsJsonChannelEnabled);

        Logger.EmitDiagnostic(new DiagnosticRecord(
            DiagnosticSeverity.Error,
            "XHT062",
            "internal failure",
            File: "X.h",
            Line: 42,
            Column: 13));

        jsonWriter.Flush();
        string raw = Encoding.UTF8.GetString(ms.ToArray());

        // Bare JSON: no sentinel prefix on the line.
        Assert.DoesNotContain(Logger.SentinelPrefix, raw);
        Assert.EndsWith("\n", raw);
        // Parse round-trip.
        using JsonDocument doc = JsonDocument.Parse(raw.TrimEnd('\n'));
        Assert.Equal("XHT", doc.RootElement.GetProperty("tool").GetString());
        Assert.Equal("XHT062", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(42, doc.RootElement.GetProperty("line").GetInt32());
    }

    [Fact]
    public void ConfigureJsonChannel_Sentinel_PrefixesEveryLine()
    {
        using StringWriter stderr = new();
        Logger.__SetStderrForTesting(stderr);
        using MemoryStream ms = new();
        using StreamWriter jsonWriter = new(ms, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Logger.ConfigureJsonChannel(jsonWriter, useSentinel: true, leaveOpen: true);

        Logger.Error("first");
        Logger.Warning("second");

        jsonWriter.Flush();
        string raw = Encoding.UTF8.GetString(ms.ToArray());

        string[] lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        foreach (string line in lines)
        {
            Assert.StartsWith(Logger.SentinelPrefix, line);
        }
    }

    [Fact]
    public void DisableJsonChannel_TurnsChannelOff()
    {
        using MemoryStream ms = new();
        using StreamWriter w = new(ms);
        Logger.ConfigureJsonChannel(w, useSentinel: false, leaveOpen: true);
        Assert.True(Logger.IsJsonChannelEnabled);

        Logger.DisableJsonChannel();
        Assert.False(Logger.IsJsonChannelEnabled);
    }

    [Fact]
    public void DecideJsonChannelMode_ExplicitFd_ChoosesFd()
    {
        Assert.Equal(JsonChannelMode.Fd, Logger.DecideJsonChannelMode(3));
        Assert.Equal(JsonChannelMode.Fd, Logger.DecideJsonChannelMode(7));
    }

    [Fact]
    public void DecideJsonChannelMode_ZeroOrNegativeFd_FallsThroughToAutoDetect()
    {
        // The actual result depends on Console.IsOutputRedirected,
        // which is environment-dependent. We just verify the mode is
        // NOT Fd when no positive FD is supplied.
        JsonChannelMode mode = Logger.DecideJsonChannelMode(0);
        Assert.NotEqual(JsonChannelMode.Fd, mode);

        JsonChannelMode neg = Logger.DecideJsonChannelMode(-1);
        Assert.NotEqual(JsonChannelMode.Fd, neg);
    }

    [Fact]
    public void EmitDiagnostic_NullRecord_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Logger.EmitDiagnostic(null!));
    }

    [Fact]
    public void EmitDiagnostic_JsonChannelFailure_DoesNotBlockStderr()
    {
        // A failing JSON channel must not eat the stderr emit.
        using StringWriter stderr = new();
        Logger.__SetStderrForTesting(stderr);

        // Build a stream writer wrapping a stream that throws on every
        // write to simulate the JSON channel failing.
        using FailingStream failing = new();
        using StreamWriter jsonWriter = new(failing);
        Logger.ConfigureJsonChannel(jsonWriter, useSentinel: false, leaveOpen: true);

        Logger.Error("payload");

        // The stderr emit must have landed despite the JSON failure.
        Assert.Contains("error XHT000: payload", stderr.ToString());
    }

    private sealed class FailingStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
            => throw new IOException("simulated JSON channel failure");

        public override void Write(ReadOnlySpan<byte> buffer)
            => throw new IOException("simulated JSON channel failure");
    }
}
