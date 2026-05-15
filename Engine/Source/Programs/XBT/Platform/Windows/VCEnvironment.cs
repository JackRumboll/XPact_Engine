// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealBuildTool.VCEnvironment. The UE 5.9 version exhaustively
// catalogues every installed MSVC toolchain version and SDK fragment so that
// build configurations can pin specific revisions. XBT Task 0.2 only needs to
// answer four questions:
//   * Where is cl.exe?
//   * Where is link.exe?
//   * What INCLUDE paths does MSVC + Windows SDK need?
//   * What LIB paths does MSVC + Windows SDK need?
// We discover the newest VS 2022+ install via vswhere.exe and the newest
// Windows 10/11 SDK via HKLM\SOFTWARE\Microsoft\Windows Kits\Installed Roots.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Microsoft.Win32;
using XPact.Build;
using XPact.Core.IO;

namespace XBT.Platform.Windows
{
	/// <summary>
	/// Resolved MSVC + Windows SDK paths for a single compile.
	/// </summary>
	public sealed class VCEnvironment
	{
		/// <summary>Visual Studio install root, e.g. C:\Program Files\Microsoft Visual Studio\2022\Community.</summary>
		public DirectoryReference VisualStudioInstallDir { get; }

		/// <summary>MSVC tool root, e.g. ...\VC\Tools\MSVC\14.40.33807.</summary>
		public DirectoryReference VCToolChainDir { get; }

		/// <summary>MSVC version string (e.g. 14.40.33807).</summary>
		public string VCToolChainVersion { get; }

		/// <summary>Path to cl.exe (host x64, target x64).</summary>
		public FileReference ClExe { get; }

		/// <summary>Path to link.exe.</summary>
		public FileReference LinkExe { get; }

		/// <summary>Path to lib.exe (static-library archiver).</summary>
		public FileReference LibExe { get; }

		/// <summary>Windows SDK install root.</summary>
		public DirectoryReference WindowsSdkRoot { get; }

		/// <summary>Windows SDK version (e.g. 10.0.22621.0).</summary>
		public string WindowsSdkVersion { get; }

		/// <summary>INCLUDE paths to add (MSVC + UCRT + UM + Shared + WinRT).</summary>
		public IReadOnlyList<DirectoryReference> IncludePaths { get; }

		/// <summary>LIB paths to add (MSVC + UCRT + UM, all x64).</summary>
		public IReadOnlyList<DirectoryReference> LibraryPaths { get; }

		private VCEnvironment(DirectoryReference vsInstallDir, DirectoryReference vcToolChainDir, string vcToolChainVersion, FileReference clExe, FileReference linkExe, FileReference libExe, DirectoryReference winSdkRoot, string winSdkVersion, IReadOnlyList<DirectoryReference> includePaths, IReadOnlyList<DirectoryReference> libraryPaths)
		{
			VisualStudioInstallDir = vsInstallDir;
			VCToolChainDir = vcToolChainDir;
			VCToolChainVersion = vcToolChainVersion;
			ClExe = clExe;
			LinkExe = linkExe;
			LibExe = libExe;
			WindowsSdkRoot = winSdkRoot;
			WindowsSdkVersion = winSdkVersion;
			IncludePaths = includePaths;
			LibraryPaths = libraryPaths;
		}

