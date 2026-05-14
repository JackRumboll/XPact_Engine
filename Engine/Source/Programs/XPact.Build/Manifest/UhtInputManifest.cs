// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// xht-input-manifest.json POCO (XBT -> XHT direction; decision #5, two-manifest discipline).
// This file is the single source of truth for the schema; both XBT (writer) and XHT
// (reader) reference it through their XPact.Build project dependency so the
// serialized shape cannot drift. The matching MANIFEST_SCHEMA.md document is at
// Engine/Documentation/MANIFEST_SCHEMA.md.
//
// Phase 0 keeps the schema minimal: targetName/targetType/platform/configuration
// plus per-module {name, modulePath, outputDir, publicHeaders, privateHeaders,
// moduleVersion}. Phase 1 Task 1.9 may extend with include-paths, public-defines,
// generated-CPP filename base, etc. -- BUMP schemaVersion on incompatible changes
// and reject older versions explicitly (mirrors decision in
// MANIFEST_SCHEMA.md "Schema Versioning").

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace XPact.Build.Manifest
{
	/// <summary>
	/// Top-level xht-input-manifest.json shape: target identity + the list of
	/// modules XHT should scan.
	/// </summary>
	public sealed class UhtInputManifest
	{
		/// <summary>The currently-recognised schema version. Bumped on incompatible changes.</summary>
		public const int CurrentSchemaVersion = 1;

		/// <summary>Schema version of this manifest file.</summary>
		[JsonPropertyName("schemaVersion")]
		public int SchemaVersion { get; set; } = CurrentSchemaVersion;

		/// <summary>Logical target name (e.g. "HelloWorld").</summary>
		[JsonPropertyName("targetName")]
		public string TargetName { get; set; } = string.Empty;

		/// <summary>Target type string (e.g. "Program", "Game", "Editor"). Mirrors XBT.Configuration.Rules.TargetType.</summary>
		[JsonPropertyName("targetType")]
		public string TargetType { get; set; } = string.Empty;

		/// <summary>Platform folder name as used on disk (e.g. "Win64").</summary>
		[JsonPropertyName("platform")]
		public string Platform { get; set; } = string.Empty;

		/// <summary>Configuration name (e.g. "Development").</summary>
		[JsonPropertyName("configuration")]
		public string Configuration { get; set; } = string.Empty;

		/// <summary>Per-module entries XHT will process.</summary>
		[JsonPropertyName("modules")]
		public List<UhtInputManifestModule> Modules { get; set; } = new();
	}

	/// <summary>
	/// One module entry inside a UhtInputManifest. Header paths are recorded
	/// engine-root-relative with forward-slash separators (cross-platform friendly,
	/// easy to diff, easy to hash). XHT resolves them against the writer's engine
	/// root when reading.
	/// </summary>
	public sealed class UhtInputManifestModule
	{
		/// <summary>Module name (matches the .Build.cs class name).</summary>
		[JsonPropertyName("name")]
		public string Name { get; set; } = string.Empty;

		/// <summary>Path to the module directory, relative to engine root, forward-slash separators.</summary>
		[JsonPropertyName("modulePath")]
		public string ModulePath { get; set; } = string.Empty;

		/// <summary>Per-module Generated/ directory, relative to engine root.</summary>
		[JsonPropertyName("outputDir")]
		public string OutputDir { get; set; } = string.Empty;

		/// <summary>Public headers the module exposes (relative to modulePath, forward-slash).</summary>
		[JsonPropertyName("publicHeaders")]
		public List<string> PublicHeaders { get; set; } = new();

		/// <summary>Private headers / source files (relative to modulePath, forward-slash).</summary>
		[JsonPropertyName("privateHeaders")]
		public List<string> PrivateHeaders { get; set; } = new();

		/// <summary>Module-supplied version string. Phase 0 uses the engine version (e.g. "5.9.0").</summary>
		[JsonPropertyName("moduleVersion")]
		public string ModuleVersion { get; set; } = string.Empty;
	}
}
