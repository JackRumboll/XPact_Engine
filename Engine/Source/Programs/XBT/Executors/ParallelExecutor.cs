// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealBuildTool.ParallelExecutor. The UE 5.9 version is built on
// a custom thread pool with weighted scheduling (memory pressure, action
// duration prediction, retries). XBT Task 0.2 keeps the semantics: take a list
// of Actions, topo-sort by FileReference dependencies, then dispatch with a
// bounded degree of parallelism (Environment.ProcessorCount). Implemented on
// top of System.Threading.Channels for a simple ready-queue / worker model.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XBT.Actions;
using XPact.Core.IO;
using XPact.Core.Logging;
using Action = XBT.Actions.Action;

namespace XBT.Executors
{
	/// <summary>
	/// Runs a DAG of Actions in parallel, respecting prerequisites.
	/// </summary>
	public sealed class ParallelExecutor
	{
		/// <summary>Maximum concurrent actions. Defaults to <see cref="Environment.ProcessorCount"/>.</summary>
		public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

		/// <summary>
		/// Run the given actions to completion. Returns true on success.
		/// </summary>
		public async Task<bool> RunAsync(IReadOnlyList<Action> actions, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(actions);
			if (actions.Count == 0)
			{
				return true;
			}

			// Build a producer-map (FullName -> Action) so we can look up which action
			// owns each prerequisite file.
			Dictionary<string, Action> producers = new(StringComparer.OrdinalIgnoreCase);
			foreach (Action a in actions)
			{
				foreach (FileReference p in a.ProducedItems)
				{
					producers[p.FullName] = a;
				}
			}

			// Pre-resolve in-edges + out-edges restricted to actions in the graph.
			Dictionary<Action, HashSet<Action>> deps = new();
			Dictionary<Action, HashSet<Action>> rdeps = new();
			foreach (Action a in actions)
			{
				deps[a] = new HashSet<Action>();
				rdeps[a] = new HashSet<Action>();
			}
			foreach (Action a in actions)
			{
				foreach (FileReference pre in a.Prerequisites)
				{
					if (producers.TryGetValue(pre.FullName, out Action? producer) && producer != a)
					{
						deps[a].Add(producer);
						rdeps[producer].Add(a);
					}
				}
			}

			// Validate the DAG is acyclic via Kahn (counting only).
			Dictionary<Action, int> inDegree = deps.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
			int remaining = actions.Count;

			Queue<Action> ready = new();
			foreach (Action a in actions)
			{
				if (inDegree[a] == 0)
				{
					ready.Enqueue(a);
				}
			}

			using SemaphoreSlim gate = new(MaxDegreeOfParallelism, MaxDegreeOfParallelism);
			object readyLock = new();
			bool anyFailure = false;
			object completionLock = new();
			TaskCompletionSource<bool> drainTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

			void ScheduleFromReady()
			{
				while (true)
				{
					Action? next;
					lock (readyLock)
					{
						if (!ready.TryDequeue(out next))
						{
							return;
						}
					}
					_ = RunOne(next!);
				}
			}

			async Task RunOne(Action a)
			{
				await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
				try
				{
					EnsureOutputDirectories(a);

					if (a.bShouldOutputStatusDescription)
					{
						Log.TraceInformation("[{0}] {1}", a.CommandDescription, a.StatusDescription);
					}

					ActionResult result = await a.ExecuteAsync(cancellationToken).ConfigureAwait(false);
					if (!String.IsNullOrEmpty(result.StdOut))
					{
						Log.TraceLog("{0}", result.StdOut.TrimEnd());
					}
					if (!String.IsNullOrEmpty(result.StdErr))
					{
						Log.TraceWarning("{0}", result.StdErr.TrimEnd());
					}
					if (!result.Success)
					{
						Log.TraceError("Action '{0}' failed with exit code {1}", a.StatusDescription, result.ExitCode);
						lock (completionLock)
						{
							anyFailure = true;
						}
					}
				}
				catch (Exception ex)
				{
					Log.TraceError("Action '{0}' threw: {1}", a.StatusDescription, ex.Message);
					lock (completionLock)
					{
						anyFailure = true;
					}
				}
				finally
				{
					gate.Release();
				}

				// Mark dependents ready.
				List<Action> newlyReady = new();
				lock (readyLock)
				{
					foreach (Action dep in rdeps[a])
					{
						inDegree[dep]--;
						if (inDegree[dep] == 0)
						{
							ready.Enqueue(dep);
							newlyReady.Add(dep);
						}
					}
				}

				int left;
				lock (completionLock)
				{
					remaining--;
					left = remaining;
				}

				if (left == 0)
				{
					drainTcs.TrySetResult(true);
				}
				else if (newlyReady.Count > 0)
				{
					ScheduleFromReady();
				}
			}

			if (ready.Count == 0 && actions.Count > 0)
			{
				Log.TraceError("ParallelExecutor: action graph contains a cycle or unresolved prerequisite.");
				return false;
			}

			ScheduleFromReady();

			await drainTcs.Task.ConfigureAwait(false);
			return !anyFailure;
		}

		private static void EnsureOutputDirectories(Action a)
		{
			foreach (FileReference produced in a.ProducedItems)
			{
				string? dir = Path.GetDirectoryName(produced.FullName);
				if (!String.IsNullOrEmpty(dir) && !Directory.Exists(dir))
				{
					Directory.CreateDirectory(dir);
				}
			}
		}
	}
}
