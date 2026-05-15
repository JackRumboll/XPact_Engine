// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Action representing a lib.exe invocation to produce a static library
// (e.g. XAutoRTFM.lib). Phase 1 Task 1.0a adds this for the dependency-module
// pipeline: when the launch module depends on another module that compiles to
// .obj files, those .obj files are archived into a .lib that the final link
// step then consumes.

using XPact.Core.IO;

namespace XBT.Actions
{
	/// <summary>
	/// Static-library archive action. CommandPath points at lib.exe.
	/// </summary>
	public sealed class LibAction : ProcessAction
	{
		/// <summary>The .lib produced.</summary>
		public FileReference? OutputFile { get; set; }

		/// <summary>Construct a lib action.</summary>
		public LibAction()
		{
			ActionType = ActionType.Link;
			CommandDescription = "Lib";
		}
	}
}
