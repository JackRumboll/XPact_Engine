// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealBuildTool.BuildMode. The UE 5.9 version is the central
// orchestrator: it parses command-line targets, walks the rules assembly,
// builds the action graph, dispatches the executor, and writes makefile/receipt
// caches. XBT Task 0.2 only handles a single target with a single launch
// module, no makefile cache, no plugins. We:
//   1. Discover and Roslyn-compile all .Target.cs / .Build.cs under EngineDir
//   2. Instantiate TargetRules for the requested -target
//   3. Resolve the launch module + any ExtraModuleNames
//   4. For each module, run our toolchain to produce CompileActions + optional
//      PCHGenerateAction
//   5. Build a single LinkAction
//   6. Hand the action list to ParallelExecutor

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using XBT.Actions;
using XBT.Configuration.Rules;
using XBT.Executors;
using XBT.Platform.Windows;
using XBT.BuildSystem;
using XPact.Build;
using XPact.Build.Platform;
using XPact.Core.IO;
using XPact.Core.Logging;

namespace XBT.Modes
{
	/// <summary>
	/// The default mode invoked by `-build`.
	/// </summary>
	[SupportedOSPlatform("windows")]
	public sealed class BuildMode : IToolMode
	{
		/// <inheritdoc/>
		public string Name => "build";

