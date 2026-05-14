// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim, Phase-0 port of UnrealBuildTool.UHTExecution. The UE 5.9 version
// (Engine/Source/Programs/UnrealBuildTool/System/UHTExecution.cs, ~1.5 kLoC)
// covers hashing, timestamp caches, in-process UHT invocation and dependency
// recovery. XBT Task 0.3 only needs to:
//
//   1. Construct an UhtInputManifest from the resolved target + modules.
//   2. Serialise it to disk at the well-known Cache/Intermediate location.
//   3. Locate and shell out to XHT.exe with -manifest=<path> -target=<name>.
//   4. Stream XHT's stdout/stderr to XPact.Core.Logging.Log.
//   5. Fail the build if XHT exits non-zero.
//
// Phase 1 Task 1.9 will extend the manifest payload (per-module include paths,
// PublicDefines, generated CPP filename base etc.) once XHT actually parses C++.
// Until then, the seam matters more than the payload -- the goal is having a
// place to plug those fields in without surgery.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;
using XBT.Configuration.Rules;
using XPact.Build;
using XPact.Build.Manifest;
using XPact.Build.Platform;
using XPact.Core.IO;
using XPact.Core.Logging;

namespace XBT.BuildSystem
{
	/// <summary>
	/// Orchestrates the XBT -> XHT step: builds + writes the input manifest,
	/// locates XHT.exe, runs it, surfaces logs.
	/// </summary>
	[SupportedOSPlatform("windows")]
	public static class XHTExecution
	{
		/// <summary>
		/// Engine-wide version stamp written to every module entry. Phase 1 may
		/// switch this to per-module values once .Build.cs gains a Version
		/// property.
		/// </summary>
		private const string EngineVersionStamp = "5.9.0";

		/// <summary>
		/// File extensions XHT will eventually scan. Phase 0 only enumerates so
		/// the manifest reflects what is on disk. Header parsing comes Phase 1.
		/// </summary>
		private static readonly string[] PublicHeaderExtensions = new[] { ".h", ".hpp", ".hh", ".hxx", ".inl" };
		private static readonly string[] PrivateSourceExtensions = new[] { ".h", ".hpp", ".hh", ".hxx", ".inl", ".cpp", ".cxx", ".cc" };

		/// <summary>
		/// Run XHT for the given build. Writes the input manifest, invokes XHT.exe,
		/// and returns true on a clean exit. Failures are logged and false is returned;
		/// callers should abort the build.
		/// </summary>
		/// <param name="engineDir">Engine root (the directory containing Source/, Binaries/, Cache/).</param>
		/// <param name="targetRules">Resolved TargetRules for the requested -target.</param>
		/// <param name="modules">Modules included in the target.</param>
		/// <param name="targetIntermediateDir">Target-level intermediate dir, e.g. Engine/Cache/Intermediate/Build/Win64/HelloWorld/Development.</param>
		/// <returns>true on success, false on any failure (XHT exit != 0, manifest write error, XHT.exe missing).</returns>
		public static async Task<bool> RunAsync(
			DirectoryReference engineDir,
			TargetRules targetRules,
			IReadOnlyList<ModuleRules> modules,
			DirectoryReference targetIntermediateDir)
		{
			ArgumentNullException.ThrowIfNull(engineDir);
			ArgumentNullException.ThrowIfNull(targetRules);
			ArgumentNullException.ThrowIfNull(modules);
			ArgumentNullException.ThrowIfNull(targetIntermediateDir);

			Log.TraceInformation("[XBT] Running XHT for target {0}...", targetRules.Name);

			Stopwatch sw = Stopwatch.StartNew();

			// 1) Build manifest.
			UhtInputManifest manifest = BuildManifest(engineDir, targetRules, modules);

			// 2) Write to <intermediateDir>/xht-input-manifest.json.
			FileReference manifestPath = FileReference.Combine(targetIntermediateDir, "xht-input-manifest.json");
			Directory.CreateDirectory(manifestPath.Directory.FullName);

			JsonSerializerOptions writeOpts = new()
			{
				WriteIndented = true,
			};
			try
			{
				string json = JsonSerializer.Serialize(manifest, writeOpts);
				File.WriteAllText(manifestPath.FullName, json);
			}
			catch (Exception ex)
			{
				Log.TraceError("[XBT] Failed to write xht-input-manifest.json at '{0}': {1}", manifestPath.FullName, ex.Message);
				return false;
			}

			// 3) Locate XHT.exe.
			FileReference xhtExe;
			try
			{
				xhtExe = LocateXHTExe(engineDir);
			}
			catch (BuildException ex)
			{
				Log.TraceError("[XBT] {0}", ex.Message);
				return false;
			}

			// 4) Invoke.
			int exitCode = await InvokeXHTAsync(xhtExe, manifestPath, targetRules.Name).ConfigureAwait(false);
			sw.Stop();

			if (exitCode != 0)
			{
				Log.TraceError("[XBT] XHT failed with exit code {0}", exitCode);
				return false;
			}

			Log.TraceInformation("[XBT] XHT completed in {0}ms", sw.ElapsedMilliseconds);
			return true;
		}

