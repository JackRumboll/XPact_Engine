// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Stub project-files mode for Task 0.2. The real implementation lives in
// Task 0.4 once we have generators for the Visual Studio / VSCode targets.

using System.Threading.Tasks;
using XPact.Core.Logging;

namespace XBT.Modes
{
	/// <summary>
	/// Stub mode that prints a marker line and exits. Real implementation
	/// arrives in Task 0.4.
	/// </summary>
	public sealed class GenerateProjectFilesMode : IToolMode
	{
		/// <inheritdoc/>
		public string Name => "genproject";

		/// <inheritdoc/>
		public Task<int> ExecuteAsync(string[] args)
		{
			_ = args;
			Log.TraceInformation("[XBT] genproject not implemented in Task 0.2; see Task 0.4.");
			return Task.FromResult(0);
		}
	}
}