		/// <inheritdoc/>
		public async Task<int> ExecuteAsync(string[] args)
		{
			ArgumentNullException.ThrowIfNull(args);

			BuildOptions options;
			try
			{
				options = BuildOptions.Parse(args);
			}
			catch (BuildException ex)
			{
				Log.TraceError("{0}", ex.Message);
				return 1;
			}

			DirectoryReference engineDir = LocateEngineRoot();
			Log.TraceInformation("[XBT] Engine root: {0}", engineDir.FullName);
			Log.TraceInformation("[XBT] Building target '{0}' for {1} {2} ({3})",
				options.TargetName, options.Platform, options.Configuration, options.Architecture);

			// 1) Compile rules
			DirectoryReference searchRoot = DirectoryReference.Combine(engineDir, "Source");
			RulesAssembly rules = RulesCompiler.Compile(new[] { searchRoot });

			// 2) Instantiate TargetRules
			TargetInfo info = new(
				name: options.TargetName,
				platform: options.Platform,
				configuration: options.Configuration,
				architectures: new UnrealArchitectures(options.Architecture));
			TargetRules targetRules = rules.CreateTargetRules(options.TargetName, info);

			Log.TraceInformation("[XBT] Target type: {0}, LaunchModule: {1}, bUseSharedPCHs: {2}, bUseAutoRTFMCompiler: {3}",
				targetRules.Type, targetRules.LaunchModuleName, targetRules.bUseSharedPCHs, targetRules.bUseAutoRTFMCompiler);

			// 3) Resolve modules: launch + extras are "primary" modules whose obj
			// files go into the .exe link; their PublicDependencyModuleNames are
			// resolved transitively into "dependency" modules that compile to
			// separate static libraries and link into the final exe via .lib.
			List<string> primaryNames = new();
			if (!String.IsNullOrEmpty(targetRules.LaunchModuleName))
			{
				primaryNames.Add(targetRules.LaunchModuleName!);
			}
			primaryNames.AddRange(targetRules.ExtraModuleNames);

			List<ModuleRules> primaryModules = primaryNames
				.Select(n => rules.CreateModuleRules(n, targetRules))
				.ToList();

			// Walk PublicDependencyModuleNames and PrivateDependencyModuleNames transitively.
			List<ModuleRules> dependencyModules = new();
			HashSet<string> seen = new(primaryNames, StringComparer.OrdinalIgnoreCase);
			Queue<ModuleRules> work = new(primaryModules);
			while (work.Count > 0)
			{
				ModuleRules current = work.Dequeue();
				foreach (string dep in current.PublicDependencyModuleNames.Concat(current.PrivateDependencyModuleNames))
				{
					if (!seen.Add(dep))
					{
						continue;
					}
					ModuleRules depRules = rules.CreateModuleRules(dep, targetRules);
					dependencyModules.Add(depRules);
					work.Enqueue(depRules);
				}
			}

			// All modules collected in compile-order: dependencies first (.lib outputs
			// must exist before final link), primary modules last.
			List<ModuleRules> modules = new();
			modules.AddRange(dependencyModules);
			modules.AddRange(primaryModules);
			HashSet<string> primarySet = new(primaryNames, StringComparer.OrdinalIgnoreCase);

			// 4) Resolve toolchain
			VCEnvironment vc = VCEnvironment.Discover();
			Log.TraceInformation("[XBT] MSVC {0} | Windows SDK {1}", vc.VCToolChainVersion, vc.WindowsSdkVersion);

			// 4a) AutoRTFM toolchain detection. The AutoRTFM clang fork (verse-clang-cl.exe)
			// ships separately from the Unreal source tree. When present, we swap MSVC out
			// and emit -Xclang -autortfm-mappings flags from per-module
			// AutoRTFMExternalMappingFiles. When absent (the current Path-2 state), MSVC
			// is used and the XAutoRTFM runtime falls back to inline no-op semantics from
			// its public headers.
			bool autoRTFMCompilerRequested = targetRules.bUseAutoRTFMCompiler;
			bool autoRTFMCompilerVendored = VCEnvironment.TryGetAutoRTFMCompilerPath(engineDir, out FileReference? autoRTFMCompilerPath);
			bool autoRTFMCompilerEffective = autoRTFMCompilerRequested && autoRTFMCompilerVendored;
			if (autoRTFMCompilerRequested && !autoRTFMCompilerVendored)
			{
				Log.TraceInformation("[XBT] Toolchain: cl.exe (bUseAutoRTFMCompiler=true requested, but verse-clang-cl.exe not vendored at '{0}' — using MSVC; XAutoRTFM compiles to no-op fallback)",
					System.IO.Path.Combine(engineDir.FullName, "Source", "ThirdParty", "UnrealInstrumentation", "bin", "verse-clang-cl.exe"));
			}
			else if (autoRTFMCompilerEffective && autoRTFMCompilerPath is not null)
			{
				Log.TraceInformation("[XBT] Toolchain: {0} (AutoRTFM compiler active)", autoRTFMCompilerPath.FullName);
			}
			else if (!autoRTFMCompilerRequested)
			{
				Log.TraceInformation("[XBT] Toolchain: cl.exe (bUseAutoRTFMCompiler=false)");
			}

			// Per-module AutoRTFM mapping-file accounting. Always logged so the
			// information is visible even under MSVC fallback (acceptance #5).
			foreach (ModuleRules m in modules)
			{
				if (m.AutoRTFMExternalMappingFiles.Count > 0)
				{
					Log.TraceInformation("[XBT] {0} has {1} AutoRTFM mapping file(s): {2}",
						m.Name,
						m.AutoRTFMExternalMappingFiles.Count,
						String.Join(", ", m.AutoRTFMExternalMappingFiles.Select(System.IO.Path.GetFileName)));
				}
			}

			// 5) Compute output directories
			DirectoryReference intermediateRoot = DirectoryReference.Combine(
				engineDir, "Cache", "Intermediate", "Build",
				PlatformToFolder(options.Platform),
				options.TargetName,
				options.Configuration.ToString());
			DirectoryReference binariesDir = DirectoryReference.Combine(
				engineDir, "Binaries",
				PlatformToFolder(options.Platform),
				options.TargetName,
				options.Configuration.ToString());

			Directory.CreateDirectory(intermediateRoot.FullName);
			Directory.CreateDirectory(binariesDir.FullName);

			// 5b) Run XHT (Phase 0 Task 0.3 seam). Writes xht-input-manifest.json
			// at <intermediateRoot>/xht-input-manifest.json and shells out to
			// XHT.exe -manifest=<path> -target=<name>. Phase 0 XHT is a skeleton:
			// it logs which modules it would scan and exits 0. The point here is
			// the XBT -> XHT contract; Phase 1 Task 1.9 swaps in the real parser.
			bool xhtOk = await XHTExecution.RunAsync(engineDir, targetRules, modules, intermediateRoot).ConfigureAwait(false);
			if (!xhtOk)
			{
				return 1;
			}

			// 6) Build a TargetMakefile and append actions
			TargetMakefile makefile = new(options.TargetName, options.Platform, options.Configuration, options.Architecture)
			{
				IntermediateDirectory = intermediateRoot,
				OutputFile = FileReference.Combine(binariesDir, options.TargetName + ".exe"),
			};

			(string includeEnv, string libEnv) = XBTWindows.BuildEnvStrings(vc);

			// 6a) Determine whether to use a shared PCH.
			bool anyModuleWantsSharedPCH = modules.Any(m =>
				m.PCHUsage == ModuleRules.PCHUsageMode.UseSharedPCHs
				|| m.PCHUsage == ModuleRules.PCHUsageMode.UseExplicitOrSharedPCHs);
			bool sharedPCHEnabled = !options.ForceNoPCH && targetRules.bUsePCHFiles && targetRules.bUseSharedPCHs && anyModuleWantsSharedPCH;
			if (options.ForceNoPCH)
			{
				Log.TraceInformation("[XBT] -no-pch flag set: forcing PCHUsage=NoPCHs for all modules.");
			}

			FileReference? sharedPCHHeader = null;
			FileReference? sharedPCHFile = null;
			FileReference? sharedPCHObj = null;
			if (sharedPCHEnabled)
			{
				sharedPCHHeader = ResolveSharedPCHHeader(engineDir);
				DirectoryReference pchOutDir = DirectoryReference.Combine(intermediateRoot, "PCH");
				Directory.CreateDirectory(pchOutDir.FullName);
				sharedPCHFile = FileReference.Combine(pchOutDir, "XCorePCH.Stub.pch");
				sharedPCHObj = FileReference.Combine(pchOutDir, "XCorePCH.Stub.obj");

				PCHGenerateAction pchAction = CreatePCHGenerateAction(
					engineDir, vc, includeEnv, libEnv,
					sharedPCHHeader, sharedPCHFile, sharedPCHObj,
					targetRules, options);
				makefile.Actions.Add(pchAction);
			}

			// 6b) For each module, emit one CompileAction per .cpp. Dependency
			// modules also produce a per-module .lib via lib.exe; primary modules
			// (launch + extras) contribute their .obj files directly to the final
			// .exe link.
			List<FileReference> primaryObjects = new();
			if (sharedPCHEnabled && sharedPCHObj is not null)
			{
				primaryObjects.Add(sharedPCHObj);
			}
			List<FileReference> dependencyLibs = new();

			foreach (ModuleRules module in modules)
			{
				DirectoryReference moduleIntermediateDir = DirectoryReference.Combine(intermediateRoot, module.Name);
				Directory.CreateDirectory(moduleIntermediateDir.FullName);

				// Discover .cpp files directly under the module directory (Phase 0; recursive scan is future).
				if (module.Directory is null)
				{
					throw new BuildException("Module '{0}' has no source directory", module.Name);
				}
				List<FileReference> sources = Directory.EnumerateFiles(module.Directory.FullName, "*.cpp", SearchOption.AllDirectories)
					.Select(p => new FileReference(p))
					.ToList();

				bool isPrimary = primarySet.Contains(module.Name);

				if (sources.Count == 0)
				{
					if (isPrimary)
					{
						Log.TraceWarning("Module '{0}': no .cpp files found under {1}", module.Name, module.Directory.FullName);
					}
					else
					{
						// A header-only dependency module is legal (no .obj => no .lib).
						Log.TraceLog("[XBT] Module '{0}': header-only (no .cpp files); skipping lib step.", module.Name);
					}
					continue;
				}

				bool moduleUsesPCH = sharedPCHEnabled
					&& (module.PCHUsage == ModuleRules.PCHUsageMode.UseSharedPCHs
						|| module.PCHUsage == ModuleRules.PCHUsageMode.UseExplicitOrSharedPCHs);

				List<FileReference> moduleObjects = new();
				foreach (FileReference src in sources)
				{
					FileReference obj = FileReference.Combine(moduleIntermediateDir, Path.GetFileNameWithoutExtension(src.FullName) + ".obj");
					CompileAction ca = CreateCompileAction(engineDir, vc, includeEnv, libEnv, src, obj, targetRules, module, options, moduleIntermediateDir,
						pchHeader: moduleUsesPCH ? sharedPCHHeader : null,
						pchFile: moduleUsesPCH ? sharedPCHFile : null,
						modules: modules,
						autoRTFMCompilerEffective: autoRTFMCompilerEffective,
						autoRTFMCompilerPath: autoRTFMCompilerPath);
					if (moduleUsesPCH && sharedPCHFile is not null)
					{
						// Add PCH file as prerequisite so we cannot compile until PCH is generated.
						ca.Prerequisites.Add(sharedPCHFile);
					}
					makefile.Actions.Add(ca);
					moduleObjects.Add(obj);
				}

				if (isPrimary)
				{
					primaryObjects.AddRange(moduleObjects);
				}
				else
				{
					// Archive this dependency module's objs into <Name>.lib.
					FileReference moduleLib = FileReference.Combine(moduleIntermediateDir, module.Name + ".lib");
					LibAction lib = CreateLibAction(vc, includeEnv, libEnv, moduleObjects, moduleLib);
					makefile.Actions.Add(lib);
					dependencyLibs.Add(moduleLib);
					Log.TraceInformation("[XBT] Will archive {0} object(s) into {1}", moduleObjects.Count, moduleLib.FullName);
				}
			}

			// 6c) Link the final exe: primary .obj files + dependency .lib files.
			LinkAction link = CreateLinkAction(vc, includeEnv, libEnv, primaryObjects, dependencyLibs, makefile.OutputFile!, targetRules);
			makefile.Actions.Add(link);

			// 7) Execute
			Log.TraceInformation("[XBT] Executing {0} actions ({1} parallel)", makefile.Actions.Count, Environment.ProcessorCount);

			ParallelExecutor executor = new();
			Stopwatch sw = Stopwatch.StartNew();
			bool ok;
			using (CancellationTokenSource cts = new())
			{
				ok = await executor.RunAsync(makefile.Actions, cts.Token).ConfigureAwait(false);
			}
			sw.Stop();

			if (!ok)
			{
				Log.TraceError("[XBT] Build FAILED in {0:F2}s", sw.Elapsed.TotalSeconds);
				return 1;
			}

			Log.TraceInformation("[XBT] Build succeeded in {0:F2}s -> {1}", sw.Elapsed.TotalSeconds, makefile.OutputFile!.FullName);
			return 0;
		}

