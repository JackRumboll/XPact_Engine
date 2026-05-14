// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Minimal local ILogger surface for XPact tooling. Mirrors the most-used
// subset of Microsoft.Extensions.Logging.ILogger so that ported EpicGames.Core
// code (FileUtils, Log, etc.) compiles without referencing MS.Extensions.Logging.
// If we later decide to standardize on MS.Extensions.Logging, this interface
// can become a thin wrapper.

using System;

namespace XPact.Core.Logging
{
	/// <summary>
	/// Severity of a log entry. Values mirror Microsoft.Extensions.Logging.LogLevel
	/// so downstream tooling can move between the two without changes.
	/// </summary>
	public enum LogLevel
	{
		/// <summary>Logs that contain the most detailed messages.</summary>
		Trace = 0,
		/// <summary>Logs that are used for interactive investigation during development.</summary>
		Debug = 1,
		/// <summary>Logs that track the general flow of the application.</summary>
		Information = 2,
		/// <summary>Logs that highlight an abnormal or unexpected event.</summary>
		Warning = 3,
		/// <summary>Logs that highlight when the current flow of execution is stopped due to a failure.</summary>
		Error = 4,
		/// <summary>Logs that describe an unrecoverable application or system crash.</summary>
		Critical = 5,
		/// <summary>Not used for writing log messages. Specifies that a logging category should not write any messages.</summary>
		None = 6,
	}

	/// <summary>
	/// Identifies a specific log entry.
	/// </summary>
	public readonly struct EventId : IEquatable<EventId>
	{
		/// <summary>The numeric identifier for this event.</summary>
		public int Id { get; }
		/// <summary>The human-readable name for this event.</summary>
		public string? Name { get; }

		/// <summary>Construct a new event id.</summary>
		public EventId(int id, string? name = null)
		{
			Id = id;
			Name = name;
		}

		/// <inheritdoc/>
		public bool Equals(EventId other) => Id == other.Id;
		/// <inheritdoc/>
		public override bool Equals(object? obj) => obj is EventId other && Equals(other);
		/// <inheritdoc/>
		public override int GetHashCode() => Id;
		/// <inheritdoc/>
		public override string ToString() => Name ?? Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

		/// <summary>Equality operator.</summary>
		public static bool operator ==(EventId left, EventId right) => left.Equals(right);
		/// <summary>Inequality operator.</summary>
		public static bool operator !=(EventId left, EventId right) => !left.Equals(right);
		/// <summary>Implicit conversion from int.</summary>
		public static implicit operator EventId(int id) => new(id);
	}

	/// <summary>
	/// Minimal logging abstraction used by XPact.Core.
	/// Sinks (console, file, JSON) wire up to this interface.
	/// </summary>
	public interface ILogger
	{
		/// <summary>
		/// Whether the given log level is enabled on this logger.
		/// </summary>
		bool IsEnabled(LogLevel level);

		/// <summary>
		/// Write a log entry.
		/// </summary>
		/// <param name="level">Severity of the entry.</param>
		/// <param name="eventId">Identifier for the event.</param>
		/// <param name="exception">Optional exception associated with the entry.</param>
		/// <param name="message">Pre-formatted log message.</param>
		void Log(LogLevel level, EventId eventId, Exception? exception, string message);
	}

	/// <summary>
	/// Extension methods to make ILogger more ergonomic.
	/// </summary>
	public static class LoggerExtensions
	{
		/// <summary>Log a trace-level message.</summary>
		public static void LogTrace(this ILogger logger, string message) => logger.Log(LogLevel.Trace, default, null, message);

		/// <summary>Log a debug-level message.</summary>
		public static void LogDebug(this ILogger logger, string message) => logger.Log(LogLevel.Debug, default, null, message);

		/// <summary>Log an information-level message.</summary>
		public static void LogInformation(this ILogger logger, string message) => logger.Log(LogLevel.Information, default, null, message);

		/// <summary>Log a warning-level message.</summary>
		public static void LogWarning(this ILogger logger, string message) => logger.Log(LogLevel.Warning, default, null, message);

		/// <summary>Log an error-level message.</summary>
		public static void LogError(this ILogger logger, string message) => logger.Log(LogLevel.Error, default, null, message);

		/// <summary>Log an error-level message with an associated exception.</summary>
		public static void LogError(this ILogger logger, Exception exception, string message) => logger.Log(LogLevel.Error, default, exception, message);

		/// <summary>Log a critical-level message.</summary>
		public static void LogCritical(this ILogger logger, string message) => logger.Log(LogLevel.Critical, default, null, message);
	}
}
