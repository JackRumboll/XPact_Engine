// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Action representing PCH generation: cl.exe /Yc<header> compiles a single
// .cpp stub that #includes the PCH header. Side effects are a .pch file and
// a .obj file; the .obj must be linked into the final binary.

using XPact.Core.IO;

namespace XBT.Actions
{
	/// <summary>
	/// PCH generation action.
	/// </summary>
	public sealed class PCHGenerateAction : ProcessAction
	{
		/// <summary>The PCH header file being included.</summary>
		public FileReference? PCHHeader { get; set; }

		/// <summary>The stub .cpp file that #includes the header.</summary>
		public FileReference? StubSourceFile { get; set; }

		/// <summary>The .pch file produced.</summary>
		public FileReference? PCHFile { get; set; }

		/// <summary>The .obj file produced as a side effect.</summary>
		public FileReference? ObjectFile { get; set; }

		/// <summary>Construct a PCH-generate action.</summary>
		public PCHGenerateAction()
		{
			ActionType = ActionType.GeneratePCH;
			CommandDescription = "GeneratePCH";
		}
	}
}
