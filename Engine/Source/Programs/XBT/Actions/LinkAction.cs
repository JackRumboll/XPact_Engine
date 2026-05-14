// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Action representing the link.exe invocation.

using XPact.Core.IO;

namespace XBT.Actions
{
	/// <summary>
	/// Link action. CommandPath points at link.exe.
	/// </summary>
	public sealed class LinkAction : ProcessAction
	{
		/// <summary>The .exe / .dll / .lib produced.</summary>
		public FileReference? OutputFile { get; set; }

		/// <summary>Construct a link action.</summary>
		public LinkAction()
		{
			ActionType = ActionType.Link;
			CommandDescription = "Link";
		}
	}
}