		/// <summary>Discover the latest MSVC + Windows SDK and produce a VCEnvironment.</summary>
		[SupportedOSPlatform("windows")]
		public static VCEnvironment Discover()
		{
			DirectoryReference vsInstallDir = FindVisualStudioInstallation();
			(DirectoryReference vcToolChainDir, string vcToolChainVersion) = FindMSVCToolchain(vsInstallDir);
			FileReference clExe = FileReference.Combine(vcToolChainDir, "bin", "Hostx64", "x64", "cl.exe");
			FileReference linkExe = FileReference.Combine(vcToolChainDir, "bin", "Hostx64", "x64", "link.exe");
			FileReference libExe = FileReference.Combine(vcToolChainDir, "bin", "Hostx64", "x64", "lib.exe");
			if (!File.Exists(clExe.FullName))
			{
				throw new BuildException("Expected cl.exe at '{0}' but it does not exist.", clExe.FullName);
			}
			if (!File.Exists(linkExe.FullName))
			{
				throw new BuildException("Expected link.exe at '{0}' but it does not exist.", linkExe.FullName);
			}
			if (!File.Exists(libExe.FullName))
			{
				throw new BuildException("Expected lib.exe at '{0}' but it does not exist.", libExe.FullName);
			}

			(DirectoryReference winSdkRoot, string winSdkVersion) = FindWindowsSdk();

			DirectoryReference msvcInclude = DirectoryReference.Combine(vcToolChainDir, "include");
			DirectoryReference msvcLib = DirectoryReference.Combine(vcToolChainDir, "lib", "x64");

			DirectoryReference sdkInclude = DirectoryReference.Combine(winSdkRoot, "Include", winSdkVersion);
			DirectoryReference sdkLib = DirectoryReference.Combine(winSdkRoot, "Lib", winSdkVersion);

			List<DirectoryReference> includes =
			[
				msvcInclude,
				DirectoryReference.Combine(sdkInclude, "ucrt"),
				DirectoryReference.Combine(sdkInclude, "um"),
				DirectoryReference.Combine(sdkInclude, "shared"),
				DirectoryReference.Combine(sdkInclude, "winrt"),
				DirectoryReference.Combine(sdkInclude, "cppwinrt"),
			];
			List<DirectoryReference> libs =
			[
				msvcLib,
				DirectoryReference.Combine(sdkLib, "ucrt", "x64"),
				DirectoryReference.Combine(sdkLib, "um", "x64"),
			];

			return new VCEnvironment(vsInstallDir, vcToolChainDir, vcToolChainVersion, clExe, linkExe, libExe, winSdkRoot, winSdkVersion, includes, libs);
		}

		/// <summary>
		/// Attempts to locate the AutoRTFM-capable Clang driver (verse-clang-cl.exe).
		/// UE 5.9's WindowsCompiler.ClangRTFM maps to <c>verse-clang-cl.exe</c>; that
		/// binary is part of Epic's Verse/AutoRTFM distribution and is NOT present in
		/// our UE 5.9 a79fff49 source checkout (verified by exhaustive search).
		/// </summary>
		/// <remarks>
		/// Vendoring contract: drop the AutoRTFM clang fork as
		/// <c>Engine/Source/ThirdParty/UnrealInstrumentation/bin/verse-clang-cl.exe</c>
		/// (alongside <c>verse-link.exe</c> if a paired linker is shipped) and this
		/// method will flip to returning <c>true</c>. The corresponding mapping-file
		/// emission at <see cref="XBTWindows.MakeClCompileArgs"/> (gated on
		/// <see cref="BuildSystem.CppCompileEnvironment.bUseAutoRTFMCompilerEffective"/>)
		/// will then activate. No XBT changes are required when the binary lands —
		/// the toolchain swap is purely a presence check.
		/// </remarks>
		/// <param name="engineDir">Engine root (the directory containing Source/).</param>
		/// <param name="autoRTFMClangPath">The resolved compiler path, when found.</param>
		/// <returns>True if an AutoRTFM-capable compiler binary is vendored.</returns>
		public static bool TryGetAutoRTFMCompilerPath(DirectoryReference engineDir, out FileReference? autoRTFMClangPath)
		{
			System.ArgumentNullException.ThrowIfNull(engineDir);
			autoRTFMClangPath = null;

			FileReference candidate = FileReference.Combine(
				engineDir, "Source", "ThirdParty", "UnrealInstrumentation", "bin", "verse-clang-cl.exe");
			if (File.Exists(candidate.FullName))
			{
				autoRTFMClangPath = candidate;
				return true;
			}
			return false;
		}

