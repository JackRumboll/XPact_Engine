// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Action representing a single cl.exe invocation (one .cpp -> one .obj).

using XPact.Core.IO;

namespace XBT.Actions
{
	/// <summary>
	/// Compile-a-single-source-file action. The CommandPath should point at
	/// cl.exe (MSVC) and CommandArguments contains the response-file or inline
	/// switches.
	/// </summary>
	public sealed class CompileAction : ProcessAction
	{
		/// <summary>The .cpp file being compiled.</summary>
		public FileReference? SourceFile { get; set; }

		/// <summary>The .obj output.</summary>
		public FileReference? ObjectFile { get; set; }

		/// <summary>Construct a compile action.</summary>
		public CompileAction()
		{
			ActionType = ActionType.Compile;
			CommandDescription = "Compile";
		}
	}
}