		// ---------------------------------------------------------------------

		private static PCHGenerateAction CreatePCHGenerateAction(
			DirectoryReference engineDir,
			VCEnvironment vc,
			string includeEnv,
			string libEnv,
			FileReference pchHeader,
			FileReference pchFile,
			FileReference pchObj,
			TargetRules target,
			BuildOptions options)
		{
			// Generate a stub .cpp next to the .pch
			FileReference stub = FileReference.Combine(pchObj.Directory, "XCorePCH.Stub.cpp");
			File.WriteAllText(stub.FullName, "// Auto-generated by XBT: PCH generation TU\n#include \"" + pchHeader.GetFileName() + "\"\n");

			CppCompileEnvironment env = new()
			{
				Platform = options.Platform,
				Architecture = options.Architecture,
				Configuration = options.Configuration,
				Optimization = OptimizationForConfig(options.Configuration),
				OutputDirectory = pchObj.Directory,
				bUseSharedPCHs = target.bUseSharedPCHs,
				bUseAutoRTFMCompiler = target.bUseAutoRTFMCompiler,
				bUseStaticCRT = target.bUseStaticCRT,
				bUseDebugCRT = target.bDebugBuildsActuallyUseDebugCRT && options.Configuration == UnrealTargetConfiguration.Debug,
				bUsePDBFiles = target.bUsePDBFiles,
				bGeneratingPCH = true,
			};
			// PCH header dir is implicitly added by MakePCHGenerateArgs.

			string args = XBTWindows.MakePCHGenerateArgs(env, pchHeader, stub, pchFile, pchObj);
			PCHGenerateAction action = new()
			{
				CommandPath = vc.ClExe,
				CommandArguments = args,
				WorkingDirectory = XBTWindows.GetWorkingDirectory(vc),
				StatusDescription = $"GeneratePCH {pchHeader.GetFileName()}",
				PCHHeader = pchHeader,
				StubSourceFile = stub,
				PCHFile = pchFile,
				ObjectFile = pchObj,
			};
			action.EnvironmentVariables["INCLUDE"] = includeEnv;
			action.EnvironmentVariables["LIB"] = libEnv;
			action.Prerequisites.Add(pchHeader);
			action.ProducedItems.Add(pchFile);
			action.ProducedItems.Add(pchObj);
			_ = engineDir; // unused; retained for symmetry / future relative-path translation
			return action;
		}

