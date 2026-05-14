// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealBuildTool.IToolMode. The UE 5.9 version uses a generic
// IToolMode<T> for static-side metadata and a ToolModeOptions flag enum to
// gate startup systems (XmlConfig, build-platform registration, etc.).
// XBT Task 0.2 only dispatches between -build and -genproject, so we drop the
// generic and flags and just expose an async ExecuteAsync entry point.

using System.Threading.Tasks;

namespace XBT.Modes
{
	/// <summary>
	/// Base interface for standalone XBT modes. Modes are selected by inspecting
	/// the leading verb of the command line (e.g. -build, -genproject).
	/// </summary>
	public interface IToolMode
	{
		/// <summary>The name of the mode (matches the CLI verb without the leading dash).</summary>
		string Name { get; }

		/// <summary>
		/// Entry point. Returns the process exit code.
		/// </summary>
		Task<int> ExecuteAsync(string[] args);
	}
}
