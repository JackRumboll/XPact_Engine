// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// XHT (XPact Header Tool) - Phase 0 Task 0.3 skeleton.
//
// XHT for Phase 0 does NOT parse C++. It only:
//   1. Reads xht-input-manifest.json written by XBT.
//   2. Validates schemaVersion.
//   3. Logs which target / modules were resolved.
//   4. Exits 0.
//
// Phase 1 Task 1.9 will replace this skeleton with the real EpicGames.UHT-derived
// header parser + bindings.json emitter (Verse parsers stripped). The point of
// Task 0.3 is establishing the XBT -> XHT seam so the rest of the pipeline can
// plug in without surgery.
//
// The two-manifest discipline (decision #5) is enforced here: this binary
// strictly READS xht-input-manifest.json. It NEVER reads or writes bindings.json.
// That contract is for Phase 1.

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using XPact.Build;
using XPact.Build.Manifest;
using XPact.Core.Logging;

namespace XHT
{
	/// <summary>
	/// XHT entry point.
	/// </summary>
	[SupportedOSPlatform("windows")]
	[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Top-level driver converts everything to a non-zero exit code.")]
	public static class XHT
	{
		/// <summary>
		/// CLI entry point.
		/// </summary>
		/// <param name="args">Command-line args. Required: -manifest=&lt;path&gt; -target=&lt;name&gt;.</param>
		/// <returns>0 on success, non-zero on failure.</returns>
		public static int Main(string[] args)
		{
			ArgumentNullException.ThrowIfNull(args);
			Log.OutputLevel = LogEventType.Log;

			try
			{
				XHTOptions options = XHTOptions.Parse(args);
				return Run(options);
			}
			catch (BuildException ex)
			{
				Log.TraceError("[XHT] {0}", ex.Message);
				return 1;
			}
			catch (Exception ex)
			{
				Log.TraceError("[XHT] Unhandled exception: {0}", ex);
				return 1;
			}
		}

		private static int Run(XHTOptions options)
		{
			if (!File.Exists(options.ManifestPath))
			{
				Log.TraceError("[XHT] Input manifest not found: '{0}'", options.ManifestPath);
				return 1;
			}

			UhtInputManifest manifest;
			try
			{
				string json = File.ReadAllText(options.ManifestPath);
				JsonSerializerOptions opts = new()
				{
					PropertyNameCaseInsensitive = true,
					ReadCommentHandling = JsonCommentHandling.Skip,
					AllowTrailingCommas = true,
				};
				UhtInputManifest? parsed = JsonSerializer.Deserialize<UhtInputManifest>(json, opts);
				if (parsed is null)
				{
					Log.TraceError("[XHT] Manifest '{0}' deserialized to null.", options.ManifestPath);
					return 1;
				}
				manifest = parsed;
			}
			catch (JsonException jex)
			{
				Log.TraceError("[XHT] Failed to parse manifest '{0}': {1}", options.ManifestPath, jex.Message);
				return 1;
			}

			if (manifest.SchemaVersion != UhtInputManifest.CurrentSchemaVersion)
			{
				Log.TraceError("[XHT] Unsupported manifest schemaVersion {0} (expected {1})", manifest.SchemaVersion, UhtInputManifest.CurrentSchemaVersion);
				return 1;
			}

			// -target=<name> must match the manifest. A mismatch usually means stale
			// manifest, wrong path, or a CLI typo -- fail loud so the build doesn't
			// silently scan the wrong target's headers.
			if (!String.Equals(options.TargetName, manifest.TargetName, StringComparison.Ordinal))
			{
				Log.TraceError("[XHT] Target mismatch: CLI -target='{0}' but manifest targetName='{1}'", options.TargetName, manifest.TargetName);
				return 1;
			}

			Log.TraceInformation("[XHT] Loaded input manifest for target '{0}' ({1} modules).", manifest.TargetName, manifest.Modules.Count);

			foreach (UhtInputManifestModule module in manifest.Modules)
			{
				int publicCount = module.PublicHeaders.Count;
				int privateCount = module.PrivateHeaders.Count;
				Log.TraceInformation("[XHT] Module '{0}': {1} public headers, {2} private files (skeleton — no parsing in Phase 0).",
					module.Name, publicCount, privateCount);
			}

			return 0;
		}
	}

	/// <summary>
	/// Parsed XHT command-line options.
	/// </summary>
	internal sealed class XHTOptions
	{
		public string ManifestPath { get; init; } = String.Empty;
		public string TargetName { get; init; } = String.Empty;

		public static XHTOptions Parse(string[] args)
		{
			string? manifest = null;
			string? target = null;

			foreach (string raw in args)
			{
				string a = raw.TrimStart('-', '/');
				int eq = a.IndexOf('=', StringComparison.Ordinal);
				string key = eq < 0 ? a : a[..eq];
				string val = eq < 0 ? String.Empty : a[(eq + 1)..];
				switch (key.ToUpperInvariant())
				{
					case "MANIFEST":
						manifest = val;
						break;
					case "TARGET":
						target = val;
						break;
				}
			}

			if (String.IsNullOrEmpty(manifest))
			{
				throw new BuildException("Missing required argument: -manifest=<path-to-xht-input-manifest.json>");
			}
			if (String.IsNullOrEmpty(target))
			{
				throw new BuildException("Missing required argument: -target=<TargetName>");
			}
			return new XHTOptions { ManifestPath = manifest, TargetName = target };
		}
	}
}
