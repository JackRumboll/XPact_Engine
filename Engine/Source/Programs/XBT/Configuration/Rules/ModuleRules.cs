// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealBuildTool.ModuleRules (Configuration/Rules/ModuleRules.cs).
// XBT keeps the public API surface that .Build.cs files reference (Type,
// Public/PrivateDependencyModuleNames, Public/PrivateIncludePaths,
// Public/PrivateIncludePathModuleNames, Public/PrivateDefinitions,
// DynamicallyLoadedModuleNames, CircularlyReferencedDependentModules,
// PCHUsage, Private/SharedPCHHeaderFile, ExternalDependencies, RuntimeDependencies).
// Fields that depend on Verse/IWYU/UHT/Plugins/StagedFileType are dropped
// for Task 0.2 and will be re-introduced in later phases.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using XPact.Core.IO;

namespace XBT.Configuration.Rules
{
	/// <summary>
	/// ModuleRules is the base class that .Build.cs files derive from.
	/// </summary>
	[SuppressMessage("Design", "CA1051:Do not declare visible instance fields", Justification = "Matches UE5.9 ModuleRules API")]
	[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Subclasses are loaded via reflection from .Build.cs files")]
	[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Matches UE5.9 ModuleRules API")]
	public abstract class ModuleRules
	{
		/// <summary>Type of module.</summary>
		[SuppressMessage("Design", "CA1034:Nested types should not be visible", Justification = "Matches UE5.9 nested ModuleType enum, referenced from .Build.cs")]
		public enum ModuleType
		{
			/// <summary>C++ module compiled from source.</summary>
			CPlusPlus,

			/// <summary>External (third-party) module providing prebuilt binaries.</summary>
			External,
		}

		/// <summary>How a module participates in the PCH scheme.</summary>
		[SuppressMessage("Design", "CA1034:Nested types should not be visible", Justification = "Matches UE5.9 nested PCHUsageMode enum, referenced from .Build.cs")]
		public enum PCHUsageMode
		{
			/// <summary>Default: engine modules use shared PCHs, game modules do not.</summary>
			Default,

			/// <summary>Never use any PCHs.</summary>
			NoPCHs,

			/// <summary>Never use shared PCHs. Always generate a unique PCH for this module if appropriate.</summary>
			NoSharedPCHs,

			/// <summary>Shared PCHs may be used.</summary>
			UseSharedPCHs,

			/// <summary>Shared PCHs may be used if an explicit private PCH is not set through PrivatePCHHeaderFile.</summary>
			UseExplicitOrSharedPCHs,
		}

		// -------------------------------------------------------------------------
		// Identity
		// -------------------------------------------------------------------------

		/// <summary>Name of this module.</summary>
		public string Name { get; internal set; } = String.Empty;

		/// <summary>File containing this module (the .Build.cs).</summary>
		public FileReference? File { get; internal set; }

		/// <summary>Directory containing this module.</summary>
		public DirectoryReference? Directory { get; internal set; }

		/// <summary>Rules for the target that this module belongs to.</summary>
		public TargetRules Target { get; init; }

		/// <summary>Type of module.</summary>
		public ModuleType Type { get; set; } = ModuleType.CPlusPlus;

		// -------------------------------------------------------------------------
		// Dependencies
		// -------------------------------------------------------------------------

		/// <summary>List of public dependency module names. These modules' public include paths are visible.</summary>
		public List<string> PublicDependencyModuleNames { get; } = [];

		/// <summary>List of private dependency module names. These modules' public include paths are visible only to this module.</summary>
		public List<string> PrivateDependencyModuleNames { get; } = [];

		/// <summary>Modules whose public include paths should be added but which we do not link against.</summary>
		public List<string> PublicIncludePathModuleNames { get; } = [];

		/// <summary>Modules whose public include paths should be added to our private include paths only.</summary>
		public List<string> PrivateIncludePathModuleNames { get; } = [];

		/// <summary>Modules that are dynamically loaded by this module at runtime (not link-time deps).</summary>
		public List<string> DynamicallyLoadedModuleNames { get; } = [];

		/// <summary>List of modules that may circularly reference back to this one (the build graph must tolerate the cycle).</summary>
		public List<string> CircularlyReferencedDependentModules { get; } = [];

		// -------------------------------------------------------------------------
		// Include paths
		// -------------------------------------------------------------------------

		/// <summary>List of public include paths to add (relative to module dir or absolute).</summary>
		public List<string> PublicIncludePaths { get; } = [];

		/// <summary>List of private include paths to add (relative to module dir or absolute).</summary>
		public List<string> PrivateIncludePaths { get; } = [];

		// -------------------------------------------------------------------------
		// Definitions
		// -------------------------------------------------------------------------

		/// <summary>Preprocessor definitions exposed to dependents.</summary>
		public List<string> PublicDefinitions { get; } = [];

		/// <summary>Preprocessor definitions only visible inside this module.</summary>
		public List<string> PrivateDefinitions { get; } = [];

		// -------------------------------------------------------------------------
		// PCH
		// -------------------------------------------------------------------------

		/// <summary>Explicit private PCH header for this module. Setting this implies no shared PCH.</summary>
		public string? PrivatePCHHeaderFile { get; set; }

		/// <summary>Shared PCH header provided by this module. Must be a public header.</summary>
		public string? SharedPCHHeaderFile { get; set; }

		/// <summary>Precompiled-header usage mode for this module.</summary>
		public PCHUsageMode PCHUsage { get; set; } = PCHUsageMode.Default;

		// -------------------------------------------------------------------------
		// External / runtime dependencies
		// -------------------------------------------------------------------------

		/// <summary>External files that should be tracked for outdated-ness.</summary>
		public List<string> ExternalDependencies { get; } = [];

		/// <summary>Runtime dependencies (files staged alongside the build product).</summary>
		public List<string> RuntimeDependencies { get; } = [];

		/// <summary>
		/// AutoRTFM external mapping (.aem) files that the AutoRTFM clang fork should
		/// consume via <c>-Xclang -autortfm-mappings</c>. Paths are relative to the
		/// Engine source root (e.g. <c>Runtime/XAutoRTFM/Public/StdLib.Common.aem</c>)
		/// or absolute. Honoured by VCToolChain only when an AutoRTFM-capable
		/// <c>verse-clang-cl.exe</c> is detected; under MSVC fallback these entries
		/// are logged and ignored. Surface mirrors UnrealBuildTool ModuleRules.cs.
		/// </summary>
		public List<string> AutoRTFMExternalMappingFiles { get; } = [];

		// -------------------------------------------------------------------------
		// Optional override for which directory contains the .cpp/.h files. Phase 0
		// uses Directory only.
		// -------------------------------------------------------------------------

		/// <summary>
		/// Constructor invoked by RulesAssembly. .Build.cs subclasses declare this
		/// constructor with [Target] as their parameter, so the rules compiler can
		/// pass the resolved TargetRules through.
		/// </summary>
		protected ModuleRules(TargetRules target)
		{
			ArgumentNullException.ThrowIfNull(target);
			Target = target;
		}
	}
}
