// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealBuildTool's solution-writing logic (split across
// VCProjectFileGenerator.cs / VisualStudioSolutionBuilder.cs). The UE 5.9
// version handles configuration mapping for multiple target types (Game /
// Client / Server / Editor / Test), nested solution folders inferred from
// every project's filesystem location, and .vsconfig/.suo emission. XBT
// Task 0.4 only needs Win64 + {Debug, Development, Release} for the
// HelloWorld program plus the four C# .csproj references; one solution
// folder per top-level grouping is enough.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using XPact.Build;
using XPact.Core.IO;

namespace XBT.ProjectFiles.VisualStudio
{
	/// <summary>
	/// One Visual Studio solution. Builds .sln content for a list of C++ NMake projects + a list of C# project files.
	/// </summary>
	public sealed class VCSolution
	{
		/// <summary>Solution configurations we emit (each x64 only).</summary>
		public static readonly string[] SolutionConfigurations = { "Debug", "Development", "Release" };

		/// <summary>Solution platform (single).</summary>
		public const string SolutionPlatform = "x64";

		/// <summary>Path on disk where the .sln will be written.</summary>
		public FileReference SolutionFilePath { get; }

		/// <summary>C++ NMake projects to include.</summary>
		public IReadOnlyList<VCProject> CppProjects { get; }

		/// <summary>C# project file paths to include (loaded by VS as native csproj references).</summary>
		public IReadOnlyList<FileReference> CSharpProjects { get; }

		/// <summary>Construct a VCSolution.</summary>
		public VCSolution(FileReference solutionFilePath, IReadOnlyList<VCProject> cppProjects, IReadOnlyList<FileReference> cSharpProjects)
		{
			ArgumentNullException.ThrowIfNull(solutionFilePath);
			ArgumentNullException.ThrowIfNull(cppProjects);
			ArgumentNullException.ThrowIfNull(cSharpProjects);

			SolutionFilePath = solutionFilePath;
			CppProjects = cppProjects;
			CSharpProjects = cSharpProjects;
		}

