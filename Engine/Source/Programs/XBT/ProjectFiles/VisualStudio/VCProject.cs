// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealBuildTool.VCProject. The UE 5.9 file is ~2.6 kLoC because
// it has to handle a fully-fledged module graph: per-file IntelliSense, shared
// include path optimization, multiple architectures, Linux/Mac/Android/iOS
// generators, .vcxproj.user files, etc. XBT Task 0.4 only needs to emit an
// NMake-style vcxproj for a single Windows x64 C++ target so VS can drive XBT
// for build/rebuild/clean and provide IntelliSense for the existing module
// directory. Each generated .vcxproj is paired with a .vcxproj.filters that
// preserves the on-disk folder layout under the module directory.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using XBT.Configuration.Rules;
using XPact.Core.IO;

namespace XBT.ProjectFiles.VisualStudio
{
	/// <summary>
	/// One Visual Studio C++ project (NMake-flavoured .vcxproj) for a discovered XBT target.
	/// </summary>
	public sealed class VCProject
	{
		/// <summary>
		/// Solution-level configuration names we expose to users. "Release" is the user-facing label;
		/// XBT itself only knows UnrealTargetConfiguration.Shipping, so we translate at the NMake command line
		/// (see <see cref="ToXBTConfig"/>). Keeping the names identical to those on the solution avoids the
		/// "active config doesn't match" warnings VS shows when the .sln and .vcxproj configs diverge.
		/// </summary>
		private static readonly string[] s_supportedConfigurations = { "Debug", "Development", "Release" };

		/// <summary>Map a user-facing configuration to the underlying XBT (-config=) value.</summary>
		private static string ToXBTConfig(string solutionConfig) => solutionConfig switch
		{
			"Release" => "Shipping",
			_ => solutionConfig,
		};

		/// <summary>Target name (e.g. "HelloWorld"). Drives output paths and NMake commands.</summary>
		public string TargetName { get; }

		/// <summary>Path on disk where the .vcxproj will be written.</summary>
		public FileReference ProjectFilePath { get; }

		/// <summary>Path to .Target.cs for this target.</summary>
		public FileReference TargetFile { get; }

		/// <summary>The TargetRules object instantiated from .Target.cs (so we know launch module + extras).</summary>
		public TargetRules TargetRules { get; }

		/// <summary>The list of modules included in this target. Each contributes .cpp + .h files plus .Build.cs.</summary>
		public IReadOnlyList<ModuleRules> Modules { get; }

		/// <summary>Stable GUID for the project. Random per session is fine because the .sln is fully regenerated each invocation.</summary>
		public Guid Guid { get; } = Guid.NewGuid();

		/// <summary>Construct a VCProject. Caller is responsible for populating Modules via RulesAssembly.</summary>
		/// <param name="targetName">Name of the target.</param>
		/// <param name="projectFilePath">Path where the .vcxproj will be written.</param>
		/// <param name="targetFile">.Target.cs location.</param>
		/// <param name="targetRules">Instantiated TargetRules subclass.</param>
		/// <param name="modules">Modules that contribute source files.</param>
		public VCProject(string targetName, FileReference projectFilePath, FileReference targetFile, TargetRules targetRules, IReadOnlyList<ModuleRules> modules)
		{
			ArgumentNullException.ThrowIfNull(targetName);
			ArgumentNullException.ThrowIfNull(projectFilePath);
			ArgumentNullException.ThrowIfNull(targetFile);
			ArgumentNullException.ThrowIfNull(targetRules);
			ArgumentNullException.ThrowIfNull(modules);

			TargetName = targetName;
			ProjectFilePath = projectFilePath;
			TargetFile = targetFile;
			TargetRules = targetRules;
			Modules = modules;
		}