		// ---------------------------------------------------------------------

		private static UhtInputManifest BuildManifest(
			DirectoryReference engineDir,
			TargetRules targetRules,
			IReadOnlyList<ModuleRules> modules)
		{
			UhtInputManifest manifest = new()
			{
				SchemaVersion = UhtInputManifest.CurrentSchemaVersion,
				TargetName = targetRules.Name,
				TargetType = targetRules.Type.ToString(),
				Platform = PlatformToFolder(targetRules.Platform),
				Configuration = targetRules.Configuration.ToString(),
			};

			string platformFolder = PlatformToFolder(targetRules.Platform);

			foreach (ModuleRules module in modules)
			{
				if (module.Directory is null)
				{
					Log.TraceWarning("[XBT] Module '{0}' has no Directory; skipping in XHT manifest.", module.Name);
					continue;
				}

				string moduleRel = MakeEngineRelative(engineDir, module.Directory.FullName);

				// Generated/ path mirrors the per-module intermediate layout BuildMode
				// uses for .obj output: <engine>/Cache/Intermediate/Build/<platform>/<target>/<config>/<module>/Generated.
				DirectoryReference generatedDir = DirectoryReference.Combine(
					engineDir, "Cache", "Intermediate", "Build",
					platformFolder,
					targetRules.Name,
					targetRules.Configuration.ToString(),
					module.Name,
					"Generated");
				// Create the Generated/ dir even though Phase 0 XHT writes no files; Phase 1
				// XHT will populate it and we want the path stable from day one.
				Directory.CreateDirectory(generatedDir.FullName);
				string generatedRel = MakeEngineRelative(engineDir, generatedDir.FullName);

				(List<string> publicHeaders, List<string> privateHeaders) = EnumerateModuleFiles(module.Directory);

				manifest.Modules.Add(new UhtInputManifestModule
				{
					Name = module.Name,
					ModulePath = moduleRel,
					OutputDir = generatedRel,
					PublicHeaders = publicHeaders,
					PrivateHeaders = privateHeaders,
					ModuleVersion = EngineVersionStamp,
				});
			}

			return manifest;
		}

