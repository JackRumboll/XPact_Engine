// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim re-implementation of the EpicGames.Core.Log public surface.
// The full UE 5.9 Log.cs is ~1.6 kLoC and bound to LegacyEventLogger /
// LogEventParser / Microsoft.Extensions.Logging / Microsoft.Extensions.DependencyInjection.
// XPact only needs the static Trace*/WriteLine entrypoints for XBT/XHT bring-up.
// Behaviour: writes severity-prefixed lines to stdout / stderr with thread safety.
// This will be replaced by a structured logger in a later Phase 0 task once the
// engine logging contract is settled.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace XPact.Core.Logging
{
	/// <summary>
	/// Log Event Type. Values mirror Microsoft.Extensions.Logging.LogLevel for parity.
	/// </summary>
#pragma warning disable CA1027 // Mark enums with FlagsAttribute
	public enum LogEventType
#pragma warning restore CA1027 // Mark enums with FlagsAttribute
	{
		/// <summary>The log event is a fatal error</summary>
		Fatal = LogLevel.Critical,

		/// <summary>The log event is an error</summary>
		Error = LogLevel.Error,

		/// <summary>The log event is a warning</summary>
		Warning = LogLevel.Warning,

		/// <summary>Output the log event to the console</summary>
		Console = LogLevel.Information,

		/// <summary>Output the event to the on-disk log</summary>
		Log = LogLevel.Debug,

		/// <summary>The log event should only be displayed if verbose logging is enabled</summary>
		Verbose = LogLevel.Trace,

		/// <summary>The log event should only be displayed if very verbose logging is enabled</summary>
#pragma warning disable CA1069 // Enums values should not be duplicated
		VeryVerbose = LogLevel.Trace
#pragma warning restore CA1069 // Enums values should not be duplicated
	}

	/// <summary>
	/// Options for formatting messages
	/// </summary>
	[Flags]
	public enum LogFormatOptions
	{
		/// <summary>Format normally</summary>
		None = 0,

		/// <summary>
		/// Never write a severity prefix. Useful for pre-formatted messages that need to be in a particular format for, eg. the Visual Studio output window
		/// </summary>
		NoSeverityPrefix = 1,

		/// <summary>Do not output text to the console</summary>
		NoConsoleOutput = 2,
	}

	/// <summary>
	/// XPact static log facade. Mirrors the EpicGames.Core.Log public API at the surface
	/// level used by build tooling, but with a deliberately small implementation. The
	/// underlying sink is plain Console.Out / Console.Error gated by a static
	/// <see cref="OutputLevel"/> threshold.
	/// </summary>
	public static class Log
	{
		/// <summary>
		/// Minimum verbosity that will actually be written. Anything more verbose
		/// (i.e. of a numerically lower severity) is suppressed.
		/// </summary>
		public static LogEventType OutputLevel { get; set; } = LogEventType.Log;

		private static readonly object s_consoleLock = new();

		/// <summary>
		/// Converts a LogEventType into a log prefix. Only used when bLogSeverity is true.
		/// </summary>
		private static string GetSeverityPrefix(LogEventType severity)
		{
			// Note: Verbose and VeryVerbose share the same underlying value (LogLevel.Trace);
			// distinguishing between them requires explicit caller intent, so we collapse here.
			return severity switch
			{
				LogEventType.Fatal => "FATAL ERROR: ",
				LogEventType.Error => "ERROR: ",
				LogEventType.Warning => "WARNING: ",
				LogEventType.Console => "",
				LogEventType.Verbose => "VERBOSE: ",
				LogEventType.Log => "LOG: ",
				_ => "",
			};
		}

		[StringFormatMethod("format")]
		private static void WriteLinePrivate(LogEventType verbosity, LogFormatOptions formatOptions, string format, params object?[] args)
		{
			if (verbosity > OutputLevel)
			{
				// suppressed (Trace=0 is the most verbose, Critical=5 the least; we compare on enum-int)
				return;
			}

			if ((formatOptions & LogFormatOptions.NoConsoleOutput) != 0)
			{
				return;
			}

			string body = args.Length == 0
				? format
				: String.Format(CultureInfo.InvariantCulture, format, args);

			string line = (formatOptions & LogFormatOptions.NoSeverityPrefix) != 0
				? body
				: GetSeverityPrefix(verbosity) + body;

			lock (s_consoleLock)
			{
				if (verbosity <= LogEventType.Warning)
				{
					Console.Error.WriteLine(line);
				}
				else
				{
					Console.Out.WriteLine(line);
				}
			}
		}

		/// <summary>Conditionally writes a formatted line at the given verbosity.</summary>
		[StringFormatMethod("format")]
		public static void WriteLineIf(bool condition, LogEventType verbosity, string format, params object?[] args)
		{
			if (condition)
			{
				WriteLinePrivate(verbosity, LogFormatOptions.None, format, args);
			}
		}

		/// <summary>Writes a formatted line at the given verbosity.</summary>
		[StringFormatMethod("format")]
		public static void WriteLine(LogEventType verbosity, string format, params object?[] args)
		{
			WriteLinePrivate(verbosity, LogFormatOptions.None, format, args);
		}

		/// <summary>Writes a formatted line at the given verbosity with explicit formatting options.</summary>
		[StringFormatMethod("format")]
		public static void WriteLine(LogEventType verbosity, LogFormatOptions formatOptions, string format, params object?[] args)
		{
			WriteLinePrivate(verbosity, formatOptions, format, args);
		}

		/// <summary>Writes an error message.</summary>
		[StringFormatMethod("format")]
		public static void TraceError(string format, params object?[] args)
		{
			WriteLinePrivate(LogEventType.Error, LogFormatOptions.None, format, args);
		}

		/// <summary>Writes an error message tagged with a source file.</summary>
		[StringFormatMethod("format")]
		public static void TraceErrorTask(string file, string format, params object?[] args)
		{
			WriteLinePrivate(LogEventType.Error, LogFormatOptions.NoSeverityPrefix, "{0}: error: {1}", file, String.Format(CultureInfo.InvariantCulture, format, args));
		}

		/// <summary>Writes an error message tagged with a source file and line number.</summary>
		[StringFormatMethod("format")]
		public static void TraceErrorTask(string file, int line, string format, params object?[] args)
		{
			WriteLinePrivate(LogEventType.Error, LogFormatOptions.NoSeverityPrefix, "{0}({1}): error: {2}", file, line, String.Format(CultureInfo.InvariantCulture, format, args));
		}

		/// <summary>Writes a verbose informational message.</summary>
		[StringFormatMethod("format")]
		public static void TraceVerbose(string format, params object?[] args)
		{
			WriteLinePrivate(LogEventType.Verbose, LogFormatOptions.None, format, args);
		}

		/// <summary>Writes a plain informational message (no severity prefix).</summary>
		[StringFormatMethod("format")]
		public static void TraceInformation(string format, params object?[] args)
		{
			WriteLinePrivate(LogEventType.Console, LogFormatOptions.None, format, args);
		}

		/// <summary>Writes a warning message.</summary>
		[StringFormatMethod("format")]
		public static void TraceWarning(string format, params object?[] args)
		{
			WriteLinePrivate(LogEventType.Warning, LogFormatOptions.None, format, args);
		}

		/// <summary>Writes a warning message tagged with a source file.</summary>
		[StringFormatMethod("format")]
		public static void TraceWarningTask(string file, string format, params object?[] args)
		{
			WriteLinePrivate(LogEventType.Warning, LogFormatOptions.NoSeverityPrefix, "{0}: warning: {1}", file, String.Format(CultureInfo.InvariantCulture, format, args));
		}

		/// <summary>Writes a warning message tagged with a source file and line number.</summary>
		[StringFormatMethod("format")]
		public static void TraceWarningTask(string file, int line, string format, params object?[] args)
		{
			WriteLinePrivate(LogEventType.Warning, LogFormatOptions.NoSeverityPrefix, "{0}({1}): warning: {2}", file, line, String.Format(CultureInfo.InvariantCulture, format, args));
		}

		/// <summary>Writes a very-verbose informational message.</summary>
		[StringFormatMethod("format")]
		public static void TraceVeryVerbose(string format, params object?[] args)
		{
			WriteLinePrivate(LogEventType.VeryVerbose, LogFormatOptions.None, format, args);
		}

		/// <summary>Writes a message at the on-disk log level.</summary>
		[StringFormatMethod("format")]
		public static void TraceLog(string format, params object?[] args)
		{
			WriteLinePrivate(LogEventType.Log, LogFormatOptions.None, format, args);
		}
	}

	/// <summary>
	/// Marker attribute equivalent to JetBrains.StringFormatAttribute. Kept locally so we
	/// do not pull in the JetBrains.Annotations NuGet for this slim port.
	/// </summary>
	[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Method | AttributeTargets.Constructor)]
	internal sealed class StringFormatMethodAttribute : Attribute
	{
		public string FormatParameterName { get; }
		public StringFormatMethodAttribute(string formatParameterName)
		{
			FormatParameterName = formatParameterName;
		}
	}
}