		/// <summary>
		/// Generate the .vcxproj (and a sibling .vcxproj.filters) on disk.
		/// </summary>
		/// <param name="engineDir">Engine root (used for NMakeOutput paths).</param>
		/// <param name="callBuildToolBat">Absolute path to CallBuildTool.bat (delegate target for NMake).</param>
		/// <returns>The list of files written.</returns>
		public IReadOnlyList<FileReference> Write(DirectoryReference engineDir, FileReference callBuildToolBat)
		{
			ArgumentNullException.ThrowIfNull(engineDir);
			ArgumentNullException.ThrowIfNull(callBuildToolBat);

			// Make sure the output directory exists.
			Directory.CreateDirectory(ProjectFilePath.Directory.FullName);

			// Collect source/header/build files from each module's directory.
			List<FileReference> cppFiles = new();
			List<FileReference> headerFiles = new();
			List<FileReference> noneFiles = new() { TargetFile };
			HashSet<DirectoryReference> includePaths = new();

			foreach (ModuleRules module in Modules)
			{
				if (module.File is not null)
				{
					noneFiles.Add(module.File);
				}
				if (module.Directory is null)
				{
					continue;
				}
				includePaths.Add(module.Directory);

				if (Directory.Exists(module.Directory.FullName))
				{
					foreach (string path in Directory.EnumerateFiles(module.Directory.FullName, "*.cpp", SearchOption.AllDirectories))
					{
						cppFiles.Add(new FileReference(path));
					}
					foreach (string path in Directory.EnumerateFiles(module.Directory.FullName, "*.h", SearchOption.AllDirectories))
					{
						headerFiles.Add(new FileReference(path));
					}
					foreach (string path in Directory.EnumerateFiles(module.Directory.FullName, "*.inl", SearchOption.AllDirectories))
					{
						headerFiles.Add(new FileReference(path));
					}
				}
			}

			// Build up the include-path string used for IntelliSense.
			StringBuilder includeBuilder = new();
			foreach (DirectoryReference d in includePaths)
			{
				includeBuilder.Append(d.FullName).Append(';');
			}
			// Always allow XCorePCH location for IntelliSense.
			DirectoryReference runtimeDir = DirectoryReference.Combine(engineDir, "Source", "Runtime");
			if (Directory.Exists(runtimeDir.FullName))
			{
				includeBuilder.Append(runtimeDir.FullName).Append(';');
			}

			string includeSearchPath = includeBuilder.ToString();

			// Build up a preprocessor-defs string from the merged module defs (used only for IntelliSense).
			StringBuilder defsBuilder = new();
			foreach (ModuleRules module in Modules)
			{
				foreach (string d in module.PublicDefinitions)
				{
					defsBuilder.Append(d).Append(';');
				}
				foreach (string d in module.PrivateDefinitions)
				{
					defsBuilder.Append(d).Append(';');
				}
			}
			string preprocessorDefs = defsBuilder.ToString();

			// Compose the XML.
			StringBuilder sb = new();
			sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
			sb.Append("<Project DefaultTargets=\"Build\" ToolsVersion=\"")
				.Append(MSBuildIdentifiers.CppProjectToolsVersion)
				.AppendLine("\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");

			// ProjectConfigurations item group.
			sb.AppendLine("  <ItemGroup Label=\"ProjectConfigurations\">");
			foreach (string cfg in s_supportedConfigurations)
			{
				sb.Append("    <ProjectConfiguration Include=\"").Append(cfg).AppendLine("|x64\">");
				sb.Append("      <Configuration>").Append(cfg).AppendLine("</Configuration>");
				sb.AppendLine("      <Platform>x64</Platform>");
				sb.AppendLine("    </ProjectConfiguration>");
			}
			sb.AppendLine("  </ItemGroup>");

			// Globals: project GUID + Makefile keyword.
			sb.AppendLine("  <PropertyGroup Label=\"Globals\">");
			sb.Append("    <ProjectGuid>").Append(GuidString).AppendLine("</ProjectGuid>");
			sb.Append("    <RootNamespace>").Append(TargetName).AppendLine("</RootNamespace>");
			sb.AppendLine("    <Keyword>MakeFileProj</Keyword>");
			sb.AppendLine("    <MinimumVisualStudioVersion>17.0</MinimumVisualStudioVersion>");
			sb.AppendLine("    <WindowsTargetPlatformVersion>10.0</WindowsTargetPlatformVersion>");
			sb.AppendLine("  </PropertyGroup>");

			sb.AppendLine("  <Import Project=\"$(VCTargetsPath)\\Microsoft.Cpp.Default.props\" />");

			// One <PropertyGroup Label="Configuration"> per config (sets NMake project type + toolset).
			foreach (string cfg in s_supportedConfigurations)
			{
				sb.Append("  <PropertyGroup Condition=\"'$(Configuration)|$(Platform)'=='")
					.Append(cfg).Append("|x64'\" Label=\"Configuration\">");
				sb.AppendLine();
				sb.AppendLine("    <ConfigurationType>Makefile</ConfigurationType>");
				sb.Append("    <PlatformToolset>").Append(MSBuildIdentifiers.PlatformToolsetVS2022).AppendLine("</PlatformToolset>");
				sb.AppendLine("    <UseDebugLibraries>false</UseDebugLibraries>");
				sb.AppendLine("  </PropertyGroup>");
			}

			sb.AppendLine("  <Import Project=\"$(VCTargetsPath)\\Microsoft.Cpp.props\" />");
			sb.AppendLine("  <ImportGroup Label=\"ExtensionSettings\" />");
			sb.AppendLine("  <PropertyGroup Label=\"UserMacros\" />");

			// One NMake PropertyGroup per (Configuration, x64) writing the build/rebuild/clean lines and IntelliSense state.
			string callBuildToolPath = callBuildToolBat.FullName;
			DirectoryReference binariesDir = DirectoryReference.Combine(engineDir, "Binaries", "Win64", TargetName);
			foreach (string cfg in s_supportedConfigurations)
			{
				string xbtConfig = ToXBTConfig(cfg);
				FileReference outputExe = FileReference.Combine(binariesDir, xbtConfig, TargetName + ".exe");
				sb.Append("  <PropertyGroup Condition=\"'$(Configuration)|$(Platform)'=='")
					.Append(cfg).AppendLine("|x64'\">");
				sb.Append("    <NMakeBuildCommandLine>\"").Append(callBuildToolPath).Append("\" -build -target=").Append(TargetName)
					.Append(" -platform=Windows -config=").Append(xbtConfig).AppendLine(" -arch=x64</NMakeBuildCommandLine>");
				sb.Append("    <NMakeReBuildCommandLine>\"").Append(callBuildToolPath).Append("\" -build -target=").Append(TargetName)
					.Append(" -platform=Windows -config=").Append(xbtConfig).AppendLine(" -arch=x64</NMakeReBuildCommandLine>");
				sb.Append("    <NMakeCleanCommandLine>@rem XBT clean not implemented in Phase 0.</NMakeCleanCommandLine>").AppendLine();
				sb.Append("    <NMakeOutput>").Append(outputExe.FullName).AppendLine("</NMakeOutput>");
				if (includeSearchPath.Length > 0)
				{
					sb.Append("    <NMakeIncludeSearchPath>").Append(EscapeXml(includeSearchPath)).AppendLine("</NMakeIncludeSearchPath>");
				}
				if (preprocessorDefs.Length > 0)
				{
					sb.Append("    <NMakePreprocessorDefinitions>").Append(EscapeXml(preprocessorDefs)).AppendLine("</NMakePreprocessorDefinitions>");
				}
				// Match the C++ standard used by the CL.exe args XBT actually emits (see XBTWindows.MakeClCompileArgs).
				sb.AppendLine("    <AdditionalOptions>/std:c++20 %(AdditionalOptions)</AdditionalOptions>");
				sb.AppendLine("  </PropertyGroup>");
			}

			// ItemDefinitionGroup is required by Microsoft.Cpp.targets but stays minimal for Makefile projects.
			sb.AppendLine("  <ItemDefinitionGroup />");

			// Source / header / none groups.
			if (cppFiles.Count > 0)
			{
				sb.AppendLine("  <ItemGroup>");
				foreach (FileReference f in cppFiles.OrderBy(c => c.FullName, StringComparer.OrdinalIgnoreCase))
				{
					sb.Append("    <ClCompile Include=\"").Append(EscapeXml(f.FullName)).AppendLine("\" />");
				}
				sb.AppendLine("  </ItemGroup>");
			}
			if (headerFiles.Count > 0)
			{
				sb.AppendLine("  <ItemGroup>");
				foreach (FileReference f in headerFiles.OrderBy(c => c.FullName, StringComparer.OrdinalIgnoreCase))
				{
					sb.Append("    <ClInclude Include=\"").Append(EscapeXml(f.FullName)).AppendLine("\" />");
				}
				sb.AppendLine("  </ItemGroup>");
			}
			if (noneFiles.Count > 0)
			{
				sb.AppendLine("  <ItemGroup>");
				foreach (FileReference f in noneFiles.OrderBy(c => c.FullName, StringComparer.OrdinalIgnoreCase))
				{
					sb.Append("    <None Include=\"").Append(EscapeXml(f.FullName)).AppendLine("\" />");
				}
				sb.AppendLine("  </ItemGroup>");
			}

			sb.AppendLine("  <Import Project=\"$(VCTargetsPath)\\Microsoft.Cpp.targets\" />");
			sb.AppendLine("  <ImportGroup Label=\"ExtensionTargets\" />");
			sb.AppendLine("</Project>");

			File.WriteAllText(ProjectFilePath.FullName, sb.ToString(), Encoding.UTF8);

			// Write the matching .vcxproj.filters so Solution Explorer mirrors the on-disk tree.
			FileReference filtersPath = new(ProjectFilePath.FullName + ".filters");
			WriteFiltersFile(filtersPath, cppFiles, headerFiles, noneFiles);

			return new[] { ProjectFilePath, filtersPath };
		}