		private static CompileAction CreateCompileAction(
			DirectoryReference engineDir,
			VCEnvironment vc,
			string includeEnv,
			string libEnv,
			FileReference source,
			FileReference obj,
			TargetRules target,
			ModuleRules module,
			BuildOptions options,
			DirectoryReference moduleIntermediateDir,
			FileReference? pchHeader,
			FileReference? pchFile,
			IReadOnlyList<ModuleRules> modules,
			bool autoRTFMCompilerEffective,
			FileReference? autoRTFMCompilerPath)
		{
			CppCompileEnvironment env = new()
			{
				Platform = options.Platform,
				Architecture = options.Architecture,
				Configuration = options.Configuration,
				Optimization = OptimizationForConfig(options.Configuration),
				OutputDirectory = moduleIntermediateDir,
				bUseSharedPCHs = target.bUseSharedPCHs,
				bUseAutoRTFMCompiler = target.bUseAutoRTFMCompiler,
				bUseAutoRTFMCompilerEffective = autoRTFMCompilerEffective,
				AutoRTFMCompilerPath = autoRTFMCompilerPath,
				bUseStaticCRT = target.bUseStaticCRT,
				bUseDebugCRT = target.bDebugBuildsActuallyUseDebugCRT && options.Configuration == UnrealTargetConfiguration.Debug,
				bUsePDBFiles = target.bUsePDBFiles,
				PCHHeader = pchHeader,
				PCHFile = pchFile,
				bGeneratingPCH = false,
			};
			// Module-private definitions
			env.Definitions.AddRange(module.PrivateDefinitions);
			env.Definitions.AddRange(module.PublicDefinitions);

			// Dependency-module public definitions are visible.
			HashSet<string> moduleDeps = new(module.PublicDependencyModuleNames.Concat(module.PrivateDependencyModuleNames), StringComparer.OrdinalIgnoreCase);
			foreach (ModuleRules other in modules)
			{
				if (moduleDeps.Contains(other.Name))
				{
					env.Definitions.AddRange(other.PublicDefinitions);
				}
			}

			// Module include paths
			if (module.Directory is not null)
			{
				env.IncludePaths.Add(module.Directory);
			}
			foreach (string p in module.PrivateIncludePaths.Concat(module.PublicIncludePaths))
			{
				DirectoryReference combined;
				try
				{
					combined = Path.IsPathRooted(p)
						? new DirectoryReference(p)
						: DirectoryReference.Combine(module.Directory!, p);
				}
				catch (Exception)
				{
					continue;
				}
				env.IncludePaths.Add(combined);
			}

			// Dependency-module public include paths
			foreach (ModuleRules other in modules)
			{
				if (!moduleDeps.Contains(other.Name) || other.Directory is null)
				{
					continue;
				}
				env.IncludePaths.Add(other.Directory);
				foreach (string p in other.PublicIncludePaths)
				{
					DirectoryReference combined;
					try
					{
						combined = Path.IsPathRooted(p)
							? new DirectoryReference(p)
							: DirectoryReference.Combine(other.Directory!, p);
					}
					catch (Exception)
					{
						continue;
					}
					env.IncludePaths.Add(combined);
				}
			}

			// Always allow the PCH header's directory on the include path (for #include "XCorePCH.Stub.h").
			if (pchHeader is not null)
			{
				env.IncludePaths.Add(pchHeader.Directory);
			}

			// AutoRTFM mapping files: under effective mode these flow to clang via
			// -Xclang -autortfm-mappings. Resolved to absolute paths so the compile
			// action can run from any working directory. Under MSVC fallback the
			// list is still populated (so XBTWindows could log it), but
			// bUseAutoRTFMCompilerEffective is false so no flags are emitted.
			foreach (string mapping in module.AutoRTFMExternalMappingFiles)
			{
				string absolute = Path.IsPathRooted(mapping)
					? mapping
					: Path.GetFullPath(Path.Combine(engineDir.FullName, "Source", mapping));
				env.AutoRTFMExternalMappingFiles.Add(absolute);
			}

			string args = XBTWindows.MakeClCompileArgs(env, source, obj);

			// Toolchain swap: under effective AutoRTFM mode the compiler is verse-clang-cl.exe.
			FileReference compilerPath = autoRTFMCompilerEffective && autoRTFMCompilerPath is not null
				? autoRTFMCompilerPath
				: vc.ClExe;

			CompileAction action = new()
			{
				CommandPath = compilerPath,
				CommandArguments = args,
				WorkingDirectory = XBTWindows.GetWorkingDirectory(vc),
				StatusDescription = $"Compile {source.GetFileName()}",
				SourceFile = source,
				ObjectFile = obj,
			};
			action.EnvironmentVariables["INCLUDE"] = includeEnv;
			action.EnvironmentVariables["LIB"] = libEnv;
			action.Prerequisites.Add(source);
			if (env.bUseAutoRTFMCompilerEffective && env.AutoRTFMCompilerPath is not null)
			{
				action.Prerequisites.Add(env.AutoRTFMCompilerPath);
				foreach (string mappingFile in env.AutoRTFMExternalMappingFiles)
				{
					action.Prerequisites.Add(new FileReference(mappingFile));
				}
			}
			action.ProducedItems.Add(obj);
			return action;
		}

