// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealBuildTool.UEBuildWindows command-line generation. We
// stitch CppCompileEnvironment + LinkEnvironment + VCEnvironment into MSVC
// cl.exe / link.exe arguments. Strip every option that isn't either required
// or part of the bUseAutoRTFM* / PCH paths.

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using XBT.BuildSystem;
using XPact.Build.Platform;
using XPact.Core.IO;

namespace XBT.Platform.Windows
{
	/// <summary>
	/// Static helpers that translate CppCompileEnvironment / LinkEnvironment
	/// into MSVC command-line arguments for cl.exe and link.exe.
	/// </summary>
	public static class XBTWindows
	{
		/// <summary>
		/// Build the cl.exe arguments for a single .cpp -> .obj compile.
		/// </summary>
		public static string MakeClCompileArgs(CppCompileEnvironment env, FileReference sourceFile, FileReference objectFile)
		{
			System.ArgumentNullException.ThrowIfNull(env);
			System.ArgumentNullException.ThrowIfNull(sourceFile);
			System.ArgumentNullException.ThrowIfNull(objectFile);

			StringBuilder sb = new();

			AppendCommonClOptions(sb, env);

			// PCH consumption: /Yu<header> /Fp<pch>
			if (env.PCHHeader is not null && env.PCHFile is not null && !env.bGeneratingPCH)
			{
				sb.Append(' ').Append("/Yu\"").Append(env.PCHHeader.GetFileName()).Append('"');
				sb.Append(' ').Append("/Fp\"").Append(env.PCHFile.FullName).Append('"');
				// /FI forces the PCH header into every source file, mirroring Unreal's MSVCToolChain behaviour.
				sb.Append(' ').Append("/FI\"").Append(env.PCHHeader.GetFileName()).Append('"');
			}

			// Output and source
			sb.Append(' ').Append("/c");
			sb.Append(' ').Append("/Fo\"").Append(objectFile.FullName).Append('"');
			sb.Append(' ').Append('"').Append(sourceFile.FullName).Append('"');

			return sb.ToString();
		}

		/// <summary>
		/// Build the cl.exe arguments for generating the shared/private PCH.
		/// </summary>
		public static string MakePCHGenerateArgs(CppCompileEnvironment env, FileReference pchHeader, FileReference stubSourceFile, FileReference pchFile, FileReference pchObjectFile)
		{
			System.ArgumentNullException.ThrowIfNull(env);
			System.ArgumentNullException.ThrowIfNull(pchHeader);
			System.ArgumentNullException.ThrowIfNull(stubSourceFile);
			System.ArgumentNullException.ThrowIfNull(pchFile);
			System.ArgumentNullException.ThrowIfNull(pchObjectFile);

			StringBuilder sb = new();
			AppendCommonClOptions(sb, env);

			// /Yc<header> /Fp<pch>
			sb.Append(' ').Append("/Yc\"").Append(pchHeader.GetFileName()).Append('"');
			sb.Append(' ').Append("/Fp\"").Append(pchFile.FullName).Append('"');
			sb.Append(' ').Append("/FI\"").Append(pchHeader.GetFileName()).Append('"');

			// We need the PCH header dir on the include path so the cl driver can find it.
			DirectoryReference headerDir = pchHeader.Directory;
			sb.Append(' ').Append("/I\"").Append(headerDir.FullName).Append('"');

			sb.Append(' ').Append("/c");
			sb.Append(' ').Append("/Fo\"").Append(pchObjectFile.FullName).Append('"');
			sb.Append(' ').Append('"').Append(stubSourceFile.FullName).Append('"');

			return sb.ToString();
		}

