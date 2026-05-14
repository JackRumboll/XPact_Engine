// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealBuildTool.BuildException. The original LogException helper
// depends on EpicGames.Core.ExceptionUtils, which is not in the Task 0.1 port set.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace XPact.Build
{
	/// <summary>
	/// Base class for exceptions thrown by XPact build tooling.
	/// </summary>
	[SuppressMessage("Naming", "CA1724:Type names should not match namespaces", Justification = "Matches Unreal naming for ported types")]
	public class BuildException : Exception
	{
		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="message">The error message to display.</param>
		public BuildException(string message)
			: base(message)
		{
		}

		/// <summary>
		/// Constructor which wraps another exception
		/// </summary>
		/// <param name="innerException">An inner exception to wrap</param>
		/// <param name="message">The error message to display.</param>
		public BuildException(Exception? innerException, string message)
			: base(message, innerException)
		{
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="format">Formatting string for the error message</param>
		/// <param name="arguments">Arguments for the formatting string</param>
		public BuildException(string format, params object?[] arguments)
			: base(String.Format(CultureInfo.InvariantCulture, format, arguments))
		{
		}

		/// <summary>
		/// Constructor which wraps another exception
		/// </summary>
		/// <param name="innerException">The inner exception being wrapped</param>
		/// <param name="format">Format for the message string</param>
		/// <param name="arguments">Format arguments</param>
		public BuildException(Exception innerException, string format, params object?[] arguments)
			: base(String.Format(CultureInfo.InvariantCulture, format, arguments), innerException)
		{
		}

		/// <summary>
		/// Returns the string representing the exception. Our build exceptions do not show the
		/// callstack since they are used to report known error conditions.
		/// </summary>
		/// <returns>Message for the exception</returns>
		public override string ToString()
		{
			return Message;
		}
	}
}
