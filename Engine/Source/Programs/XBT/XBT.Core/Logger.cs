// Copyright Simgenics. All Rights Reserved.

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Simgenics.XPact.XBT.Core;

/// <summary>
/// Minimal structured logger. Phase 1 surface only -- the streaming JSON
/// channel per <c>/Documents/XBT.html</c> Section 21.2 is reserved here
/// and will be implemented in Phase 1.1.
/// </summary>
/// <remarks>
/// <para>
/// All <c>Console.Out</c> / <c>Console.Error</c> writes in XBT must go
/// through this logger so the streaming JSON channel hook (Phase 1.1)
/// can mirror every diagnostic into the IDE-subscriber channel without
/// retrofitting every call site.
/// </para>
/// </remarks>
public static class Logger
{
    private static readonly object s_writeGate = new();

    /// <summary>
    /// FD or sentinel (1 = stdout-with-sentinel-prefix on Windows; 3 =
    /// dedicated FD on Linux/Android; null = no JSON mirroring). Per
    /// <c>/Documents/XBT.html</c> Section 21.2.
    /// </summary>
    /// <remarks>
    /// TODO Phase 1.1: implement the streaming JSON channel writer.
    /// At present setting this records the FD but the writer is a no-op.
    /// </remarks>
    private static int? s_jsonChannelFd;

    /// <summary>Severity classes. Mirrors the JSON channel record's <c>level</c>.</summary>
    public enum Level
    {
        Info,
        Warning,
        Error,
    }

    /// <summary>Write an informational line to stderr.</summary>
    public static void Info(string message)
    {
        Write(Level.Info, message, exitCode: null);
    }

    /// <summary>Write a warning line to stderr.</summary>
    public static void Warning(string message)
    {
        Write(Level.Warning, message, exitCode: null);
    }

    /// <summary>
    /// Write an error line to stderr. Optionally records the exit code XBT
    /// should propagate when the surrounding mode completes; the recording
    /// is informational at this layer -- the actual exit propagation is
    /// owned by <c>XBT.Entry</c>'s Main per <c>/Documents/XBT.html</c>
    /// Section 1.3.
    /// </summary>
    public static void Error(string message, int? exitCode = null)
    {
        Write(Level.Error, message, exitCode);
    }

    /// <summary>
    /// Reserve the streaming JSON channel descriptor. Phase 1.1 will turn
    /// this into an actual writer; today it stores the value and returns
    /// without side effects.
    /// </summary>
    /// <param name="fdOrNull">
    /// On Linux/Android: a numeric file descriptor (typically 3). On
    /// Windows: 1 to enable the <c>@@XBT-JSON@@</c> stdout sentinel mode.
    /// Pass null to disable the channel.
    /// </param>
    public static void SetJsonChannel(int? fdOrNull)
    {
        s_jsonChannelFd = fdOrNull;
        // TODO Phase 1.1: open the FD, set up a serialized writer, mirror
        // every Write() call into a one-line JSON record per the schema
        // in /Documents/XBT.html Section 21.2.
    }

    /// <summary>True if the streaming JSON channel is currently active.</summary>
    public static bool IsJsonChannelEnabled => s_jsonChannelFd.HasValue;

    private static void Write(Level level, string message, int? exitCode)
    {
        // Compose without allocating LINQ overhead.
        StringBuilder sb = new(message.Length + 64);
        sb.Append(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        sb.Append(' ');
        sb.Append(level switch
        {
            Level.Info    => "info ",
            Level.Warning => "warn ",
            Level.Error   => "error",
            _             => "?    ",
        });
        sb.Append(' ');
        sb.Append(message);
        if (exitCode is int code)
        {
            sb.Append(" (exit=");
            sb.Append(code.ToString(CultureInfo.InvariantCulture));
            sb.Append(')');
        }

        lock (s_writeGate)
        {
            TextWriter writer = level == Level.Info ? Console.Out : Console.Error;
            writer.WriteLine(sb.ToString());
            writer.Flush();
        }

        // TODO Phase 1.1: mirror the record on the JSON channel if enabled.
        _ = s_jsonChannelFd;
    }
}
