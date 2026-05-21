// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Simgenics.XPact.XHT.Core;

/// <summary>
/// Process-global structured logger for XHT. Mirrors XBT.Core's logger
/// pattern (dual-channel emit to stderr in MSBuild diagnostic format +
/// optional streaming JSON channel) per <c>/Documents/XHT.html</c> Rev 5
/// Section 1.4 + Section 12.
/// </summary>
/// <remarks>
/// <para>
/// <b>MSBuild text format (Section 12.1).</b> Every emitted record is
/// formatted as <c>&lt;file&gt;(&lt;line&gt;,&lt;column&gt;): &lt;severity&gt; XHT&lt;NNN&gt;: &lt;message&gt;</c>
/// on stderr -- compatible with the MSBuild diagnostic regex IDEs and CI
/// dashboards use. Records without a file fall back to a leading-severity
/// form.
/// </para>
/// <para>
/// <b>JSON channel activation (Section 1.4 + Section 12.4).</b> The
/// channel is opt-in:
/// <list type="bullet">
///   <item><description>
///     Linux / Android: explicit opt-in via <c>-JsonFd=N</c> CLI flag at
///     <c>XHT.Entry</c> startup; default off.
///   </description></item>
///   <item><description>
///     Windows: auto-on when stdout is not a TTY
///     (<c>!Console.IsOutputRedirected</c>); records are stdout lines
///     prefixed with the sentinel <c>@@XHT-JSON@@ </c>.
///   </description></item>
/// </list>
/// In both modes, <see cref="ConfigureJsonChannel(StreamWriter, bool, bool)"/>
/// installs the writer; <see cref="DisableJsonChannel"/> tears it down.
/// </para>
/// <para>
/// <b>Counters.</b> <see cref="WarningCount"/> and <see cref="ErrorCount"/>
/// are thread-safe via <see cref="Interlocked"/>; XHT.Entry consults them
/// at session exit to compute the exit code (per XHT.html Section 12.6
/// the <c>-WarningsAsErrors</c> flag promotes warnings to errors).
/// </para>
/// <para>
/// <b>Atomicity.</b> Stderr and JSON writes for one record are emitted
/// under a single lock so concurrent threads' diagnostics do not
/// interleave at the line level.
/// </para>
/// </remarks>
public static class Logger
{
    /// <summary>
    /// Windows / Linux-fallback sentinel prefix on stdout lines per
    /// <c>/Documents/XHT.html</c> Rev 5 Section 1.4. Includes the
    /// trailing space that delimits the sentinel from the JSON payload.
    /// </summary>
    public const string SentinelPrefix = "@@XHT-JSON@@ ";

    private static readonly object s_writeGate = new();
    private static StreamWriter? s_jsonWriter;
    private static bool s_jsonWriterUsesSentinel;
    private static bool s_jsonWriterLeaveOpen = true;
    private static TextWriter? s_stderrOverride;

    private static long s_warningCount;
    private static long s_errorCount;

    /// <summary>
    /// Cumulative count of warning-severity diagnostics emitted through
    /// this logger since process start (or the last
    /// <see cref="ResetCounters"/>).
    /// </summary>
    public static long WarningCount => Interlocked.Read(ref s_warningCount);

    /// <summary>
    /// Cumulative count of error-severity diagnostics emitted through
    /// this logger since process start (or the last
    /// <see cref="ResetCounters"/>).
    /// </summary>
    public static long ErrorCount => Interlocked.Read(ref s_errorCount);

    /// <summary>True iff the streaming JSON channel is currently open.</summary>
    public static bool IsJsonChannelEnabled
    {
        get { lock (s_writeGate) { return s_jsonWriter is not null; } }
    }

    // ---------------------------------------------------------------------
    // Public diagnostic API.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Emit an info-severity diagnostic with no source-anchor.
    /// Composes the message via
    /// <see cref="string.Format(IFormatProvider,string,object?[])"/> with
    /// <see cref="CultureInfo.InvariantCulture"/>.
    /// </summary>
    /// <param name="message">Format string.</param>
    /// <param name="args">Format arguments. Empty for literal messages.</param>
    public static void Info(string message, params object?[] args)
    {
        EmitDiagnostic(new DiagnosticRecord(
            DiagnosticSeverity.Info,
            "XHT000",
            Compose(message, args)));
    }

