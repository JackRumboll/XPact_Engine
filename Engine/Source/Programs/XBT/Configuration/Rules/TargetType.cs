// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealBuildTool.TargetType from Configuration/Rules/TargetRules.cs.

using System;

namespace XBT.Configuration.Rules
{
	/// <summary>
	/// The type of target. Mirrors UE 5.9 UnrealBuildTool.TargetType.
	/// </summary>
	[Serializable]
	public enum TargetType
	{
		/// <summary>Cooked monolithic game executable.</summary>
		Game,

		/// <summary>Uncooked modular editor executable and DLLs.</summary>
		Editor,

		/// <summary>Cooked monolithic game client executable.</summary>
		Client,

		/// <summary>Cooked monolithic game server executable.</summary>
		Server,

		/// <summary>Program (standalone program, e.g. ShaderCompileWorker.exe, HelloWorld.exe).</summary>
		Program,
	}

	/// <summary>
	/// Specifies how to link all the modules in this target.
	/// </summary>
	[Serializable]
	public enum TargetLinkType
	{
		/// <summary>Default link type based on the current target type.</summary>
		Default,

		/// <summary>Link all modules into a single binary.</summary>
		Monolithic,

		/// <summary>Link modules into individual dynamic libraries.</summary>
		Modular,
	}

	/// <summary>
	/// Specifies whether to share engine binaries and intermediates with other projects, or to create project-specific versions.
	/// </summary>
	[Serializable]
	public enum TargetBuildEnvironment
	{
		/// <summary>Engine binaries and intermediates are output to the engine folder.</summary>
		Shared,

		/// <summary>Engine binaries and intermediates are specific to this target.</summary>
		Unique,

		/// <summary>Will switch to Unique if needed (per-project sdk, etc).</summary>
		UniqueIfNeeded,
	}
}
