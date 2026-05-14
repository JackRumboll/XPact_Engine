// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Real implementation of XBT's -genproject mode. The UE 5.9 entry point
// (GenerateProjectFilesMode.cs) dispatches across many generator types (VC, VS
// Workspace, VSCode, Make, CMake, QMake, KDevelop, CodeLite, XCode, Eddie,
// CLion, Rider, AndroidStudio, VProject) and walks the rules-source attribution
// of every project file to decide what to emit. XBT Task 0.4 only supports the
// Visual Studio generator with Win64 + {Debug, Development, Release} and a
// hand-picked set of solution folders (Engine/Source/Programs,
// Engine/Source/Programs.Targets, Engine/Documentation).
//
// Flow:
//   1) Locate engine root.
//   2) Compile rules under Engine/Source via the same RulesCompiler BuildMode uses.
//   3) Instantiate TargetRules for every discovered Program target.
//   4) Resolve LaunchModule + ExtraModuleNames so we know which .Build.cs files
//      and which on-disk source dirs contribute to each .vcxproj.
//   5) Glob C# .csproj files under Engine/Source/Programs/<Name>/X*.csproj so
//      the solution picks up XPact.Core, XPact.Build, XBT, and XHT (when Task 0.3
//      lands). Patterns intentionally exclude *.Tests.csproj — Phase 0 keeps the
//      solution focused on the production projects; tests can be added with their
//      own -genproject flag in a later phase.
//   6) Emit Engine/Cache/Projects/<Target>/<Target>.vcxproj per discovered target.
//   7) Emit Engine/Cache/Projects/XPactEngine.sln referencing everything.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using XBT.Configuration.Rules;
using XBT.ProjectFiles.VisualStudio;
using XPact.Build;
using XPact.Build.Platform;
using XPact.Core.IO;
using XPact.Core.Logging;

namespace XBT.Modes
{
	/// <summary>
	/// Generates Visual Studio project files for every discovered XBT target plus the supporting C# csprojs.
	/// </summary>
	[SupportedOSPlatform("windows")]
	public sealed class GenerateProjectFilesMode : IToolMode
	{
		/// <inheritdoc/>
		public string Name => "genproject";