		private static void WriteFiltersFile(FileReference path, IEnumerable<FileReference> cpp, IEnumerable<FileReference> h, IEnumerable<FileReference> none)
		{
			// We use three top-level filters (Source / Header / None). The Phase 0 set of files is small enough that a
			// per-target single-level filter is plenty; UE 5.9 walks parent directories to build nested filters, which we
			// will revisit when targets grow beyond a single source dir.
			StringBuilder sb = new();
			sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
			sb.Append("<Project ToolsVersion=\"")
				.Append(MSBuildIdentifiers.CppProjectToolsVersion)
				.AppendLine("\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");

			sb.AppendLine("  <ItemGroup>");
			sb.Append("    <Filter Include=\"Source Files\"><UniqueIdentifier>").Append(Guid.NewGuid().ToString("B").ToUpperInvariant()).AppendLine("</UniqueIdentifier></Filter>");
			sb.Append("    <Filter Include=\"Header Files\"><UniqueIdentifier>").Append(Guid.NewGuid().ToString("B").ToUpperInvariant()).AppendLine("</UniqueIdentifier></Filter>");
			sb.Append("    <Filter Include=\"Rules\"><UniqueIdentifier>").Append(Guid.NewGuid().ToString("B").ToUpperInvariant()).AppendLine("</UniqueIdentifier></Filter>");
			sb.AppendLine("  </ItemGroup>");

			sb.AppendLine("  <ItemGroup>");
			foreach (FileReference f in cpp)
			{
				sb.Append("    <ClCompile Include=\"").Append(EscapeXml(f.FullName)).AppendLine("\"><Filter>Source Files</Filter></ClCompile>");
			}
			sb.AppendLine("  </ItemGroup>");

			sb.AppendLine("  <ItemGroup>");
			foreach (FileReference f in h)
			{
				sb.Append("    <ClInclude Include=\"").Append(EscapeXml(f.FullName)).AppendLine("\"><Filter>Header Files</Filter></ClInclude>");
			}
			sb.AppendLine("  </ItemGroup>");

			sb.AppendLine("  <ItemGroup>");
			foreach (FileReference f in none)
			{
				sb.Append("    <None Include=\"").Append(EscapeXml(f.FullName)).AppendLine("\"><Filter>Rules</Filter></None>");
			}
			sb.AppendLine("  </ItemGroup>");

			sb.AppendLine("</Project>");
			File.WriteAllText(path.FullName, sb.ToString(), Encoding.UTF8);
		}

		/// <summary>String representation of this project's GUID in MSBuild form (uppercased, surrounded by braces).</summary>
		public string GuidString => Guid.ToString("B").ToUpperInvariant();

		private static string EscapeXml(string value)
		{
			return value
				.Replace("&", "&amp;", StringComparison.Ordinal)
				.Replace("<", "&lt;", StringComparison.Ordinal)
				.Replace(">", "&gt;", StringComparison.Ordinal)
				.Replace("\"", "&quot;", StringComparison.Ordinal);
		}
	}
}
