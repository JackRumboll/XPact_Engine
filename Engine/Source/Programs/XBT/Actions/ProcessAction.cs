// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Helper base class providing a default ExecuteAsync that runs the configured
// CommandPath with CommandArguments and captures stdout/stderr. Compile / Link
// / PCH actions inherit from this; only specialised actions need to override.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XPact.Build;

namespace XBT.Actions
{
	/// <summary>
	/// Action whose work is "run the configured external process".
	/// </summary>
	public abstract class ProcessAction : Action
	{
		/// <summary>
		/// Environment variables to set on the child process. The cl.exe / link.exe
		/// toolchain reads INCLUDE / LIB from the environment.
		/// </summary>
		public Dictionary<string, string> EnvironmentVariables { get; } = new(StringComparer.OrdinalIgnoreCase);

		/// <inheritdoc/>
		public override async Task<ActionResult> ExecuteAsync(CancellationToken cancellationToken)
		{
			if (CommandPath is null)
			{
				throw new BuildException("Action '{0}' has no CommandPath set", StatusDescription);
			}

			ProcessStartInfo psi = new()
			{
				FileName = CommandPath.FullName,
				Arguments = CommandArguments,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
				WorkingDirectory = WorkingDirectory?.FullName ?? Environment.CurrentDirectory,
			};

			foreach (KeyValuePair<string, string> kv in EnvironmentVariables)
			{
				psi.EnvironmentVariables[kv.Key] = kv.Value;
			}

			using Process proc = new() { StartInfo = psi, EnableRaisingEvents = true };
			StringBuilder stdout = new();
			StringBuilder stderr = new();
			proc.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (stdout) { stdout.AppendLine(e.Data); } } };
			proc.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (stderr) { stderr.AppendLine(e.Data); } } };

			if (!proc.Start())
			{
				throw new BuildException("Failed to start '{0}'", CommandPath.FullName);
			}
			proc.BeginOutputReadLine();
			proc.BeginErrorReadLine();

			try
			{
				await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
				throw;
			}

			return new ActionResult
			{
				ExitCode = proc.ExitCode,
				StdOut = stdout.ToString(),
				StdErr = stderr.ToString(),
			};
		}
	}
}