		private static LibAction CreateLibAction(VCEnvironment vc, string includeEnv, string libEnv, IReadOnlyList<FileReference> objects, FileReference outputLib)
		{
			string args = XBTWindows.MakeLibArgs(objects, outputLib);
			LibAction action = new()
			{
				CommandPath = vc.LibExe,
				CommandArguments = args,
				WorkingDirectory = XBTWindows.GetWorkingDirectory(vc),
				StatusDescription = $"Lib {outputLib.GetFileName()}",
				OutputFile = outputLib,
			};
			action.EnvironmentVariables["INCLUDE"] = includeEnv;
			action.EnvironmentVariables["LIB"] = libEnv;
			foreach (FileReference o in objects)
			{
				action.Prerequisites.Add(o);
			}
			action.ProducedItems.Add(outputLib);
			return action;
		}

		private static LinkAction CreateLinkAction(VCEnvironment vc, string includeEnv, string libEnv, IReadOnlyList<FileReference> objects, IReadOnlyList<FileReference> dependencyLibs, FileReference outputFile, TargetRules target)
		{
			LinkEnvironment env = new()
			{
				OutputFile = outputFile,
				OutputType = LinkOutputType.Executable,
				Subsystem = LinkerSubsystem.Console,
				bUsePDBFiles = target.bUsePDBFiles,
				bUseIncrementalLinking = target.bUseIncrementalLinking,
			};
			env.InputFiles.AddRange(objects);
			env.AdditionalLibraries.AddRange(dependencyLibs);
			foreach (DirectoryReference lp in vc.LibraryPaths)
			{
				env.LibraryPaths.Add(lp);
			}
			// Default Win32/CRT libs for a console exe
			env.SystemLibraries.AddRange(new[] { "kernel32", "user32", "advapi32" });

			string args = XBTWindows.MakeLinkArgs(env);
			LinkAction la = new()
			{
				CommandPath = vc.LinkExe,
				CommandArguments = args,
				WorkingDirectory = XBTWindows.GetWorkingDirectory(vc),
				StatusDescription = $"Link {outputFile.GetFileName()}",
				OutputFile = outputFile,
			};
			la.EnvironmentVariables["INCLUDE"] = includeEnv;
			la.EnvironmentVariables["LIB"] = libEnv;
			foreach (FileReference o in objects)
			{
				la.Prerequisites.Add(o);
			}
			foreach (FileReference lib in dependencyLibs)
			{
				la.Prerequisites.Add(lib);
			}
			la.ProducedItems.Add(outputFile);
			return la;
		}

