// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// MSBuild / Visual Studio project type GUIDs. These are the well-known constants
// MSBuild uses to identify project flavors in a .sln file. They are fixed by the
// MSBuild contract and identical across every Visual Studio version we target.
// See: https://learn.microsoft.com/en-us/visualstudio/extensibility/internals/project-types
//
// The same GUIDs appear verbatim in Unreal Engine 5.9's
// UnrealBuildTool/UnrealBuildTool.sln; XBT keeps them in one place so VCProject /
// VCSolution can share them.

namespace XBT.ProjectFiles.VisualStudio
{
	/// <summary>
	/// Well-known MSBuild project-type GUIDs used in .sln files.
	/// </summary>
	public static class MSBuildIdentifiers
	{
		/// <summary>Project type GUID for C++ (.vcxproj) projects.</summary>
		public const string CppProjectTypeGuid = "{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}";

		/// <summary>Project type GUID for legacy C# (.csproj) projects (i.e. csproj files that don't use the SDK style attribute).</summary>
		public const string LegacyCSharpProjectTypeGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";

		/// <summary>Project type GUID for SDK-style C# (.csproj) projects. Visual Studio recognises both this and the legacy GUID; we use this for our SDK-style projects.</summary>
		public const string CSharpSdkProjectTypeGuid = "{9A19103F-16F7-4668-BE54-9A1E7A4F7556}";

		/// <summary>Project type GUID for solution folders (logical grouping nodes with no on-disk file).</summary>
		public const string SolutionFolderTypeGuid = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";

		/// <summary>The solution file format version we emit. This has not changed since Visual Studio 2012.</summary>
		public const string SolutionFormatVersion = "12.00";

		/// <summary>The "Visual Studio Version" line emitted into the .sln header (used by VS to pick the default toolset).</summary>
		public const string VisualStudioVersionLine = "17";

		/// <summary>The detailed VisualStudioVersion value emitted under the header.</summary>
		public const string VisualStudioVersionDetailed = "17.0.31903.59";

		/// <summary>The minimum-VS version line, kept at the Visual Studio 2010 sentinel value.</summary>
		public const string MinimumVisualStudioVersion = "10.0.40219.1";

		/// <summary>The Cpp tools version string embedded in vcxproj XML headers (matches VS 2022 / 2026).</summary>
		public const string CppProjectToolsVersion = "17.0";

		/// <summary>The PlatformToolset entry for VS 2022.</summary>
		public const string PlatformToolsetVS2022 = "v143";
	}
}
