// Copyright Simgenics. All Rights Reserved.

using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Simgenics.XPact.XBT.Core;

/// <summary>
/// Process-wide structured logger. Every diagnostic emitted through this
/// type is dual-channelled:
/// <list type="bullet">
///   <item><description>
///     A human-readable line on stderr -- the format compatible with the
///     MSBuild <c>file(line,col): level CODE: message</c> regex IDEs and
///     CI dashboards use (per <c>/Documents/XBT.html</c> Rev 4
///     Section 21.1).
///   </description></item>
///   <item><description>
///     A one-line JSON record on the streaming JSON channel (Section 21.2)
///     when that channel is configured. The channel is either a Linux/
///     Android file descriptor (<c>-JsonFd=N</c> CLI flag) or a stdout
///     stream prefixed with the sentinel <c>@@XBT-JSON@@ </c>
///     (Windows-default and Linux fallback).
///   </description></item>
/// </list>
/// Both writes happen under a single lock so the stderr line and the
/// JSON record for one diagnostic are atomic with respect to other
/// threads' diagnostics. The Phase 1.2 action graph executor is
/// parallel; this guarantee is what lets IDE subscribers reconstruct
/// the diagnostic stream without per-record interleave.
/// </summary>
/// <remarks>
/// <para>
/// <b>Determinism.</b> Diagnostic ordering across builds is not
/// guaranteed (parallelism). The JSON record format itself <em>is</em>
/// deterministic -- the configuration on
/// <see cref="JsonSerializerOptions"/> is fixed and the same record
/// always produces byte-identical output.
/// </para>
/// <para>
/// <b>Failure isolation.</b> A failure to write to the JSON channel
/// never blocks the stderr line from being emitted; the channel is
/// best-effort. Failures to write to stderr are unrecoverable and
/// propagate.
/// </para>
/// </remarks>
public static class Logger
{
    /// <summary>
    /// Windows / Linux-fallback line prefix per <c>/Documents/XBT.html</c>
    /// Rev 4 Section 21.2. Includes the trailing space that delimits the
    /// sentinel from the JSON payload.
    /// </summary>
    public const string SentinelPrefix = "@@XBT-JSON@@ ";

    // Single gate that covers BOTH stderr and the JSON channel write so
    // a record's two outputs are atomic with respect to other threads.
    // Per the spec: don't optimise this with multiple locks; the cost
    // is negligible compared to the build itself.
    private static readonly object s_writeGate = new();

    // The JSON channel stream. Null = JSON channel is disabled (writes
    // become no-ops). Set via SetJsonChannel* APIs.
    private static Stream? s_jsonStream;
    private static bool s_jsonStreamLeaveOpen = true;
    private static bool s_jsonStreamUsesSentinel;
    private static bool s_repoRootWarningEmitted;

    private static readonly JsonSerializerOptions s_jsonOptions = BuildJsonOptions();

    private static JsonSerializerOptions BuildJsonOptions()
    {
        JsonSerializerOptions opts = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        opts.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        opts.Converters.Add(new ZuluDateTimeOffsetConverter());
        return opts;
    }