		/// <summary>
		/// Walk a module directory looking for headers + sources. Files under a
		/// "Public" / "Classes" subdir are classified as public headers; everything
		/// else under "Private" or directly under the module root is private.
		/// Phase 0 follows the UHT convention here so Phase 1 can match Unreal's
		/// expectations without churn.
		/// </summary>
		private static (List<string> publicHeaders, List<string> privateHeaders) EnumerateModuleFiles(DirectoryReference moduleDir)
		{
			List<string> publics = new();
			List<string> privates = new();

			if (!Directory.Exists(moduleDir.FullName))
			{
				return (publics, privates);
			}

			foreach (string filePath in Directory.EnumerateFiles(moduleDir.FullName, "*", SearchOption.AllDirectories))
			{
				string ext = Path.GetExtension(filePath);
				bool isHeaderLike = Array.Exists(PublicHeaderExtensions, e => String.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
				bool isSourceLike = Array.Exists(PrivateSourceExtensions, e => String.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
				if (!isHeaderLike && !isSourceLike)
				{
					continue;
				}

				string rel = Path.GetRelativePath(moduleDir.FullName, filePath).Replace('\\', '/');
				string topLevel = rel.Split('/', 2)[0];
				bool isPublic = topLevel.Equals("Public", StringComparison.OrdinalIgnoreCase)
					|| topLevel.Equals("Classes", StringComparison.OrdinalIgnoreCase);

				if (isPublic && isHeaderLike)
				{
					publics.Add(rel);
				}
				else
				{
					privates.Add(rel);
				}
			}

			publics.Sort(StringComparer.Ordinal);
			privates.Sort(StringComparer.Ordinal);
			return (publics, privates);
		}

		private static string MakeEngineRelative(DirectoryReference engineDir, string absolutePath)
		{
			// engineDir points at <root>/Engine. We want paths relative to the
			// repo root (one level above Engine/) so manifest entries look like
			// "Engine/Source/Programs.Targets/HelloWorld" -- matching the documented
			// schema example.
			string repoRoot = engineDir.ParentDirectory?.FullName ?? engineDir.FullName;
			string rel = Path.GetRelativePath(repoRoot, absolutePath);
			return rel.Replace('\\', '/');
		}

		private static FileReference LocateXHTExe(DirectoryReference engineDir)
		{
			// Match XBT's expected location pattern: bin\Release\net9.0\XHT.exe under
			// the project tree. CallBuildTool.bat owns building this file; we only
			// validate presence here.
			FileReference candidate = FileReference.Combine(engineDir, "Source", "Programs", "XHT", "bin", "Release", "net9.0", "XHT.exe");
			if (!File.Exists(candidate.FullName))
			{
				throw new BuildException(
					"XHT.exe not found at '{0}'. Run CallBuildTool.bat to (re)build XHT before invoking XBT directly.",
					candidate.FullName);
			}
			return candidate;
		}

		private static async Task<int> InvokeXHTAsync(FileReference xhtExe, FileReference manifestPath, string targetName)
		{
			ProcessStartInfo psi = new()
			{
				FileName = xhtExe.FullName,
				Arguments = $"-manifest=\"{manifestPath.FullName}\" -target={targetName}",
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};

			using Process proc = new() { StartInfo = psi };
			List<Task> readers = new(2);
			proc.OutputDataReceived += (s, e) =>
			{
				if (!String.IsNullOrEmpty(e.Data))
				{
					// XHT prefixes its own lines already; pass them through verbatim at
					// Information level (no severity prefix) so they appear identically
					// to how XHT would render them on its own stdout.
					Log.WriteLine(LogEventType.Console, LogFormatOptions.NoSeverityPrefix, "{0}", e.Data);
				}
			};
			proc.ErrorDataReceived += (s, e) =>
			{
				if (!String.IsNullOrEmpty(e.Data))
				{
					Log.WriteLine(LogEventType.Error, LogFormatOptions.NoSeverityPrefix, "{0}", e.Data);
				}
			};

			if (!proc.Start())
			{
				Log.TraceError("[XBT] Failed to start XHT.exe at '{0}'", xhtExe.FullName);
				return -1;
			}
			proc.BeginOutputReadLine();
			proc.BeginErrorReadLine();

			await proc.WaitForExitAsync().ConfigureAwait(false);
			// Ensure the async stdout/stderr pumps have drained before we report exit.
			proc.WaitForExit();
			_ = readers;
			return proc.ExitCode;
		}

		private static string PlatformToFolder(UnrealTargetPlatform p)
		{
			if (p == UnrealTargetPlatform.Win64) { return "Win64"; }
			return p.ToString();
		}
	}
}
