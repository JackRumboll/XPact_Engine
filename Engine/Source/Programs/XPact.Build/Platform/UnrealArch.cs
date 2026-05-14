// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealArch from UnrealBuildTool/Configuration/UEBuildTarget.cs.
// The UE 5.9 implementation includes a `Host` Lazy<UnrealArch> that queries
// UnrealArchitectureConfig.ForPlatform(BuildHostPlatform.Current.Platform);
// those heavy dependencies are out of scope for Task 0.1, so `Host` is omitted
// and TryParse/Parse treat "Host" as unknown.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XPact.Build.Platform
{
	/// <summary>
	/// The architecture we're building for
	/// </summary>
	[Serializable, TypeConverter(typeof(UnrealArchPlatformTypeConverter))]
	[JsonConverter(typeof(UnrealArchPlatformJsonConverter))]
	public partial struct UnrealArch : ISerializable, IEquatable<UnrealArch>
	{
		#region Private/boilerplate

		// internal concrete name of the group
		private int _id;

		// shared string instance registry - pass in a delegate to create a new one with a name that wasn't made yet
		private static UniqueStringRegistry? s_stringRegistry;

		// tracks if non-default architectures are an x64 arch
		private static readonly Dictionary<int, bool> s_isX64Map = new();

		// #jira UE-88908 see UnrealTargetPlatform for explanation
		private static UniqueStringRegistry GetUniqueStringRegistry()
		{
			s_stringRegistry ??= new UniqueStringRegistry();
			return s_stringRegistry;
		}

		private UnrealArch(string name)
		{
			_id = GetUniqueStringRegistry().FindOrAddByName(name);
		}

		private UnrealArch(int inId)
		{
			_id = inId;
		}

		/// <summary>ISerializable.GetObjectData</summary>
		public void GetObjectData(SerializationInfo info, StreamingContext context)
		{
			info.AddValue("Name", ToString());
		}

		/// <summary>Deserialization constructor</summary>
		public UnrealArch(SerializationInfo info, StreamingContext context)
		{
			_id = GetUniqueStringRegistry().FindOrAddByName((string)info.GetValue("Name", typeof(string))!);
		}

		/// <summary>Return the single instance of the architecture with this name</summary>
		/// <param name="name">Architecture name.</param>
		/// <param name="bIsX64">True if X64, false if not, or null if it's Default and will ask the platform later</param>
		private static UnrealArch FindOrAddByName(string name, bool? bIsX64)
		{
			int id = GetUniqueStringRegistry().FindOrAddByName(name);
			if (bIsX64 != null)
			{
				s_isX64Map[id] = bIsX64.Value;
			}
			return new UnrealArch(id);
		}

		/// <summary>Equality operator</summary>
		public static bool operator ==(UnrealArch a, UnrealArch b) => a._id == b._id;

		/// <summary>Inequality operator</summary>
		public static bool operator !=(UnrealArch a, UnrealArch b) => a._id != b._id;

		/// <inheritdoc/>
		public override bool Equals(object? b)
		{
			if (b is null)
			{
				return false;
			}

			return _id == ((UnrealArch)b)._id;
		}

		/// <inheritdoc/>
		public readonly bool Equals(UnrealArch other) => this == other;

		/// <inheritdoc/>
		public override int GetHashCode() => _id;

		#endregion

		/// <summary>
		/// Returns true if the architecure is X64 based
		/// </summary>
		public bool bIsX64 => s_isX64Map[_id];

		/// <summary>Return the string representation</summary>
		public override string ToString()
		{
			return GetUniqueStringRegistry().GetStringForId(_id);
		}

		private static string FixName(string name)
		{
			// allow for some alternate names
			switch (name.ToUpperInvariant())
			{
				case "A8":
				case "A":
				case "-ARM64":
					name = "arm64";
					break;

				case "X86_64":
				case "X6":
				case "X":
				case "-X64":
				case "INTEL":
					name = "x64";
					break;
			}

			return name;
		}

		/// <summary>Try to parse an architecture name</summary>
		public static bool TryParse(string name, out UnrealArch arch)
		{
			// "Host" is handled here in UE5.9; Task 0.1 does not include the host-arch detector, so reject.
			if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
			{
				arch._id = -1;
				return false;
			}

			name = FixName(name);

			if (GetUniqueStringRegistry().HasString(name))
			{
				arch._id = GetUniqueStringRegistry().FindOrAddByName(name);
				return true;
			}

			if (GetUniqueStringRegistry().HasAlias(name))
			{
				arch._id = GetUniqueStringRegistry().FindExistingAlias(name);
				return true;
			}

			arch._id = -1;
			return false;
		}

		/// <summary>Parse an architecture name, throwing on failure</summary>
		public static UnrealArch Parse(string name)
		{
			if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
			{
				throw new BuildException("UnrealArch.Host is not available in Task 0.1 (no UnrealArchitectureConfig yet).");
			}

			name = FixName(name);

			if (GetUniqueStringRegistry().HasString(name))
			{
				return new UnrealArch(name);
			}

			if (GetUniqueStringRegistry().HasAlias(name))
			{
				int id = GetUniqueStringRegistry().FindExistingAlias(name);
				return new UnrealArch(id);
			}

			throw new BuildException(String.Format(CultureInfo.InvariantCulture, "The Architecture name {0} is not a valid Architecture name. Valid names are ({1})", name,
				String.Join(",", GetUniqueStringRegistry().GetStringNames())));
		}

		/// <summary>List of all known architectures</summary>
		public static UnrealArch[] GetValidPlatforms()
		{
			return Array.ConvertAll(GetUniqueStringRegistry().GetStringIds(), x => new UnrealArch(x));
		}

		/// <summary>Names of all known architectures</summary>
		public static string[] GetValidPlatformNames()
		{
			return GetUniqueStringRegistry().GetStringNames();
		}

		/// <summary>Whether the given name is a known architecture name</summary>
		public static bool IsValidName(string name)
		{
			return GetUniqueStringRegistry().HasString(name);
		}

		/// <summary>64-bit x86/intel</summary>
		public static UnrealArch X64 { get; } = FindOrAddByName("x64", bIsX64: true);

		/// <summary>64-bit arm</summary>
		public static UnrealArch Arm64 { get; } = FindOrAddByName("arm64", bIsX64: false);

		/// <summary>
		/// Used in place when needing to handle deprecated architectures that a licensee may still have. Do not use in normal logic
		/// </summary>
		public static UnrealArch Deprecated { get; } = FindOrAddByName("deprecated", bIsX64: false);
	}

	internal class UnrealArchPlatformTypeConverter : TypeConverter
	{
		public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
		{
			if (sourceType == typeof(string))
			{
				return true;
			}
			return base.CanConvertFrom(context, sourceType);
		}

		public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType)
		{
			if (destinationType == typeof(string))
			{
				return true;
			}
			return base.CanConvertTo(context, destinationType);
		}

		public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
		{
			if (value.GetType() == typeof(string))
			{
				return UnrealArch.Parse((string)value);
			}
			return base.ConvertFrom(context, culture, value);
		}

		public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
		{
			if (destinationType == typeof(string) && value != null)
			{
				UnrealArch platform = (UnrealArch)value;
				return platform.ToString();
			}
			return base.ConvertTo(context, culture, value, destinationType);
		}
	}

	internal class UnrealArchPlatformJsonConverter : JsonConverter<UnrealArch>
	{
		public override UnrealArch Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType != JsonTokenType.String)
			{
				throw new JsonException("UnrealArch values must be represented as JSON strings");
			}
			return UnrealArch.Parse(reader.GetString()!);
		}

		public override UnrealArch ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType != JsonTokenType.PropertyName)
			{
				throw new JsonException("UnrealArch values must be represented as JSON strings");
			}
			return UnrealArch.Parse(reader.GetString()!);
		}

		public override void Write(Utf8JsonWriter writer, UnrealArch value, JsonSerializerOptions options)
		{
			writer.WriteStringValue(value.ToString());
		}

		public override void WriteAsPropertyName(Utf8JsonWriter writer, [DisallowNull] UnrealArch value, JsonSerializerOptions options)
		{
			writer.WritePropertyName(value.ToString());
		}
	}
}