		// ---------------------------------------------------------------------

		private static OptimizationMode OptimizationForConfig(UnrealTargetConfiguration cfg)
		{
			return cfg switch
			{
				UnrealTargetConfiguration.Debug => OptimizationMode.Disabled,
				UnrealTargetConfiguration.DebugGame => OptimizationMode.Disabled,
				UnrealTargetConfiguration.Shipping => OptimizationMode.Shipping,
				_ => OptimizationMode.Development,
			};
		}

		private static string PlatformToFolder(UnrealTargetPlatform p)
		{
			if (p == UnrealTargetPlatform.Win64) { return "Win64"; }
			return p.ToString();
		}

		private static FileReference ResolveSharedPCHHeader(DirectoryReference engineDir)
		{
			FileReference candidate = FileReference.Combine(engineDir, "Source", "Runtime", "XCorePCH.Stub.h");
			if (!File.Exists(candidate.FullName))
			{
				throw new BuildException("Shared PCH stub header not found at '{0}'", candidate.FullName);
			}
			return candidate;
		}

		private static DirectoryReference LocateEngineRoot()
		{
			// XBT.exe lives at Engine\Source\Programs\XBT\bin\<Config>\net9.0\XBT.exe (when run via dotnet)
			// or Engine\Binaries\<Platform>\XBT.exe (when published). Walk up looking for 'Engine\Source'.
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
			// Final fallback: walk up from CWD
			cur = Environment.CurrentDirectory;
			while (!String.IsNullOrEmpty(cur))
			{
				string sourceCheck = Path.Combine(cur, "Engine", "Source");
				if (Directory.Exists(sourceCheck))
				{
					return new DirectoryReference(Path.Combine(cur, "Engine"));
				}
				DirectoryInfo? di = System.IO.Directory.GetParent(cur);
				if (di is null)
				{
					break;
				}
				cur = di.FullName;
			}
			throw new BuildException("Could not locate engine root (looking for Engine\\Source) above CWD '{0}' or executable directory.", Environment.CurrentDirectory);
		}
	}