		private static DirectoryReference FindVisualStudioInstallation()
		{
			string vswherePath = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
				"Microsoft Visual Studio", "Installer", "vswhere.exe");
			if (!File.Exists(vswherePath))
			{
				throw new BuildException("vswhere.exe not found at '{0}'. Install Visual Studio 2022 (or later) with the C++ workload.", vswherePath);
			}

			ProcessStartInfo psi = new()
			{
				FileName = vswherePath,
				// Latest VS 2022+ install with the native C++ workload, sorted newest first
				Arguments = "-latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath -format value",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};
			using Process proc = Process.Start(psi) ?? throw new BuildException("Failed to start vswhere.exe");
			string stdout = proc.StandardOutput.ReadToEnd().Trim();
			proc.WaitForExit();
			if (proc.ExitCode != 0 || String.IsNullOrEmpty(stdout))
			{
				throw new BuildException("vswhere.exe did not return any Visual Studio installation with the C++ workload. Install VS 2022+ with 'Desktop development with C++'.");
			}

			string firstLine = stdout.Split('\n', '\r').First(s => !String.IsNullOrWhiteSpace(s));
			if (!Directory.Exists(firstLine))
			{
				throw new BuildException("vswhere.exe returned non-existent VS install dir '{0}'", firstLine);
			}
			return new DirectoryReference(firstLine);
		}

		private static (DirectoryReference, string) FindMSVCToolchain(DirectoryReference vsInstallDir)
		{
			string toolsRoot = Path.Combine(vsInstallDir.FullName, "VC", "Tools", "MSVC");
			if (!Directory.Exists(toolsRoot))
			{
				throw new BuildException("MSVC tools directory not found at '{0}'", toolsRoot);
			}

			string[] versions = Directory.GetDirectories(toolsRoot)
				.Select(Path.GetFileName)
				.Where(n => !String.IsNullOrEmpty(n))
				.Cast<string>()
				.OrderByDescending(s => s, StringComparer.OrdinalIgnoreCase)
				.ToArray();

			if (versions.Length == 0)
			{
				throw new BuildException("No MSVC versions found under '{0}'", toolsRoot);
			}

			string chosen = versions[0];
			return (new DirectoryReference(Path.Combine(toolsRoot, chosen)), chosen);
		}

		[SupportedOSPlatform("windows")]
		private static (DirectoryReference, string) FindWindowsSdk()
		{
			// HKLM\SOFTWARE\Microsoft\Windows Kits\Installed Roots\KitsRoot10 + subdirs of <KitsRoot10>\Include
			using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Kits\Installed Roots");
			string? root = key?.GetValue("KitsRoot10") as string
				?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots")?.GetValue("KitsRoot10") as string;
			if (String.IsNullOrEmpty(root) || !Directory.Exists(root))
			{
				throw new BuildException("Windows 10/11 SDK not found in registry. Install Windows SDK 10.0.22621.0 or newer.");
			}

			string includeRoot = Path.Combine(root, "Include");
			if (!Directory.Exists(includeRoot))
			{
				throw new BuildException("Windows SDK Include root '{0}' does not exist.", includeRoot);
			}
			string[] candidates = Directory.GetDirectories(includeRoot)
				.Select(Path.GetFileName)
				.Where(n => !String.IsNullOrEmpty(n) && n!.StartsWith("10.", StringComparison.Ordinal))
				.Cast<string>()
				.OrderByDescending(VersionKey)
				.ToArray();
			if (candidates.Length == 0)
			{
				throw new BuildException("No Windows 10/11 SDK Include subdirs found under '{0}'.", includeRoot);
			}
			return (new DirectoryReference(root), candidates[0]);
		}

		// Parse "10.0.22621.0" into a comparable Version. We sort descending.
		private static Version VersionKey(string s)
		{
			return Version.TryParse(s, out Version? v) ? v : new Version(0, 0);
		}
	}
}
