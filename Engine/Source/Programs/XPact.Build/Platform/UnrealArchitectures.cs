// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealArchitectures. UE 5.9's version takes an optional
// ValidationPlatform parameter that calls UEBuildPlatform.TryGetBuildPlatform
// and UnrealArchitectureConfig.ForPlatform; those types are not in Task 0.1.
// We drop the validation path entirely and only accept already-validated arch
// names; callers can validate themselves once we port the platform plumbing.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace XPact.Build.Platform
{
	/// <summary>
	/// A collection of one or more architectures
	/// </summary>
	[Serializable]
	public class UnrealArchitectures
	{
		/// <summary>Construct from a list of UnrealArch.</summary>
		public UnrealArchitectures(IEnumerable<UnrealArch> architectures)
		{
			Architectures = new List<UnrealArch>(architectures.Distinct());
			Architectures.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.ToString(), b.ToString()));
		}

		/// <summary>Construct from a list of arch name strings.</summary>
		public UnrealArchitectures(IEnumerable<string> architectures)
		{
			Architectures = new List<UnrealArch>(architectures.Select(x => UnrealArch.Parse(x)).Distinct());
			// standardize order so that passing in { X64, Arm64 } will always result in "arm64+x64" filenames, etc
			Architectures.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.ToString(), b.ToString()));
		}

		/// <summary>Construct from a single UnrealArch.</summary>
		public UnrealArchitectures(UnrealArch architecture)
			: this(new[] { architecture })
		{
		}

		/// <summary>Construct from a "+"-separated arch string.</summary>
		public UnrealArchitectures(string architectureString)
			: this(architectureString.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
		{
		}

		/// <summary>Construct a copy of another UnrealArchitectures.</summary>
		public UnrealArchitectures(UnrealArchitectures other)
			: this(other.Architectures)
		{
		}

		[JsonConstructor]
		private UnrealArchitectures(List<UnrealArch> Architectures)
		{
			this.Architectures = [.. Architectures.Distinct()];
		}

		/// <summary>
		/// Creates an UnrealArchitectures from a string (created via ToString() or similar), or returns null if the string is null/empty.
		/// </summary>
		public static UnrealArchitectures? FromString(string? archString)
		{
			if (String.IsNullOrEmpty(archString))
			{
				return null;
			}
			return new UnrealArchitectures(archString);
		}

		/// <summary>
		/// The list of architecture names in this set, sorted by architecture name
		/// </summary>
		public readonly List<UnrealArch> Architectures;

		/// <summary>
		/// Gets the Architecture in the normal case where there is a single Architecture in Architectures, or throw an Exception if not single
		/// </summary>
		[JsonIgnore]
		public UnrealArch SingleArchitecture
		{
			get
			{
				if (Architectures.Count > 1)
				{
					throw new InvalidOperationException($"Asking for a single Architecture, but it has multiple Architectures ({String.Join(", ", Architectures)}). This indicates logic needs to be updated");
				}
				return Architectures[0];
			}
		}

		/// <summary>True if there is is more than one architecture specified</summary>
		[JsonIgnore]
		public bool bIsMultiArch => Architectures.Count > 1;

		/// <summary>Gets the Architectures list as a + delimited string</summary>
		public override string ToString()
		{
			return String.Join('+', Architectures);
		}

		/// <summary>Convenience function to check if the given architecture is in this set</summary>
		public bool Contains(UnrealArch arch)
		{
			return Architectures.Contains(arch);
		}
	}
}