    /// <summary>
    /// Emit a warning-severity diagnostic with no source-anchor.
    /// Increments <see cref="WarningCount"/>.
    /// </summary>
    /// <param name="message">Format string.</param>
    /// <param name="args">Format arguments. Empty for literal messages.</param>
    public static void Warning(string message, params object?[] args)
    {
        EmitDiagnostic(new DiagnosticRecord(
            DiagnosticSeverity.Warning,
            "XHT000",
            Compose(message, args)));
    }

    /// <summary>
    /// Emit an error-severity diagnostic with no source-anchor.
    /// Increments <see cref="ErrorCount"/>.
    /// </summary>
    /// <param name="message">Format string.</param>
    /// <param name="args">Format arguments. Empty for literal messages.</param>
    public static void Error(string message, params object?[] args)
    {
        EmitDiagnostic(new DiagnosticRecord(
            DiagnosticSeverity.Error,
            "XHT000",
            Compose(message, args)));
    }

    /// <summary>
    /// Emit a fully-formed <see cref="DiagnosticRecord"/> on both
    /// channels. Updates <see cref="WarningCount"/> /
    /// <see cref="ErrorCount"/> per the record's
    /// <see cref="DiagnosticRecord.Severity"/>.
    /// </summary>
    /// <param name="record">The diagnostic to emit. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="record"/> is null.</exception>
    public static void EmitDiagnostic(DiagnosticRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        switch (record.Severity)
        {
            case DiagnosticSeverity.Warning:
                Interlocked.Increment(ref s_warningCount);
                break;
            case DiagnosticSeverity.Error:
                Interlocked.Increment(ref s_errorCount);
                break;
            default:
                break;
        }

        string humanLine = record.FormatMsBuild();
        TextWriter stderr = s_stderrOverride ?? Console.Error;

        lock (s_writeGate)
        {
            stderr.WriteLine(humanLine);
            stderr.Flush();

            if (s_jsonWriter is { } w)
            {
                try
                {
                    if (s_jsonWriterUsesSentinel)
                    {
                        w.Write(SentinelPrefix);
                    }
                    record.WriteJson(w);
                    w.Flush();
                }
                catch (Exception)
                {
                    // Best-effort: a JSON channel write failure must
                    // never block the stderr emit (already done above)
                    // or fail the build. Swallow and continue.
                }
            }
        }
    }

