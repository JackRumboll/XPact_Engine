// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealTargetPlatform from UnrealBuildTool/Configuration/UEBuildTarget.cs.
// We keep the registry-backed instance pattern (so future platform extensions can
// add entries via partials), and we keep TryParse/Parse/ToString/equality so
// callers behave identically to UE 5.9. The IsInGroup helpers and binary-archive
// extension methods are dropped because they pull in UEBuildPlatform /
// BuildHostPlatform / UnrealPlatformGroup which are not part of Task 0.1.

using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XPact.Build.Platform
{
	/// <summary>
	/// The platform we're building for
	/// </summary>
	[Serializable, TypeConverter(typeof(UnrealTargetPlatformTypeConverter))]
	[JsonConverter(typeof(UnrealTargetPlatformJsonConverter))]
	public partial struct UnrealTargetPlatform : ISerializable, IEquatable<UnrealTargetPlatform>
	{
		#region Private/boilerplate

		// internal concrete name of the group
		private int _id;

		// shared string instance registry - pass in a delegate to create a new one with a name that wasn't made yet
		private static UniqueStringRegistry? s_stringRegistry;

		// #jira UE-88908 if parts of a partial struct have each static member variables, their initialization order does not appear guaranteed
		// here this means initializing "StringRegistry" directly to "new UniqueStringRegistry()" may not be executed before FindOrAddByName() has been called as part of initializing a static member variable of another part of the partial struct
		private static UniqueStringRegistry GetUniqueStringRegistry()
		{
			s_stringRegistry ??= new UniqueStringRegistry();
			return s_stringRegistry;
		}

		private UnrealTargetPlatform(string name)
		{
			_id = GetUniqueStringRegistry().FindOrAddByName(name);
		}

		private UnrealTargetPlatform(int inId)
		{
			_id = inId;
		}

		/// <summary>
		/// ISerializable.GetObjectData
		/// </summary>
		public void GetObjectData(SerializationInfo info, StreamingContext context)
		{
			info.AddValue("Name", ToString());
		}

		/// <summary>
		/// Deserialization constructor
		/// </summary>
		public UnrealTargetPlatform(SerializationInfo info, StreamingContext context)
		{
			_id = GetUniqueStringRegistry().FindOrAddByName((string)info.GetValue("Name", typeof(string))!);
		}

		/// <summary>Return the single instance of the platform with this name</summary>
		private static UnrealTargetPlatform FindOrAddByName(string name)
		{
			return new UnrealTargetPlatform(GetUniqueStringRegistry().FindOrAddByName(name));
		}

#pragma warning disable IDE0051 // "unused private member" - used by platform-specific partial struct extensions
		private static UnrealTargetPlatform AddAliasByName(string alias, UnrealTargetPlatform original)
		{
			GetUniqueStringRegistry().FindOrAddAlias(alias, original.ToString());
			return original;
		}
#pragma warning restore IDE0051

		/// <summary>Equality operator</summary>
		public static bool operator ==(UnrealTargetPlatform a, UnrealTargetPlatform b)
		{
			return a._id == b._id;
		}

		/// <summary>Inequality operator</summary>
		public static bool operator !=(UnrealTargetPlatform a, UnrealTargetPlatform b)
		{
			return a._id != b._id;
		}

		/// <inheritdoc/>
		public override bool Equals(object? b)
		{
			if (b is null)
			{
				return false;
			}

			return _id == ((UnrealTargetPlatform)b)._id;
		}

		/// <inheritdoc/>
		public readonly bool Equals(UnrealTargetPlatform other) => this == other;

		/// <inheritdoc/>
		public override int GetHashCode()
		{
			return _id;
		}

		#endregion

		/// <summary>Return the string representation</summary>
		public override string ToString()
		{
			return GetUniqueStringRegistry().GetStringForId(_id);
		}

		/// <summary>Try to parse a platform name into an UnrealTargetPlatform</summary>
		public static bool TryParse(string name, out UnrealTargetPlatform platform)
		{
			if (GetUniqueStringRegistry().HasString(name))
			{
				platform._id = GetUniqueStringRegistry().FindOrAddByName(name);
				return true;
			}

			if (GetUniqueStringRegistry().HasAlias(name))
			{
				platform._id = GetUniqueStringRegistry().FindExistingAlias(name);
				return true;
			}

			platform._id = -1;
			return false;
		}

		/// <summary>Parse a platform name, throwing on failure</summary>
		public static UnrealTargetPlatform Parse(string name)
		{
			if (GetUniqueStringRegistry().HasString(name))
			{
				return new UnrealTargetPlatform(name);
			}

			if (GetUniqueStringRegistry().HasAlias(name))
			{
				int id = GetUniqueStringRegistry().FindExistingAlias(name);
				return new UnrealTargetPlatform(id);
			}

			throw new BuildException(String.Format(CultureInfo.InvariantCulture, "The platform name {0} is not a valid platform name. Valid names are ({1})", name,
				String.Join(",", GetUniqueStringRegistry().GetStringNames())));
		}

		/// <summary>List of all known platforms</summary>
		public static UnrealTargetPlatform[] GetValidPlatforms()
		{
			return Array.ConvertAll(GetUniqueStringRegistry().GetStringIds(), x => new UnrealTargetPlatform(x));
		}

		/// <summary>Names of all known platforms</summary>
		public static string[] GetValidPlatformNames()
		{
			return GetUniqueStringRegistry().GetStringNames();
		}

		/// <summary>Whether the given name is a known platform name</summary>
		public static bool IsValidName(string name)
		{
			return GetUniqueStringRegistry().HasString(name);
		}

		/// <summary>64-bit Windows</summary>
		public static UnrealTargetPlatform Win64 { get; } = FindOrAddByName("Win64");

		/// <summary>Mac</summary>
		public static UnrealTargetPlatform Mac { get; } = FindOrAddByName("Mac");

		/// <summary>iOS</summary>
		public static UnrealTargetPlatform IOS { get; } = FindOrAddByName("IOS");

		/// <summary>Android</summary>
		public static UnrealTargetPlatform Android { get; } = FindOrAddByName("Android");

		/// <summary>Linux</summary>
		public static UnrealTargetPlatform Linux { get; } = FindOrAddByName("Linux");

		/// <summary>LinuxArm64</summary>
		public static UnrealTargetPlatform LinuxArm64 { get; } = FindOrAddByName("LinuxArm64");

		/// <summary>TVOS</summary>
		public static UnrealTargetPlatform TVOS { get; } = FindOrAddByName("TVOS");

		/// <summary>VisionOS</summary>
		public static UnrealTargetPlatform VisionOS { get; } = FindOrAddByName("VisionOS");
	}

	internal class UnrealTargetPlatformTypeConverter : TypeConverter
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
				return UnrealTargetPlatform.Parse((string)value);
			}
			return base.ConvertFrom(context, culture, value);
		}

		public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
		{
			if (destinationType == typeof(string) && value != null)
			{
				UnrealTargetPlatform platform = (UnrealTargetPlatform)value;
				return platform.ToString();
			}
			return base.ConvertTo(context, culture, value, destinationType);
		}
	}

	internal class UnrealTargetPlatformJsonConverter : JsonConverter<UnrealTargetPlatform>
	{
		public override UnrealTargetPlatform Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType != JsonTokenType.String)
			{
				throw new JsonException("UnrealTargetPlatform values must be represented as JSON strings");
			}
			return UnrealTargetPlatform.Parse(reader.GetString()!);
		}

		public override UnrealTargetPlatform ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType != JsonTokenType.PropertyName)
			{
				throw new JsonException("UnrealTargetPlatform keys must be represented as JSON strings");
			}
			return UnrealTargetPlatform.Parse(reader.GetString()!);
		}

		public override void Write(Utf8JsonWriter writer, UnrealTargetPlatform value, JsonSerializerOptions options)
		{
			writer.WriteStringValue(value.ToString());
		}

		public override void WriteAsPropertyName(Utf8JsonWriter writer, [DisallowNull] UnrealTargetPlatform value, JsonSerializerOptions options)
		{
			writer.WritePropertyName(value.ToString());
		}
	}
}
