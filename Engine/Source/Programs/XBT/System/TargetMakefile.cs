// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealBuildTool.TargetMakefile. The UE 5.9 version is a 1.4 kLoC
// cache of the entire build graph plus diagnostics needed for incremental
// rebuilds, hot-reload, and rules-source invalidation. XBT Task 0.2 always
// does full clean compiles, so the makefile is just a typed container for the
// Action graph plus output paths. Incremental support is a Task 0.4+ concern.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using XBT.Actions;
using XPact.Build.Platform;
using XPact.Core.IO;
using Action = XBT.Actions.Action;

namespace XBT.BuildSystem
{
	/// <summary>
	/// Per-target build plan: actions to run, plus the output binary location.
	/// </summary>
	[SuppressMessage("Performance", "CA1002:Do not expose generic lists", Justification = "Matches UE5.9 API")]
	public sealed class TargetMakefile
	{
		/// <summary>The target this makefile is for.</summary>
		public string TargetName { get; }

		/// <summary>Target platform.</summary>
		public UnrealTargetPlatform Platform { get; }

		/// <summary>Target configuration.</summary>
		public UnrealTargetConfiguration Configuration { get; }

		/// <summary>Target architecture.</summary>
		public UnrealArch Architecture { get; }

		/// <summary>All actions to run, in declaration order. The executor topo-sorts before running.</summary>
		public List<Action> Actions { get; } = [];

		/// <summary>The final output binary.</summary>
		public FileReference? OutputFile { get; set; }

		/// <summary>Intermediate directory for .obj / .pch files.</summary>
		public DirectoryReference? IntermediateDirectory { get; set; }

		/// <summary>Construct a makefile.</summary>
		public TargetMakefile(string targetName, UnrealTargetPlatform platform, UnrealTargetConfiguration configuration, UnrealArch architecture)
		{
			System.ArgumentNullException.ThrowIfNull(targetName);
			TargetName = targetName;
			Platform = platform;
			Configuration = configuration;
			Architecture = architecture;
		}
	}
}