    /// <summary>
    /// Serialises <see cref="DateTimeOffset"/> as ISO 8601 with the
    /// <c>Z</c> suffix to match the example in
    /// <c>/Documents/XBT.html</c> Rev 4 Section 21.2
    /// (<c>"2026-05-19T14:00:42.123Z"</c>). Default System.Text.Json
    /// serialises with the explicit <c>+00:00</c> offset which the
    /// example does not use.
    /// </summary>
    private sealed class ZuluDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
            => reader.GetDateTimeOffset();

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options)
        {
            // Always UTC; the .ToUniversalTime() is a no-op for an
            // offset of zero but normalises any caller-supplied
            // non-UTC offset to UTC for the JSON record (operators
            // expect a single timezone on the channel).
            DateTime utc = value.UtcDateTime;
            // Round-trip "O" with the DateTimeKind.Utc kind produces
            // the Z suffix; the equivalent format string here keeps
            // the precision (7-digit fractional seconds) consistent.
            string iso = utc.ToString(
                "yyyy-MM-ddTHH:mm:ss.fffffffZ",
                System.Globalization.CultureInfo.InvariantCulture);
            writer.WriteStringValue(iso);
        }
    }

    // ---------------------------------------------------------------------
    // Public diagnostic API.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Emit a debug-severity diagnostic to stderr and the JSON channel.
    /// </summary>
    public static void Debug(string message, DiagnosticContext context = default)
        => Emit(BuildRecord(DiagnosticLevel.Debug, message, context, exitCode: null));

    /// <summary>
    /// Emit an info-severity diagnostic to stderr and the JSON channel.
    /// </summary>
    public static void Info(string message, DiagnosticContext context = default)
        => Emit(BuildRecord(DiagnosticLevel.Info, message, context, exitCode: null));

    /// <summary>
    /// Emit a warning-severity diagnostic to stderr and the JSON channel.
    /// </summary>
    public static void Warning(string message, DiagnosticContext context = default)
        => Emit(BuildRecord(DiagnosticLevel.Warning, message, context, exitCode: null));

    /// <summary>
    /// Emit an error-severity diagnostic to stderr and the JSON channel.
    /// </summary>
    /// <param name="message">Human-readable diagnostic text.</param>
    /// <param name="exitCode">
    /// Optional Toolchain Contract Rev 13 Section 13 exit code. When
    /// set, becomes the <c>exitCode</c> field on the JSON record.
    /// </param>
    /// <param name="context">Optional structured fields.</param>
    public static void Error(string message, int? exitCode = null, DiagnosticContext context = default)
        => Emit(BuildRecord(DiagnosticLevel.Error, message, context, exitCode));

    /// <summary>
    /// Emit a fully-formed <see cref="DiagnosticRecord"/>. Use this
    /// overload when the caller has every field ready and prefers an
    /// immutable record construction site over the field-by-field
    /// helpers above.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown if <see cref="DiagnosticRecord.ExitCode"/> is non-null
    /// on a non-error record. Exit codes belong on errors only.
    /// </exception>
    public static void Emit(DiagnosticRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.ExitCode is not null && record.Level != DiagnosticLevel.Error)
        {
            throw new ArgumentException(
                $"ExitCode is only valid on error-level records; got {record.Level} with ExitCode={record.ExitCode}.",
                nameof(record));
        }

        DiagnosticRecord canonical = record;
        if (record.File is not null)
        {
            string? canonicalFile = SafeCanonicalize(record.File);
            if (!ReferenceEquals(canonicalFile, record.File))
            {
                canonical = record with { File = canonicalFile };
            }
        }

        WriteAtomic(canonical);
    }

    // ---------------------------------------------------------------------
    // JSON channel configuration. Called by XBT.Entry at startup based on
    // the -JsonFd= and -JsonStdout= CLI flags per /Documents/XBT.html
    // Rev 4 Section 21.2.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Linux/Android: open the JSON channel against an inherited file
    /// descriptor. Default FD per Section 21.2 is 3 (IDE attaches the
    /// read end of a pipe before exec). A value of 0 (or any FD whose
    /// file handle cannot be opened) disables the channel.
    /// </summary>
    /// <remarks>
    /// On Windows, <see cref="OperatingSystem.IsWindows"/> is true and
    /// this method is a no-op -- Windows uses the stdout-with-sentinel
    /// path instead, configured via
    /// <see cref="SetJsonChannelStdoutSentinel"/>.
    /// </remarks>
    public static void SetJsonChannelFromFd(int fd)
    {
        if (fd <= 0)
        {
            DisableJsonChannel();
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            // Windows .NET does not expose Open() on a raw inherited
            // numeric FD the way POSIX does. The Section 21.2 fallback
            // for Windows is the stdout sentinel path. We silently
            // ignore -JsonFd on Windows; the launcher should pass
            // -JsonStdout=true instead.
            return;
        }

        try
        {
            SafeFileHandle handle = new((IntPtr)fd, ownsHandle: false);
            FileStream stream = new(handle, FileAccess.Write);
            SetJsonStream(stream, leaveOpen: false, usesSentinel: false);
        }
        catch (Exception ex)
        {
            // Probe failed -- the FD is not open / not writable. Channel
            // stays disabled. Surface as a warning so the launcher knows
            // its pipe wiring is wrong, then continue with stderr only.
            DisableJsonChannel();
            Warning(
                $"Failed to open JSON channel on FD {fd}: {ex.GetType().Name}: {ex.Message}. " +
                $"Continuing without the streaming JSON channel.");
        }
    }

    /// <summary>
    /// Windows / Linux fallback: emit JSON records on stdout prefixed
    /// with the sentinel <see cref="SentinelPrefix"/>. IDE consumers
    /// strip lines beginning with the sentinel to recover the JSON
    /// stream; non-prefixed stdout content is preserved for humans.
    /// </summary>
    /// <param name="enabled">
    /// True to enable sentinel-mode emission; false to disable the
    /// JSON channel entirely.
    /// </param>
    public static void SetJsonChannelStdoutSentinel(bool enabled)
    {
        if (!enabled)
        {
            DisableJsonChannel();
            return;
        }
        Stream stdout = Console.OpenStandardOutput();
        SetJsonStream(stdout, leaveOpen: true, usesSentinel: true);
    }

    /// <summary>
    /// Tear down the JSON channel. Subsequent diagnostics emit only to
    /// stderr; JSON-side writes become no-ops.
    /// </summary>
    public static void DisableJsonChannel()
    {
        Stream? stream;
        bool leaveOpen;
        lock (s_writeGate)
        {
            stream = s_jsonStream;
            leaveOpen = s_jsonStreamLeaveOpen;
            s_jsonStream = null;
            s_jsonStreamLeaveOpen = true;
            s_jsonStreamUsesSentinel = false;
        }
        if (stream is not null && !leaveOpen)
        {
            try { stream.Dispose(); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>True iff the streaming JSON channel is currently open.</summary>
    public static bool IsJsonChannelEnabled
    {
        get { lock (s_writeGate) { return s_jsonStream is not null; } }
    }

    // ---------------------------------------------------------------------
    // Internal test hooks. Exposed to XBT.Tests via InternalsVisibleTo.
    // Per /Documents/XBT.html Rev 4 Section 22.1: every unit test gets a
    // deterministic input, including the JSON channel destination.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Direct the JSON channel at an arbitrary <see cref="Stream"/>.
    /// Used by <c>XBT.Tests</c> to capture the stream into a
    /// <see cref="MemoryStream"/> without requiring an inherited FD or
    /// a real stdout intercept.
    /// </summary>
    /// <param name="stream">
    /// The destination stream (may be null to disable). The stream is
    /// not disposed by the logger when <paramref name="usesSentinel"/>
    /// is true or <paramref name="leaveOpen"/> is true.
    /// </param>
    /// <param name="leaveOpen">
    /// When true, <see cref="DisableJsonChannel"/> does not dispose
    /// the stream (test ownership). When false, the logger owns the
    /// lifetime.
    /// </param>
    /// <param name="usesSentinel">
    /// When true, every emitted line is prefixed with
    /// <see cref="SentinelPrefix"/> (Windows / Linux-fallback mode).
    /// </param>
    internal static void __SetJsonStreamForTesting(Stream? stream, bool leaveOpen = true, bool usesSentinel = false)
    {
        if (stream is null)
        {
            DisableJsonChannel();
            return;
        }
        SetJsonStream(stream, leaveOpen, usesSentinel);
    }

    /// <summary>
    /// Reset the one-time "repo root not found" warning latch so a
    /// follow-up test can re-exercise the warning path.
    /// </summary>
    internal static void __ResetWarningLatchForTesting()
    {
        lock (s_writeGate)
        {
            s_repoRootWarningEmitted = false;
        }
    }

    // ---------------------------------------------------------------------
    // Internals.
    // ---------------------------------------------------------------------

    private static void SetJsonStream(Stream stream, bool leaveOpen, bool usesSentinel)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // Tear down any prior channel before installing the new one.
        Stream? prior;
        bool priorLeaveOpen;
        lock (s_writeGate)
        {
            prior = s_jsonStream;
            priorLeaveOpen = s_jsonStreamLeaveOpen;
            s_jsonStream = stream;
            s_jsonStreamLeaveOpen = leaveOpen;
            s_jsonStreamUsesSentinel = usesSentinel;
        }
        if (prior is not null && !priorLeaveOpen)
        {
            try { prior.Dispose(); }
            catch { /* best-effort */ }
        }
    }

    private static DiagnosticRecord BuildRecord(
        DiagnosticLevel level,
        string message,
        DiagnosticContext context,
        int? exitCode)
    {
        // Guard the per-call invariant: only error-level records carry
        // an exit code. This is the same invariant Emit() enforces; we
        // check here too so the per-level helpers fail fast at their
        // own call site.
        if (exitCode is not null && level != DiagnosticLevel.Error)
        {
            throw new ArgumentException(
                $"ExitCode is only valid on error-level records; got {level} with ExitCode={exitCode}.",
                nameof(exitCode));
        }

        // Canonicalisation deliberately deferred to Emit() so the path
        // is touched exactly once per diagnostic. Calling Canonicalize
        // twice would resolve any already-relative output against the
        // current working directory and re-anchor the path to whichever
        // directory the process happens to be in.
        return new DiagnosticRecord
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level     = level,
            Message   = message ?? string.Empty,
            Action    = context.Action,
            Module    = context.Module,
            File      = context.File,
            Line      = context.Line,
            Column    = context.Column,
            Tier      = context.Tier,
            SimPath   = context.SimPath,
            ExitCode  = exitCode,
        };
    }

    private static string? SafeCanonicalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        string? canonical;
        try
        {
            canonical = RepoRoot.Canonicalize(path);
        }
        catch (Exception)
        {
            // Defence in depth -- RepoRoot.Canonicalize already swallows
            // its own exceptions, but if a future maintainer relaxes that
            // we still must not fail the diagnostic itself.
            return path;
        }

        // One-time warning if we are emitting a diagnostic with an
        // absolute path because no repo root was discovered. The
        // operator should know cross-machine reproducibility of these
        // records is compromised.
        if (canonical is not null
            && Path.IsPathRooted(canonical)
            && RepoRoot.GetRepoRoot() is null
            && !s_repoRootWarningEmitted)
        {
            // Latch under the gate to avoid duplicate warnings under
            // concurrent first-emits; the recursion through Warning is
            // safe because the latch is set BEFORE the warning emits.
            bool emitWarning = false;
            lock (s_writeGate)
            {
                if (!s_repoRootWarningEmitted)
                {
                    s_repoRootWarningEmitted = true;
                    emitWarning = true;
                }
            }
            if (emitWarning)
            {
                // Use the structured emit path directly so the warning
                // appears on the JSON channel too.
                WriteAtomic(new DiagnosticRecord
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Level     = DiagnosticLevel.Warning,
                    Message   = "Repo root (.git directory) not found above the current working directory; "
                              + "diagnostic file paths will be absolute and may not reproduce across machines.",
                });
            }
        }

        return canonical;
    }

    private static void WriteAtomic(DiagnosticRecord record)
    {
        string humanLine = FormatHuman(record);
        string jsonLine  = JsonSerializer.Serialize(record, s_jsonOptions);

        lock (s_writeGate)
        {
            // Stderr first so a JSON-channel failure does not eat the
            // human-readable diagnostic.
            Console.Error.WriteLine(humanLine);
            Console.Error.Flush();

            if (s_jsonStream is { } stream)
            {
                try
                {
                    if (s_jsonStreamUsesSentinel)
                    {
                        WriteUtf8(stream, SentinelPrefix);
                    }
                    WriteUtf8(stream, jsonLine);
                    WriteUtf8(stream, "\n");
                    stream.Flush();
                }
                catch (Exception)
                {
                    // Best-effort -- JSON channel failures must never
                    // block stderr or fail a build. We intentionally
                    // do not recurse via Logger.Warning here (would
                    // attempt another channel write and risk loop).
                }
            }
        }
    }

    private static void WriteUtf8(Stream stream, string s)
    {
        if (s.Length == 0)
        {
            return;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string FormatHuman(DiagnosticRecord record)
    {
        // Compose without LINQ. Format roughly mirrors MSBuild's
        // file(line,col): level CODE: message regex (Section 21.1) for
        // records that carry file/line info; falls back to a simpler
        // timestamp-prefixed line for records that do not.
        StringBuilder sb = new(record.Message.Length + 96);

        sb.Append(record.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        sb.Append(' ');
        sb.Append(LevelTag(record.Level));

        if (record.File is not null)
        {
            sb.Append(' ');
            sb.Append(record.File);
            if (record.Line is int line)
            {
                sb.Append('(');
                sb.Append(line.ToString(CultureInfo.InvariantCulture));
                if (record.Column is int col)
                {
                    sb.Append(',');
                    sb.Append(col.ToString(CultureInfo.InvariantCulture));
                }
                sb.Append(')');
            }
            sb.Append(':');
        }

        if (record.Module is not null)
        {
            sb.Append(" [");
            sb.Append(record.Module);
            sb.Append(']');
        }
        if (record.Action is not null)
        {
            sb.Append(" {");
            sb.Append(record.Action);
            sb.Append('}');
        }

        sb.Append(' ');
        sb.Append(record.Message);

        if (record.ExitCode is int code)
        {
            sb.Append(" (exit=");
            sb.Append(code.ToString(CultureInfo.InvariantCulture));
            sb.Append(')');
        }

        return sb.ToString();
    }

    private static string LevelTag(DiagnosticLevel level) => level switch
    {
        DiagnosticLevel.Debug   => "debug",
        DiagnosticLevel.Info    => "info ",
        DiagnosticLevel.Warning => "warn ",
        DiagnosticLevel.Error   => "error",
        _                       => "?    ",
    };
}

/// <summary>
/// Optional structured fields for a single diagnostic emission. Passed
/// as a default-able value type so the most common call site
/// (<c>Logger.Info("msg")</c>) does not pay any allocation overhead.
/// </summary>
/// <remarks>
/// <para>
/// Fields mirror the optional fields on <see cref="DiagnosticRecord"/>.
/// A null/zero field means "not set" and is omitted from the JSON
/// record per the
/// <see cref="JsonIgnoreCondition.WhenWritingNull"/> policy.
/// </para>
/// <para>
/// Use an object initialiser at call sites:
/// <code>
/// Logger.Error("undefined symbol", exitCode: 70, new DiagnosticContext
/// {
///     Action = "compile",
///     Module = "XScoring",
///     File   = "Engine/Source/Runtime/XScoring/Private/XScoring.cpp",
///     Line   = 42,
///     Column = 7,
///     Tier   = "Engine",
///     SimPath = true,
/// });
/// </code>
/// </para>
/// </remarks>
public readonly record struct DiagnosticContext
{
    public string? Action { get; init; }
    public string? Module { get; init; }
    public string? File { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public string? Tier { get; init; }
    public bool? SimPath { get; init; }
}
