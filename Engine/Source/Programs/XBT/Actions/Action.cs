// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealBuildTool.Action and its IExternalAction surface. The UE
// 5.9 file has ~30 fields driving the various distributed-build executors
// (XGE, FASTBuild, SN-DBS, UBA) plus artifact caching for incremental builds.
// XBT Task 0.2 only runs locally with ParallelExecutor and always does a clean
// compile, so we keep:
//   * Prerequisites + ProducedItems (DAG edges)
//   * CommandPath / CommandArguments / WorkingDirectory (the process to run)
//   * StatusDescription / ActionType / CommandDescription (logging)
// Plus an ExecuteAsync hook so CompileAction / LinkAction / PCHGenerateAction
// can run themselves uniformly via ParallelExecutor.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using XPact.Core.IO;

namespace XBT.Actions
{
	/// <summary>
	/// Coarse type tag for an action. Mirrors UnrealBuildTool.ActionType.
	/// </summary>
	public enum ActionType
	{
		/// <summary>Compile a single .cpp -> .obj.</summary>
		Compile,

		/// <summary>Generate a PCH (.pch + .obj produced together).</summary>
		GeneratePCH,

		/// <summary>Link .obj files into the final binary.</summary>
		Link,

		/// <summary>Generic action invoked for pre/post-build steps.</summary>
		Generic,
	}

	/// <summary>
	/// Base class for any external command the build needs to invoke.
	/// </summary>
	[SuppressMessage("Performance", "CA1002:Do not expose generic lists", Justification = "Matches UE5.9 IExternalAction surface")]
	public abstract class Action
	{
		/// <summary>The type of action.</summary>
		public ActionType ActionType { get; protected set; }

		/// <summary>Every file this action depends on. The action will not run until all prerequisites exist.</summary>
		public List<FileReference> Prerequisites { get; } = [];

		/// <summary>The files this action produces.</summary>
		public List<FileReference> ProducedItems { get; } = [];

		/// <summary>The command executable to invoke.</summary>
		public FileReference? CommandPath { get; set; }

		/// <summary>Command-line arguments for the command.</summary>
		public string CommandArguments { get; set; } = String.Empty;

		/// <summary>Directory to launch the command from.</summary>
		public DirectoryReference? WorkingDirectory { get; set; }

		/// <summary>Human-readable description (e.g. "Compile HelloWorld.cpp").</summary>
		public string StatusDescription { get; set; } = String.Empty;

		/// <summary>Description of the command class (e.g. "Compile", "Link").</summary>
		public string CommandDescription { get; set; } = String.Empty;

		/// <summary>Whether the executor should output the status description as it runs.</summary>
		public bool bShouldOutputStatusDescription { get; set; } = true;

		/// <summary>
		/// Execute this action. The default implementation spawns the configured
		/// process; derived actions override only if they have non-process work
		/// to perform (e.g. response-file generation).
		/// </summary>
		public abstract Task<ActionResult> ExecuteAsync(CancellationToken cancellationToken);
	}

	/// <summary>
	/// Result of running an action.
	/// </summary>
	public sealed class ActionResult
	{
		/// <summary>Process exit code. 0 means success.</summary>
		public int ExitCode { get; init; }

		/// <summary>Captured stdout.</summary>
		public string StdOut { get; init; } = String.Empty;

		/// <summary>Captured stderr.</summary>
		public string StdErr { get; init; } = String.Empty;

		/// <summary>True iff <see cref="ExitCode"/> is 0.</summary>
		public bool Success => ExitCode == 0;
	}
}
