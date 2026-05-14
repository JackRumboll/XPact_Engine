// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealBuildTool.TargetRules (Configuration/Rules/TargetRules.cs).
// The UE 5.9 file is ~3.6 kLoC; XBT keeps the public API surface that
// .Target.cs files reference (Name, Type, Platform, Configuration, Architecture,
// LaunchModuleName, bBuild*, bUse*, ExtraModuleNames, Pre/PostBuildSteps, the
// AutoRTFM compiler flag etc.). Fields that depend on systems we haven't
// ported (Verse, Plugins, Receipts, ReadOnlyTargetRules wrapper, etc.) are
// dropped. Adding a missing flag in a follow-up phase only needs a new
// property here; the rules-compiler will pick it up automatically.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using XPact.Build.Platform;
using XPact.Core.IO;

namespace XBT.Configuration.Rules
{
	/// <summary>
	/// TargetRules is the base class that .Target.cs files derive from.
	/// </summary>
	[SuppressMessage("Design", "CA1051:Do not declare visible instance fields", Justification = "Matches UE5.9 TargetRules API")]
	[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Matches UE5.9 TargetRules API")]
	public abstract class TargetRules
	{
		// -------------------------------------------------------------------------
		// Identity
		// -------------------------------------------------------------------------

		/// <summary>The name of this target.</summary>
		public string Name
		{
			get => !String.IsNullOrEmpty(NameOverride) ? NameOverride : DefaultName;
			set => NameOverride = value;
		}

		/// <summary>Return the original target name without overrides.</summary>
		public string OriginalName => DefaultName;

		/// <summary>Override the name used for this target.</summary>
		private string? NameOverride;

		private readonly string DefaultName;

		/// <summary>Platform that this target is being built for.</summary>
		public UnrealTargetPlatform Platform { get; init; }

		/// <summary>The configuration being built.</summary>
		public UnrealTargetConfiguration Configuration { get; init; }

		/// <summary>Architecture(s) that the target is being built for.</summary>
		public UnrealArchitectures Architectures { get; init; }

		/// <summary>The single architecture for this target. Throws if multi-arch.</summary>
		public UnrealArch Architecture => Architectures.SingleArchitecture;

		/// <summary>Path to the project file containing this target, if any.</summary>
		public FileReference? ProjectFile { get; init; }

		// -------------------------------------------------------------------------
		// Target type / link / build environment
		// -------------------------------------------------------------------------

		/// <summary>The type of target.</summary>
		public TargetType Type { get; set; } = TargetType.Game;

		/// <summary>How the modules in this target are linked together.</summary>
		public TargetLinkType LinkType { get; set; } = TargetLinkType.Default;

		/// <summary>The build environment to use.</summary>
		public TargetBuildEnvironment BuildEnvironment { get; set; } = TargetBuildEnvironment.Shared;

		// -------------------------------------------------------------------------
		// Output
		// -------------------------------------------------------------------------

		/// <summary>
		/// Path to the output file for the main executable, relative to the Engine or project directory.
		/// </summary>
		public string? OutputFile { get; set; }

		/// <summary>
		/// Specifies the name of the launch module. For Programs, this is the module that is compiled into the target's executable.
		/// For non-Programs, defaults to "Launch".
		/// </summary>
		public string? LaunchModuleName
		{
			get => (LaunchModuleNamePrivate == null && Type != TargetType.Program) ? "Launch" : LaunchModuleNamePrivate;
			set => LaunchModuleNamePrivate = value;
		}
		private string? LaunchModuleNamePrivate;

		// -------------------------------------------------------------------------
		// Build feature flags (subset of UE 5.9 bBuild*/bUse*)
		// -------------------------------------------------------------------------

		/// <summary>Whether the editor is built.</summary>
		public bool bBuildEditor
		{
			get => bBuildEditorPrivate ?? (Type == TargetType.Editor);
			set => bBuildEditorPrivate = value;
		}
		private bool? bBuildEditorPrivate;

		/// <summary>Whether developer tools are built.</summary>
		public bool bBuildDeveloperTools
		{
			get => bBuildDeveloperToolsPrivate ?? (Type == TargetType.Editor || Type == TargetType.Server || Type == TargetType.Client);
			set => bBuildDeveloperToolsPrivate = value;
		}
		private bool? bBuildDeveloperToolsPrivate;

		/// <summary>Whether to build all modules valid for this target type.</summary>
		public bool bBuildAllModules { get; set; }

		/// <summary>Whether to use the unity build system. Phase 0 forbids unity, but the flag exists for API compatibility.</summary>
		public bool bUseUnityBuild { get; set; }

		/// <summary>Adaptive unity build (rebuild only files actually changed within a unity blob).</summary>
		public bool bUseAdaptiveUnityBuild { get; set; }

		/// <summary>Whether PCH files are used at all.</summary>
		public bool bUsePCHFiles { get; set; } = true;

		/// <summary>Whether shared PCHs are used. Required for HelloWorld in Task 0.2.</summary>
		public bool bUseSharedPCHs { get; set; } = true;

		/// <summary>Force-link static CRT.</summary>
		public bool bUseStaticCRT { get; set; }

		/// <summary>Use debug CRT for debug builds.</summary>
		public bool bDebugBuildsActuallyUseDebugCRT { get; set; }

		/// <summary>
		/// Whether to use the AutoRTFM Clang compiler. XPact decision #27a: default ON for all
		/// derived-engine targets. The AutoRTFM LLVM pass is wired up in Phase 1 Task 1.0a;
		/// Task 0.2 only exposes the flag.
		/// </summary>
		public bool bUseAutoRTFMCompiler
		{
			get => bForceNoAutoRTFMCompiler ? false : bUseAutoRTFMCompilerPrivate;
			set => bUseAutoRTFMCompilerPrivate = value;
		}
		private bool bUseAutoRTFMCompilerPrivate = true;

		/// <summary>Whether to force the AutoRTFM Clang compiler off (kill-switch on a command line).</summary>
		public bool bForceNoAutoRTFMCompiler { get; set; }

		/// <summary>Whether to emit AutoRTFM verification metadata.</summary>
		public bool bUseAutoRTFMVerifier { get; set; }

		/// <summary>Whether to use PDB files for debug info.</summary>
		public bool bUsePDBFiles { get; set; }

		/// <summary>Whether to use incremental linking.</summary>
		public bool bUseIncrementalLinking { get; set; }

		// -------------------------------------------------------------------------
		// Module composition
		// -------------------------------------------------------------------------

		/// <summary>Extra module names to compile/link into this target beyond the launch module.</summary>
		public List<string> ExtraModuleNames { get; } = [];

		/// <summary>Pre-build command lines.</summary>
		public List<string> PreBuildSteps { get; } = [];

		/// <summary>Post-build command lines.</summary>
		public List<string> PostBuildSteps { get; } = [];

		// -------------------------------------------------------------------------
		// Rules-file metadata
		// -------------------------------------------------------------------------

		/// <summary>File containing the rules for this target.</summary>
		public FileReference? File { get; internal set; }

		/// <summary>
		/// Constructor invoked by RulesAssembly with platform/config/arch resolved.
		/// </summary>
		protected TargetRules(TargetInfo target)
		{
			ArgumentNullException.ThrowIfNull(target);
			DefaultName = target.Name;
			Platform = target.Platform;
			Configuration = target.Configuration;
			Architectures = target.Architectures;
			ProjectFile = target.ProjectFile;
		}
	}
}
