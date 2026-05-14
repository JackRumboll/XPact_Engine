// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealBuildTool.CppCompileEnvironment. The UE 5.9 file carries
// ~80 toggles (CppStandard, UnityFilesToCompile, optimization knobs, IWYU
// metadata, static-analyzer plumbing, etc.). XBT keeps the fields that the
// Task 0.2 cl.exe command-line generator actually reads: includes, defs,
// optimization level, PCH binding, output dirs.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using XPact.Build.Platform;
using XPact.Core.IO;

namespace XBT.BuildSystem
{
	/// <summary>
	/// Optimization level for a compile.
	/// </summary>
	public enum OptimizationMode
	{
		/// <summary>No optimizations.</summary>
		Disabled,
		/// <summary>Balanced for development (cl /O1 or similar).</summary>
		Development,
		/// <summary>Full speed (cl /O2).</summary>
		Shipping,
	}

	/// <summary>
	/// Snapshot of everything the compiler needs to know to produce object files for a module.
	/// </summary>
	[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Matches Unreal field naming")]
	[SuppressMessage("Performance", "CA1002:Do not expose generic lists", Justification = "Matches UE5.9 API")]
	public sealed class CppCompileEnvironment
	{
		/// <summary>Target platform (Win64 only for Task 0.2).</summary>
		public UnrealTargetPlatform Platform { get; set; } = UnrealTargetPlatform.Win64;

		/// <summary>Architecture being compiled for.</summary>
		public UnrealArch Architecture { get; set; } = UnrealArch.X64;

		/// <summary>Build configuration.</summary>
		public UnrealTargetConfiguration Configuration { get; set; } = UnrealTargetConfiguration.Development;

		/// <summary>Optimization level for this compile.</summary>
		public OptimizationMode Optimization { get; set; } = OptimizationMode.Development;

		/// <summary>Include search paths.</summary>
		public List<DirectoryReference> IncludePaths { get; } = [];

		/// <summary>System include search paths (e.g. Windows SDK).</summary>
		public List<DirectoryReference> SystemIncludePaths { get; } = [];

		/// <summary>Preprocessor definitions, in NAME or NAME=VALUE form.</summary>
		public List<string> Definitions { get; } = [];

		/// <summary>Output directory for .obj files.</summary>
		public DirectoryReference? OutputDirectory { get; set; }

		/// <summary>The shared / private PCH header (path) to use when compiling these files.</summary>
		public FileReference? PCHHeader { get; set; }

		/// <summary>The .pch file produced by the PCH generation step.</summary>
		public FileReference? PCHFile { get; set; }

		/// <summary>The .obj file produced as a side-effect of PCH generation. Linked into the final binary.</summary>
		public FileReference? PCHObjectFile { get; set; }

		/// <summary>Whether this compile is the one that generates the PCH (vs. consumes it).</summary>
		public bool bGeneratingPCH { get; set; }

		/// <summary>Whether shared PCHs are enabled for this compile (mirrors TargetRules.bUseSharedPCHs).</summary>
		public bool bUseSharedPCHs { get; set; }

		/// <summary>Whether to emit a .pdb.</summary>
		public bool bUsePDBFiles { get; set; }

		/// <summary>Whether to use the AutoRTFM Clang compiler (Phase 1 Task 1.0a wires the flag through).</summary>
		public bool bUseAutoRTFMCompiler { get; set; } = true;

		/// <summary>Whether to link against the static CRT.</summary>
		public bool bUseStaticCRT { get; set; }

		/// <summary>Whether this is a debug build that should use the debug CRT.</summary>
		public bool bUseDebugCRT { get; set; }

		/// <summary>Construct an empty environment.</summary>
		public CppCompileEnvironment()
		{
		}

		/// <summary>Clone an environment.</summary>
		public CppCompileEnvironment(CppCompileEnvironment other)
		{
			System.ArgumentNullException.ThrowIfNull(other);
			Platform = other.Platform;
			Architecture = other.Architecture;
			Configuration = other.Configuration;
			Optimization = other.Optimization;
			IncludePaths.AddRange(other.IncludePaths);
			SystemIncludePaths.AddRange(other.SystemIncludePaths);
			Definitions.AddRange(other.Definitions);
			OutputDirectory = other.OutputDirectory;
			PCHHeader = other.PCHHeader;
			PCHFile = other.PCHFile;
			PCHObjectFile = other.PCHObjectFile;
			bGeneratingPCH = other.bGeneratingPCH;
			bUseSharedPCHs = other.bUseSharedPCHs;
			bUsePDBFiles = other.bUsePDBFiles;
			bUseAutoRTFMCompiler = other.bUseAutoRTFMCompiler;
			bUseStaticCRT = other.bUseStaticCRT;
			bUseDebugCRT = other.bUseDebugCRT;
		}
	}
}
