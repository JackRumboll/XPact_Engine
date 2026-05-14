// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealBuildTool.PrecompiledHeaderInstance. Each instance owns
// the .pch file, the .obj file produced as a side-effect, and the compile
// environment that produced it (used by consumers to validate compatibility).

using XPact.Core.IO;

namespace XBT.BuildSystem
{
	/// <summary>
	/// A concrete PCH artefact: the .pch file, its companion .obj, and the
	/// CppCompileEnvironment that produced it.
	/// </summary>
	public sealed class PrecompiledHeaderInstance
	{
		/// <summary>The template this instance was produced from.</summary>
		public PrecompiledHeaderTemplate Template { get; }

		/// <summary>The compile environment that was used to produce this PCH.</summary>
		public CppCompileEnvironment CompileEnvironment { get; }

		/// <summary>The .pch file path.</summary>
		public FileReference PCHFile { get; }

		/// <summary>The .obj file path produced as a side-effect (must be linked into the final binary).</summary>
		public FileReference ObjectFile { get; }

		/// <summary>The .cpp stub used to drive PCH generation (the file passed to /Yc).</summary>
		public FileReference StubSourceFile { get; }

		/// <summary>Construct an instance.</summary>
		public PrecompiledHeaderInstance(PrecompiledHeaderTemplate template, CppCompileEnvironment compileEnvironment, FileReference pchFile, FileReference objectFile, FileReference stubSourceFile)
		{
			System.ArgumentNullException.ThrowIfNull(template);
			System.ArgumentNullException.ThrowIfNull(compileEnvironment);
			System.ArgumentNullException.ThrowIfNull(pchFile);
			System.ArgumentNullException.ThrowIfNull(objectFile);
			System.ArgumentNullException.ThrowIfNull(stubSourceFile);
			Template = template;
			CompileEnvironment = compileEnvironment;
			PCHFile = pchFile;
			ObjectFile = objectFile;
			StubSourceFile = stubSourceFile;
		}
	}
}