		/// <summary>
		/// Write the solution file.
		/// </summary>
		public FileReference Write()
		{
			Directory.CreateDirectory(SolutionFilePath.Directory.FullName);

			StringBuilder sb = new();

			// Header lines. Exact whitespace and CRLF formatting matters less than getting the keywords in order; VS is tolerant.
			sb.AppendLine();
			sb.Append("Microsoft Visual Studio Solution File, Format Version ").AppendLine(MSBuildIdentifiers.SolutionFormatVersion);
			sb.Append("# Visual Studio Version ").AppendLine(MSBuildIdentifiers.VisualStudioVersionLine);
			sb.Append("VisualStudioVersion = ").AppendLine(MSBuildIdentifiers.VisualStudioVersionDetailed);
			sb.Append("MinimumVisualStudioVersion = ").AppendLine(MSBuildIdentifiers.MinimumVisualStudioVersion);

			// Solution folders: Engine/Source/Programs (for the C# projects), Engine/Source/Programs.Targets (for C++ programs), Engine/Documentation (placeholder).
			Guid programsFolderGuid = Guid.NewGuid();
			Guid programsTargetsFolderGuid = Guid.NewGuid();
			Guid documentationFolderGuid = Guid.NewGuid();

			sb.AppendFormat(
				CultureInfo.InvariantCulture,
				"Project(\"{0}\") = \"Engine.Source.Programs\", \"Engine.Source.Programs\", \"{1}\"\r\n",
				MSBuildIdentifiers.SolutionFolderTypeGuid,
				programsFolderGuid.ToString("B").ToUpperInvariant());
			sb.AppendLine("EndProject");

			sb.AppendFormat(
				CultureInfo.InvariantCulture,
				"Project(\"{0}\") = \"Engine.Source.Programs.Targets\", \"Engine.Source.Programs.Targets\", \"{1}\"\r\n",
				MSBuildIdentifiers.SolutionFolderTypeGuid,
				programsTargetsFolderGuid.ToString("B").ToUpperInvariant());
			sb.AppendLine("EndProject");

			sb.AppendFormat(
				CultureInfo.InvariantCulture,
				"Project(\"{0}\") = \"Engine.Documentation\", \"Engine.Documentation\", \"{1}\"\r\n",
				MSBuildIdentifiers.SolutionFolderTypeGuid,
				documentationFolderGuid.ToString("B").ToUpperInvariant());
			sb.AppendLine("EndProject");

			// C++ NMake projects (vcxproj).
			foreach (VCProject cpp in CppProjects)
			{
				string relPath = MakeRelative(SolutionFilePath.Directory, cpp.ProjectFilePath);
				sb.AppendFormat(
					CultureInfo.InvariantCulture,
					"Project(\"{0}\") = \"{1}\", \"{2}\", \"{3}\"\r\n",
					MSBuildIdentifiers.CppProjectTypeGuid,
					cpp.TargetName,
					relPath,
					cpp.GuidString);
				sb.AppendLine("EndProject");
			}

			// C# projects (csproj). Stable per-path GUID so a given csproj keeps the same identity across regenerations.
			Dictionary<FileReference, Guid> csharpGuids = new();
			foreach (FileReference csproj in CSharpProjects)
			{
				Guid g = StableGuidForPath(csproj.FullName);
				csharpGuids[csproj] = g;
				string relPath = MakeRelative(SolutionFilePath.Directory, csproj);
				string name = csproj.GetFileNameWithoutExtension();
				sb.AppendFormat(
					CultureInfo.InvariantCulture,
					"Project(\"{0}\") = \"{1}\", \"{2}\", \"{3}\"\r\n",
					MSBuildIdentifiers.LegacyCSharpProjectTypeGuid,
					name,
					relPath,
					g.ToString("B").ToUpperInvariant());
				sb.AppendLine("EndProject");
			}

			// Global section.
			sb.AppendLine("Global");

			// SolutionConfigurationPlatforms (Configuration|Platform list shown in the VS dropdown).
			sb.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
			foreach (string cfg in SolutionConfigurations)
			{
				sb.AppendFormat(CultureInfo.InvariantCulture, "\t\t{0}|{1} = {0}|{1}\r\n", cfg, SolutionPlatform);
			}
			sb.AppendLine("\tEndGlobalSection");

			// ProjectConfigurationPlatforms: each project gets ActiveCfg + Build.0 lines for every solution config.
			sb.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");

			// C++ projects map 1:1 (solution Debug|x64 -> project Debug|x64, etc.).
			foreach (VCProject cpp in CppProjects)
			{
				foreach (string solCfg in SolutionConfigurations)
				{
					string projectConfig = MapSolutionConfigToCppProjectConfig(solCfg);
					sb.AppendFormat(
						CultureInfo.InvariantCulture,
						"\t\t{0}.{1}|{2}.ActiveCfg = {3}|{2}\r\n",
						cpp.GuidString, solCfg, SolutionPlatform, projectConfig);
					sb.AppendFormat(
						CultureInfo.InvariantCulture,
						"\t\t{0}.{1}|{2}.Build.0 = {3}|{2}\r\n",
						cpp.GuidString, solCfg, SolutionPlatform, projectConfig);
				}
			}

			// C# projects: SDK-style csproj configurations are Debug / Release with AnyCPU platform; map "Development" -> "Release" so all three solution configs build the csproj.
			foreach ((FileReference csproj, Guid g) in csharpGuids)
			{
				string guidStr = g.ToString("B").ToUpperInvariant();
				foreach (string solCfg in SolutionConfigurations)
				{
					string projectConfig = MapSolutionConfigToCSharpProjectConfig(solCfg);
					sb.AppendFormat(
						CultureInfo.InvariantCulture,
						"\t\t{0}.{1}|{2}.ActiveCfg = {3}|Any CPU\r\n",
						guidStr, solCfg, SolutionPlatform, projectConfig);
					sb.AppendFormat(
						CultureInfo.InvariantCulture,
						"\t\t{0}.{1}|{2}.Build.0 = {3}|Any CPU\r\n",
						guidStr, solCfg, SolutionPlatform, projectConfig);
				}
				_ = csproj;
			}

			sb.AppendLine("\tEndGlobalSection");

			sb.AppendLine("\tGlobalSection(SolutionProperties) = preSolution");
			sb.AppendLine("\t\tHideSolutionNode = FALSE");
			sb.AppendLine("\tEndGlobalSection");

			// NestedProjects: place every emitted project under the right top-level folder so Solution Explorer mirrors the source tree.
			sb.AppendLine("\tGlobalSection(NestedProjects) = preSolution");
			string targetsFolderGuidStr = programsTargetsFolderGuid.ToString("B").ToUpperInvariant();
			foreach (VCProject cpp in CppProjects)
			{
				sb.AppendFormat(CultureInfo.InvariantCulture, "\t\t{0} = {1}\r\n", cpp.GuidString, targetsFolderGuidStr);
			}
			string programsFolderGuidStr = programsFolderGuid.ToString("B").ToUpperInvariant();
			foreach ((FileReference csproj, Guid g) in csharpGuids)
			{
				sb.AppendFormat(CultureInfo.InvariantCulture, "\t\t{0} = {1}\r\n",
					g.ToString("B").ToUpperInvariant(),
					programsFolderGuidStr);
				_ = csproj;
			}
			sb.AppendLine("\tEndGlobalSection");

			sb.AppendLine("\tGlobalSection(ExtensibilityGlobals) = postSolution");
			sb.AppendFormat(CultureInfo.InvariantCulture, "\t\tSolutionGuid = {0}\r\n", Guid.NewGuid().ToString("B").ToUpperInvariant());
			sb.AppendLine("\tEndGlobalSection");

			sb.AppendLine("EndGlobal");

			File.WriteAllText(SolutionFilePath.FullName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

			return SolutionFilePath;
		}

		/// <summary>Map a solution-level Configuration to the equivalent vcxproj Configuration we emit. Identity for Phase 0 since VCProject emits the same names as the solution.</summary>
		private static string MapSolutionConfigToCppProjectConfig(string solCfg) => solCfg;

		/// <summary>Map a solution-level Configuration to the SDK-style csproj Configuration (only Debug/Release exist out of the box).</summary>
		private static string MapSolutionConfigToCSharpProjectConfig(string solCfg)
		{
			return solCfg switch
			{
				"Debug" => "Debug",
				_ => "Release",
			};
		}

		/// <summary>Convert an absolute path to a path relative to the given base directory.</summary>
		private static string MakeRelative(DirectoryReference baseDir, FileReference target)
		{
			try
			{
				return Path.GetRelativePath(baseDir.FullName, target.FullName);
			}
			catch (ArgumentException)
			{
				return target.FullName;
			}
		}

		/// <summary>Deterministic GUID from a file path so the same csproj keeps the same identity across regenerations.</summary>
		private static Guid StableGuidForPath(string path)
		{
			// Use the SHA-1 of the lower-cased absolute path as the GUID body; this matches the determinism users expect from regenerated solutions.
			byte[] bytes = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
			// Take the first 16 bytes for the Guid.
			byte[] guidBytes = new byte[16];
			Array.Copy(bytes, guidBytes, 16);
			return new Guid(guidBytes);
		}
	}
}