	/// <summary>
	/// Parsed command-line options for BuildMode.
	/// </summary>
	internal sealed class BuildOptions
	{
		public string TargetName { get; init; } = String.Empty;
		public UnrealTargetPlatform Platform { get; init; } = UnrealTargetPlatform.Win64;
		public UnrealTargetConfiguration Configuration { get; init; } = UnrealTargetConfiguration.Development;
		public UnrealArch Architecture { get; init; } = UnrealArch.X64;

		/// <summary>If true, force PCHUsage=NoPCHs for every module. Used for the PCH speedup benchmark.</summary>
		public bool ForceNoPCH { get; init; }

		public static BuildOptions Parse(string[] args)
		{
			string? target = null;
			UnrealTargetPlatform platform = UnrealTargetPlatform.Win64;
			UnrealTargetConfiguration config = UnrealTargetConfiguration.Development;
			UnrealArch arch = UnrealArch.X64;
			bool noPch = false;

			foreach (string raw in args)
			{
				string a = raw.TrimStart('-', '/');
				int eq = a.IndexOf('=', StringComparison.Ordinal);
				string key = eq < 0 ? a : a[..eq];
				string val = eq < 0 ? String.Empty : a[(eq + 1)..];
				switch (key.ToUpperInvariant())
				{
					case "BUILD":
						break;
					case "TARGET":
						target = val;
						break;
					case "PLATFORM":
						if (!String.IsNullOrEmpty(val))
						{
							platform = val.Equals("Windows", StringComparison.OrdinalIgnoreCase)
								? UnrealTargetPlatform.Win64
								: UnrealTargetPlatform.Parse(val);
						}
						break;
					case "CONFIG":
					case "CONFIGURATION":
						if (!String.IsNullOrEmpty(val) && Enum.TryParse<UnrealTargetConfiguration>(val, ignoreCase: true, out UnrealTargetConfiguration c))
						{
							config = c;
						}
						break;
					case "ARCH":
					case "ARCHITECTURE":
						if (!String.IsNullOrEmpty(val))
						{
							arch = UnrealArch.Parse(val);
						}
						break;
					case "NO-PCH":
					case "NOPCH":
						noPch = true;
						break;
				}
			}

			if (String.IsNullOrEmpty(target))
			{
				throw new BuildException("Missing required argument: -target=<TargetName>");
			}
			return new BuildOptions
			{
				TargetName = target,
				Platform = platform,
				Configuration = config,
				Architecture = arch,
				ForceNoPCH = noPch,
			};
		}
	}
}
