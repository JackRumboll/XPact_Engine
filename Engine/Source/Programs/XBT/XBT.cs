// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// XBT (XPact Build Tool) entry point. Parses the leading verb (-build /
// -genproject) off the command line and dispatches to the matching mode.
// Everything else flows through the mode's argument parser.

using System;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using XBT.Modes;
using XPact.Build;
using XPact.Core.Logging;

namespace XBT
{
	/// <summary>
	/// XBT entry point.
	/// </summary>
	[SupportedOSPlatform("windows")]
	public static class XBT
	{
		/// <summary>
		/// Main entry point.
		/// </summary>
		public static async Task<int> Main(string[] args)
		{
			ArgumentNullException.ThrowIfNull(args);
			// Workaround for a Task 0.1 logger bug: WriteLinePrivate uses
			// "verbosity > OutputLevel" to suppress, but the integer scale runs
			// least-verbose-high. With OutputLevel=Log(1), Error(4) is suppressed.
			// Set to the highest value so nothing is gated out. Tracked as a
			// deviation in the Task 0.2 report; the real fix is a Task 0.1 edit
			// which the manager explicitly seals.
			Log.OutputLevel = LogEventType.Fatal;

			try
			{
				IToolMode mode = SelectMode(args);
				return await mode.ExecuteAsync(args).ConfigureAwait(false);
			}
			catch (BuildException ex)
			{
				Log.TraceError("[XBT] {0}", ex.Message);
				return 1;
			}
			catch (Exception ex)
			{
				Log.TraceError("[XBT] Unhandled exception: {0}", ex);
				return 1;
			}
		}

		private static IToolMode SelectMode(string[] args)
		{
			bool hasBuild = args.Any(a => a.Equals("-build", StringComparison.OrdinalIgnoreCase) || a.Equals("/build", StringComparison.OrdinalIgnoreCase));
			bool hasGen = args.Any(a => a.Equals("-genproject", StringComparison.OrdinalIgnoreCase) || a.Equals("/genproject", StringComparison.OrdinalIgnoreCase));

			if (hasGen)
			{
				return new GenerateProjectFilesMode();
			}
			if (hasBuild)
			{
				return new BuildMode();
			}
			throw new BuildException("No mode specified. Pass -build or -genproject as the first flag. Example: XBT.exe -build -target=HelloWorld -platform=Windows -arch=x64 -config=Development");
		}
	}
}
