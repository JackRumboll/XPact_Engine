// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.

using System.Text.Json.Serialization;

namespace XPact.Build.Platform
{
	/// <summary>
	/// The type of configuration a target can be built for.  Roughly ordered by optimization level.
	/// </summary>
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public enum UnrealTargetConfiguration
	{
		/// <summary>Unknown</summary>
		Unknown,

		/// <summary>Debug configuration</summary>
		Debug,

		/// <summary>DebugGame configuration; equivalent to development, but with optimization disabled for game modules</summary>
		DebugGame,

		/// <summary>Development configuration</summary>
		Development,

		/// <summary>Test configuration</summary>
		Test,

		/// <summary>Shipping configuration</summary>
		Shipping,
	}
}