		/// <inheritdoc/>
		public Task<int> ExecuteAsync(string[] args)
		{
			ArgumentNullException.ThrowIfNull(args);

			// 1) Engine root + script paths.
			DirectoryReference engineDir = LocateEngineRoot();
			Log.TraceInformation("[XBT] Engine root: {0}", engineDir.FullName);

			FileReference callBuildToolBat = FileReference.Combine(engineDir, "Scripts", "Windows", "CallBuildTool.bat");
			if (!File.Exists(callBuildToolBat.FullName))
			{
				throw new BuildException("Required script not found: {0}", callBuildToolBat.FullName);
			}

			// 2) Compile rules under Engine/Source (same path BuildMode uses; this picks up both .Target.cs and .Build.cs everywhere).
			DirectoryReference searchRoot = DirectoryReference.Combine(engineDir, "Source");
			RulesAssembly rules = RulesCompiler.Compile(new[] { searchRoot });

			// 3) Instantiate every Program target (Phase 0 only emits projects for Program targets).
			List<VCProject> cppProjects = new();
			DirectoryReference projectsDir = DirectoryReference.Combine(engineDir, "Cache", "Projects");
			foreach (string targetName in rules.TargetNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
			{
				TargetInfo info = new(
					name: targetName,
					platform: UnrealTargetPlatform.Win64,
					configuration: UnrealTargetConfiguration.Development,
					architectures: new UnrealArchitectures(UnrealArch.X64));

				TargetRules targetRules;
				try
				{
					targetRules = rules.CreateTargetRules(targetName, info);
				}
				catch (BuildException ex)
				{
					Log.TraceWarning("[XBT] Skipping target '{0}': {1}", targetName, ex.Message);
					continue;
				}

				if (targetRules.Type != TargetType.Program)
				{
					Log.TraceLog("[XBT] Skipping target '{0}' (type={1}); only Program targets generate vcxproj in Phase 0.", targetName, targetRules.Type);
					continue;
				}

				if (targetRules.File is null)
				{
					Log.TraceWarning("[XBT] Target '{0}' has no .Target.cs file reference; skipping.", targetName);
					continue;
				}

				// 4) Resolve modules.
				List<string> moduleNames = new();
				if (!String.IsNullOrEmpty(targetRules.LaunchModuleName))
				{
					moduleNames.Add(targetRules.LaunchModuleName!);
				}
				moduleNames.AddRange(targetRules.ExtraModuleNames);

				List<ModuleRules> modules = new();
				foreach (string moduleName in moduleNames)
				{
					try
					{
						modules.Add(rules.CreateModuleRules(moduleName, targetRules));
					}
					catch (BuildException ex)
					{
						Log.TraceWarning("[XBT] Target '{0}': skipping module '{1}': {2}", targetName, moduleName, ex.Message);
					}
				}

				// 5) Emit the .vcxproj (and .vcxproj.filters).
				FileReference vcxprojPath = FileReference.Combine(projectsDir, targetName, targetName + ".vcxproj");
				VCProject project = new(targetName, vcxprojPath, targetRules.File!, targetRules, modules);
				IReadOnlyList<FileReference> written = project.Write(engineDir, callBuildToolBat);
				foreach (FileReference w in written)
				{
					Log.TraceInformation("[XBT] Wrote {0}", w.FullName);
				}
				cppProjects.Add(project);
			}

			// 6) C# project references (XPact.Core, XPact.Build, XBT, XHT). Glob the production projects by X*.csproj — picks up Task 0.3's XHT.csproj when it lands.
			DirectoryReference programsDir = DirectoryReference.Combine(engineDir, "Source", "Programs");
			List<FileReference> cSharpProjects = new();
			if (Directory.Exists(programsDir.FullName))
			{
				foreach (string subDir in Directory.EnumerateDirectories(programsDir.FullName))
				{
					string subName = Path.GetFileName(subDir);
					if (subName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))
					{
						// Phase 0 keeps tests out of the engine .sln; they ship with XPact_Engine.sln from the root.
						continue;
					}
					foreach (string csprojPath in Directory.EnumerateFiles(subDir, "X*.csproj", SearchOption.TopDirectoryOnly))
					{
						cSharpProjects.Add(new FileReference(csprojPath));
					}
				}
			}
			cSharpProjects = cSharpProjects.OrderBy(p => p.FullName, StringComparer.OrdinalIgnoreCase).ToList();
			foreach (FileReference cs in cSharpProjects)
			{
				Log.TraceLog("[XBT] Including C# project: {0}", cs.FullName);
			}

			// 7) Emit the .sln.
			FileReference solutionFile = FileReference.Combine(projectsDir, "XPactEngine.sln");
			VCSolution solution = new(solutionFile, cppProjects, cSharpProjects);
			FileReference writtenSln = solution.Write();
			Log.TraceInformation("[XBT] Wrote {0}", writtenSln.FullName);

			int projectCount = cppProjects.Count + cSharpProjects.Count;
			Log.TraceInformation("Generated XPactEngine.sln + {0} project(s)", projectCount);

			_ = args;
			return Task.FromResult(0);
		}

		// ---------------------------------------------------------------------

		private static DirectoryReference LocateEngineRoot()
		{
			// Same walk pattern as BuildMode.LocateEngineRoot. Kept inline rather than promoted to a shared helper because
			// only the two modes need it; if a third mode arrives in a later phase we'll promote then.
			string? cur = AppContext.BaseDirectory;
			while (!String.IsNullOrEmpty(cur))
			{
				string sourceCheck = Path.Combine(cur, "Engine", "Source");
				if (Directory.Exists(sourceCheck))
				{
					return new DirectoryReference(Path.Combine(cur, "Engine"));
				}
				string parent = Path.Combine(cur, "..");
				string normalized = Path.GetFullPath(parent);
				if (String.Equals(normalized, cur, StringComparison.OrdinalIgnoreCase))
				{
					break;
				}
				cur = normalized;
			}
			cur = Environment.CurrentDirectory;
			while (!String.IsNullOrEmpty(cur))
			{
				string sourceCheck = Path.Combine(cur, "Engine", "Source");
				if (Directory.Exists(sourceCheck))
				{
					return new DirectoryReference(Path.Combine(cur, "Engine"));
				}
				DirectoryInfo? di = Directory.GetParent(cur);
				if (di is null)
				{
					break;
				}
				cur = di.FullName;
			}
			throw new BuildException("Could not locate engine root (looking for Engine\\Source) above CWD '{0}' or executable directory.", Environment.CurrentDirectory);
		}
	}
}