		/// <summary>
		/// Build link.exe arguments for the final binary.
		/// </summary>
		public static string MakeLinkArgs(LinkEnvironment env)
		{
			System.ArgumentNullException.ThrowIfNull(env);
			if (env.OutputFile is null)
			{
				throw new XPact.Build.BuildException("LinkEnvironment.OutputFile must be set before generating link args");
			}

			StringBuilder sb = new();

			sb.Append("/NOLOGO ");
			sb.Append(env.Subsystem == LinkerSubsystem.Windows ? "/SUBSYSTEM:WINDOWS " : "/SUBSYSTEM:CONSOLE ");
			sb.Append("/MACHINE:X64 ");

			if (env.Configuration == UnrealTargetConfiguration.Debug || env.bUsePDBFiles)
			{
				sb.Append("/DEBUG ");
				// /PDB:<file>
				string pdbPath = Path.ChangeExtension(env.OutputFile.FullName, ".pdb");
				sb.Append("/PDB:\"").Append(pdbPath).Append("\" ");
			}

			if (!env.bUseIncrementalLinking)
			{
				sb.Append("/INCREMENTAL:NO ");
			}

			sb.Append("/OUT:\"").Append(env.OutputFile.FullName).Append("\" ");

			foreach (DirectoryReference lp in env.LibraryPaths)
			{
				sb.Append("/LIBPATH:\"").Append(lp.FullName).Append("\" ");
			}

			foreach (string sys in env.SystemLibraries)
			{
				sb.Append(sys);
				if (!sys.EndsWith(".lib", System.StringComparison.OrdinalIgnoreCase))
				{
					sb.Append(".lib");
				}
				sb.Append(' ');
			}

			foreach (FileReference additional in env.AdditionalLibraries)
			{
				sb.Append('"').Append(additional.FullName).Append("\" ");
			}

			foreach (FileReference inp in env.InputFiles)
			{
				sb.Append('"').Append(inp.FullName).Append("\" ");
			}

			return sb.ToString().TrimEnd();
		}

		// ---------------------------------------------------------------------

		private static void AppendCommonClOptions(StringBuilder sb, CppCompileEnvironment env)
		{
			sb.Append("/nologo /EHsc /Zc:__cplusplus /std:c++20 /utf-8 /W3 ");

			// CRT selection
			if (env.bUseStaticCRT)
			{
				sb.Append(env.bUseDebugCRT ? "/MTd " : "/MT ");
			}
			else
			{
				sb.Append(env.bUseDebugCRT ? "/MDd " : "/MD ");
			}

			// Optimisation
			switch (env.Optimization)
			{
				case OptimizationMode.Disabled:
					sb.Append("/Od ");
					break;
				case OptimizationMode.Development:
					sb.Append("/O2 ");
					break;
				case OptimizationMode.Shipping:
					sb.Append("/O2 /Ob3 /GL ");
					break;
			}

			if (env.bUsePDBFiles)
			{
				sb.Append("/Zi ");
				// Force separate Fd per object dir to avoid the cl.exe shared PDB serialisation hazard.
				if (env.OutputDirectory is not null)
				{
					sb.Append("/Fd\"").Append(Path.Combine(env.OutputDirectory.FullName, "vc")).Append(".pdb\" ");
				}
			}

			foreach (DirectoryReference inc in env.IncludePaths)
			{
				sb.Append("/I\"").Append(inc.FullName).Append("\" ");
			}
			foreach (DirectoryReference sys in env.SystemIncludePaths)
			{
				sb.Append("/external:I\"").Append(sys.FullName).Append("\" /external:W0 ");
			}

			foreach (string def in env.Definitions)
			{
				sb.Append("/D").Append(def).Append(' ');
			}

			// The AutoRTFM compiler flag is exposed but no clang-fork toolchain is
			// wired up in Task 0.2. Phase 1 Task 1.0a will gate /D and a path switch
			// here. For now we expose a sentinel define so callers can detect that
			// the flag was honoured.
			if (env.bUseAutoRTFMCompiler)
			{
				sb.Append("/DXPACT_AUTORTFM_COMPILER_FLAG_SET=1 ");
			}

			sb.Length--; // strip trailing space - we'll re-add specifics from callers
			_ = CultureInfo.InvariantCulture; // appease analyzers; this method intentionally builds ASCII-only switches
		}

		/// <summary>
		/// Computes the working directory for spawning cl.exe/link.exe. We need
		/// VC's environment vars resolved, so we run from the VC tools bin dir.
		/// </summary>
		public static DirectoryReference GetWorkingDirectory(VCEnvironment vc)
		{
			System.ArgumentNullException.ThrowIfNull(vc);
			return vc.VCToolChainDir;
		}

		/// <summary>
		/// Build the INCLUDE / LIB environment-variable strings to set on child processes.
		/// Returns (include, lib).
		/// </summary>
		public static (string include, string lib) BuildEnvStrings(VCEnvironment vc)
		{
			System.ArgumentNullException.ThrowIfNull(vc);
			List<string> inc = [];
			foreach (DirectoryReference d in vc.IncludePaths)
			{
				inc.Add(d.FullName);
			}
			List<string> lib = [];
			foreach (DirectoryReference d in vc.LibraryPaths)
			{
				lib.Add(d.FullName);
			}
			return (string.Join(';', inc), string.Join(';', lib));
		}
	}
}
