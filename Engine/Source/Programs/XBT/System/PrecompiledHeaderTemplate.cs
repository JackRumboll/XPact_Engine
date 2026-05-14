// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealBuildTool.PrecompiledHeaderTemplate. A template represents
// the *recipe* for creating a shared PCH: the header file, the module that
// owns it, and the base compile environment to clone from. UE 5.9 also tracks
// rules-scope visibility and dependent-module lists; XBT Task 0.2 just stores
// the header reference and a flat list of instances created from this template.

using System.Collections.Generic;
using XPact.Core.IO;

namespace XBT.BuildSystem
{
	/// <summary>
	/// Recipe for producing a shared PCH. A single template can spawn multiple
	/// PrecompiledHeaderInstances if different consumer environments require
	/// different definitions.
	/// </summary>
	public sealed class PrecompiledHeaderTemplate
	{
		/// <summary>Name of the module that owns this shared PCH.</summary>
		public string OwningModuleName { get; }

		/// <summary>Path to the PCH header file (e.g. XCorePCH.Stub.h).</summary>
		public FileReference HeaderFile { get; }

		/// <summary>Base compile environment for instances of this PCH.</summary>
		public CppCompileEnvironment BaseCompileEnvironment { get; }

		/// <summary>Instances already created from this template.</summary>
		public List<PrecompiledHeaderInstance> Instances { get; } = [];

		/// <summary>Construct a template.</summary>
		public PrecompiledHeaderTemplate(string owningModuleName, FileReference headerFile, CppCompileEnvironment baseEnv)
		{
			System.ArgumentNullException.ThrowIfNull(owningModuleName);
			System.ArgumentNullException.ThrowIfNull(headerFile);
			System.ArgumentNullException.ThrowIfNull(baseEnv);
			OwningModuleName = owningModuleName;
			HeaderFile = headerFile;
			BaseCompileEnvironment = baseEnv;
		}
	}
}