    // ---------------------------------------------------------------------
    // JSON channel configuration. Called by XHT.Entry at startup.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Install a <see cref="StreamWriter"/> as the streaming JSON channel.
    /// Records emit as single-line JSON per
    /// <c>/Documents/XHT.html</c> Rev 5 Section 1.4. When
    /// <paramref name="useSentinel"/> is true, every line is prefixed
    /// with <see cref="SentinelPrefix"/> (Windows / Linux-fallback mode);
    /// when false, lines are bare JSON (FD-based mode).
    /// </summary>
    /// <param name="writer">
    /// The writer to install. Must be writable. The logger does not own
    /// the writer's lifetime by default; pass <paramref name="leaveOpen"/>
    /// false to have <see cref="DisableJsonChannel"/> dispose it.
    /// </param>
    /// <param name="useSentinel">
    /// When true, prefix every record line with
    /// <see cref="SentinelPrefix"/>. Set true on Windows + Linux-fallback
    /// paths where the JSON share's the stdout stream with progress text.
    /// </param>
    /// <param name="leaveOpen">
    /// When true (default), <see cref="DisableJsonChannel"/> does NOT
    /// dispose the writer; the caller retains ownership. When false, the
    /// logger disposes it on tear-down.
    /// </param>
    public static void ConfigureJsonChannel(StreamWriter writer, bool useSentinel = false, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(writer);

        StreamWriter? prior;
        bool priorLeaveOpen;
        lock (s_writeGate)
        {
            prior = s_jsonWriter;
            priorLeaveOpen = s_jsonWriterLeaveOpen;
            s_jsonWriter = writer;
            s_jsonWriterUsesSentinel = useSentinel;
            s_jsonWriterLeaveOpen = leaveOpen;
        }

        if (prior is not null && !priorLeaveOpen)
        {
            try { prior.Dispose(); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Tear down the JSON channel. Subsequent diagnostics emit only to
    /// stderr; JSON-side writes become no-ops.
    /// </summary>
    public static void DisableJsonChannel()
    {
        StreamWriter? writer;
        bool leaveOpen;
        lock (s_writeGate)
        {
            writer = s_jsonWriter;
            leaveOpen = s_jsonWriterLeaveOpen;
            s_jsonWriter = null;
            s_jsonWriterUsesSentinel = false;
            s_jsonWriterLeaveOpen = true;
        }

        if (writer is not null && !leaveOpen)
        {
            try { writer.Dispose(); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Reset <see cref="WarningCount"/> and <see cref="ErrorCount"/> to
    /// zero. Used by tests + by long-running tool sessions that want a
    /// per-module counter snapshot.
    /// </summary>
    public static void ResetCounters()
    {
        Interlocked.Exchange(ref s_warningCount, 0);
        Interlocked.Exchange(ref s_errorCount, 0);
    }

    // ---------------------------------------------------------------------
    // Test hooks. Exposed to XHT.Tests via InternalsVisibleTo (registered
    // on XHT.Core.csproj when the test project lands).
    // ---------------------------------------------------------------------

    /// <summary>
    /// Redirect stderr emit to a caller-supplied writer. Used by tests
    /// to capture diagnostics without globally rebinding
    /// <see cref="Console.Error"/>. Pass null to restore default
    /// behaviour (writes to <see cref="Console.Error"/>).
    /// </summary>
    internal static void __SetStderrForTesting(TextWriter? writer)
    {
        lock (s_writeGate)
        {
            s_stderrOverride = writer;
        }
    }

    private static string Compose(string message, object?[] args)
    {
        if (args is null || args.Length == 0)
        {
            return message;
        }
        return string.Format(CultureInfo.InvariantCulture, message, args);
    }

    /// <summary>
    /// Auto-detect the JSON channel activation mode per XHT.html
    /// Section 1.4. Returns the mode flag the caller (XHT.Entry) should
    /// apply when wiring the channel.
    /// </summary>
    /// <param name="jsonFd">
    /// CLI flag value <c>-JsonFd=N</c>; -1 or 0 means "not specified".
    /// </param>
    /// <returns>
    /// The activation decision. <see cref="JsonChannelMode.Disabled"/>
    /// when the channel should stay off; <see cref="JsonChannelMode.Fd"/>
    /// for explicit FD activation on POSIX; <see cref="JsonChannelMode.StdoutSentinel"/>
    /// for Windows / Linux-fallback when stdout is redirected.
    /// </returns>
    public static JsonChannelMode DecideJsonChannelMode(int jsonFd)
    {
        if (jsonFd > 0)
        {
            return JsonChannelMode.Fd;
        }

        if (OperatingSystem.IsWindows())
        {
            // Auto-on when stdout is not a TTY. Console.IsOutputRedirected
            // returns true when stdout has been piped / redirected (the
            // typical XBT-subprocess case); false when the user is at an
            // interactive console.
            if (Console.IsOutputRedirected)
            {
                return JsonChannelMode.StdoutSentinel;
            }
        }

        return JsonChannelMode.Disabled;
    }
}

/// <summary>
/// JSON channel activation mode per <c>/Documents/XHT.html</c> Rev 5
/// Section 1.4 + Section 12.4. Returned by
/// <see cref="Logger.DecideJsonChannelMode(int)"/>.
/// </summary>
public enum JsonChannelMode
{
    /// <summary>The JSON channel is off (default on interactive POSIX).</summary>
    Disabled,

    /// <summary>Explicit FD opt-in via <c>-JsonFd=N</c>. Bare JSON lines.</summary>
    Fd,

    /// <summary>Auto-on Windows / Linux-fallback. Sentinel-prefixed lines on stdout.</summary>
    StdoutSentinel,
}
