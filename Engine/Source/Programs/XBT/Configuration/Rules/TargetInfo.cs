// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealBuildTool.TargetInfo. The UE 5.9 version dispatches
// architecture defaults through UnrealArchitectureConfig.ForPlatform(...);
// XBT Task 0.2 doesn't have that plumbing, so callers must supply an
// architecture explicitly (we default to UnrealArch.X64 for Windows).

using System;
using XPact.Build.Platform;
using XPact.Core.IO;

namespace XBT.Configuration.Rules
{
	/// <summary>
	/// Information about a target, passed along when creating a TargetRules instance.
	/// </summary>
	public sealed class TargetInfo
	{
		/// <summary>Name of the target.</summary>
		public string Name { get; }

		/// <summary>The platform that the target is being built for.</summary>
		public UnrealTargetPlatform Platform { get; }

		/// <summary>The configuration being built.</summary>
		public UnrealTargetConfiguration Configuration { get; }

		/// <summary>Architecture(s) that the target is being built for.</summary>
		public UnrealArchitectures Architectures { get; }

		/// <summary>Path to the project file containing the target, if any.</summary>
		public FileReference? ProjectFile { get; }

		/// <summary>Construct a TargetInfo.</summary>
		public TargetInfo(string name, UnrealTargetPlatform platform, UnrealTargetConfiguration configuration, UnrealArchitectures architectures, FileReference? projectFile = null)
		{
			ArgumentNullException.ThrowIfNull(name);
			ArgumentNullException.ThrowIfNull(architectures);
			Name = name;
			Platform = platform;
			Configuration = configuration;
			Architectures = architectures;
			ProjectFile = projectFile;
		}
	}
}
