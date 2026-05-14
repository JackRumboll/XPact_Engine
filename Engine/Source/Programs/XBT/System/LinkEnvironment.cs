// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealBuildTool.LinkEnvironment. Keeps the fields the cl/link
// command-line generator needs in Task 0.2: input objects, library paths and
// names, output binary, subsystem, debug flags.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using XPact.Build.Platform;
using XPact.Core.IO;

namespace XBT.BuildSystem
{
	/// <summary>
	/// Whether the produced binary is an .exe, .dll, or .lib.
	/// </summary>
	public enum LinkOutputType
	{
		/// <summary>Console / windows executable.</summary>
		Executable,
		/// <summary>Dynamic-link library.</summary>
		DynamicLibrary,
		/// <summary>Static library.</summary>
		StaticLibrary,
	}

	/// <summary>
	/// MSVC subsystem the linker should target.
	/// </summary>
	public enum LinkerSubsystem
	{
		/// <summary>Default - usually CONSOLE for Programs.</summary>
		Console,
		/// <summary>WINDOWS subsystem.</summary>
		Windows,
	}

	/// <summary>
	/// Snapshot of what the linker needs to produce the final binary.
	/// </summary>
	[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Matches Unreal field naming")]
	[SuppressMessage("Performance", "CA1002:Do not expose generic lists", Justification = "Matches UE5.9 API")]
	public sealed class LinkEnvironment
	{
		/// <summary>Target platform.</summary>
		public UnrealTargetPlatform Platform { get; set; } = UnrealTargetPlatform.Win64;

		/// <summary>Architecture being linked for.</summary>
		public UnrealArch Architecture { get; set; } = UnrealArch.X64;

		/// <summary>Build configuration.</summary>
		public UnrealTargetConfiguration Configuration { get; set; } = UnrealTargetConfiguration.Development;

		/// <summary>Kind of output the linker is producing.</summary>
		public LinkOutputType OutputType { get; set; } = LinkOutputType.Executable;

		/// <summary>MSVC linker subsystem (CONSOLE / WINDOWS).</summary>
		public LinkerSubsystem Subsystem { get; set; } = LinkerSubsystem.Console;

		/// <summary>Object files to link.</summary>
		public List<FileReference> InputFiles { get; } = [];

		/// <summary>Library directories.</summary>
		public List<DirectoryReference> LibraryPaths { get; } = [];

		/// <summary>System library names (linker resolves via LibraryPaths).</summary>
		public List<string> SystemLibraries { get; } = [];

		/// <summary>Additional libraries to link by full path.</summary>
		public List<FileReference> AdditionalLibraries { get; } = [];

		/// <summary>The output binary.</summary>
		public FileReference? OutputFile { get; set; }

		/// <summary>The intermediate object directory (for response files etc.).</summary>
		public DirectoryReference? IntermediateDirectory { get; set; }

		/// <summary>Whether to emit a .pdb file.</summary>
		public bool bUsePDBFiles { get; set; }

		/// <summary>Whether to do incremental linking.</summary>
		public bool bUseIncrementalLinking { get; set; }

		/// <summary>Construct an empty environment.</summary>
		public LinkEnvironment()
		{
		}
	}
}
