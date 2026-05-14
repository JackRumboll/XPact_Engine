// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of EpicGames.Core.LogValueFormatter. The full UE 5.9 version pulls in
// Utf8String / LogValue / LogEventPropertyName / LogValueType, which are heavy
// helpers not yet on the Task 0.1 port set. We keep the public API surface
// (ILogValueFormatter, LogValueFormatter, LogValueFormatterAttribute) plus the
// primitive/string/enumerable formatters, which is all current XPact callers need.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;

namespace XPact.Core.Logging
{
	/// <summary>
	/// Formatter for objects of a given type
	/// </summary>
	public interface ILogValueFormatter
	{
		/// <summary>Format an object to a JSON output stream</summary>
		void Format(object value, Utf8JsonWriter writer);
	}

	/// <summary>
	/// Specifies a type to use for serializing objects to a log event
	/// </summary>
	[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class)]
	public sealed class LogValueFormatterAttribute : Attribute
	{
		/// <summary>Type of the formatter</summary>
		public Type Type { get; }

		/// <summary>Constructor</summary>
		public LogValueFormatterAttribute(Type type)
		{
			Type = type;
		}
	}

	/// <summary>
	/// Annotation specifying the logical type name for a value when written through
	/// LogValueFormatter. Optional friendly name; defaults to the CLR type name.
	/// </summary>
	[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class)]
	public sealed class LogValueTypeAttribute : Attribute
	{
		/// <summary>Logical type name written to the log stream.</summary>
		public string? Name { get; }

		/// <summary>Constructor</summary>
		public LogValueTypeAttribute(string? name = null)
		{
			Name = name;
		}
	}

	/// <summary>
	/// Utility methods for manipulating formatters
	/// </summary>
	public static class LogValueFormatter
	{
		class BoolFormatter : ILogValueFormatter
		{
			public void Format(object value, Utf8JsonWriter writer) => writer.WriteBooleanValue((bool)value);
		}

		class Int32Formatter : ILogValueFormatter
		{
			public void Format(object value, Utf8JsonWriter writer) => writer.WriteNumberValue((int)value);
		}

		class UInt32Formatter : ILogValueFormatter
		{
			public void Format(object value, Utf8JsonWriter writer) => writer.WriteNumberValue((uint)value);
		}

		class Int64Formatter : ILogValueFormatter
		{
			public void Format(object value, Utf8JsonWriter writer) => writer.WriteNumberValue((long)value);
		}

		class UInt64Formatter : ILogValueFormatter
		{
			public void Format(object value, Utf8JsonWriter writer) => writer.WriteNumberValue((ulong)value);
		}

		class FloatFormatter : ILogValueFormatter
		{
			public void Format(object value, Utf8JsonWriter writer) => writer.WriteNumberValue((float)value);
		}

		class DoubleFormatter : ILogValueFormatter
		{
			public void Format(object value, Utf8JsonWriter writer) => writer.WriteNumberValue((double)value);
		}

		class StringFormatter : ILogValueFormatter
		{
			public void Format(object value, Utf8JsonWriter writer) => writer.WriteStringValue(value.ToString());
		}

		class EnumerableFormatter : ILogValueFormatter
		{
			readonly ILogValueFormatter _elementFormatter;

			public EnumerableFormatter(ILogValueFormatter elementFormatter) => _elementFormatter = elementFormatter;

			public void Format(object value, Utf8JsonWriter writer)
			{
				writer.WriteStartArray();
				foreach (object element in (IEnumerable)value)
				{
					_elementFormatter.Format(element, writer);
				}
				writer.WriteEndArray();
			}
		}

		class DictionaryFormatter : ILogValueFormatter
		{
			public void Format(object value, Utf8JsonWriter writer)
			{
				Dictionary<string, object> dictionary = (Dictionary<string, object>)value;
				writer.WriteStartObject();
				foreach (KeyValuePair<string, object> kvp in dictionary)
				{
					writer.WritePropertyName(kvp.Key);
					Format(kvp.Value, writer);
				}
				writer.WriteEndObject();
			}
		}

		class AnnotateTypeFormatter : ILogValueFormatter
		{
			readonly string _type;

			public AnnotateTypeFormatter(string type) => _type = type;

			public void Format(object value, Utf8JsonWriter writer)
			{
				writer.WriteStartObject();
				writer.WriteString("$type", _type);
				writer.WriteString("$text", value.ToString());
				writer.WriteEndObject();
			}
		}

		static readonly StringFormatter s_stringFormatter = new();

		static readonly ConcurrentDictionary<Type, ILogValueFormatter> s_formatters = GetDefaultFormatters();

		static ConcurrentDictionary<Type, ILogValueFormatter> GetDefaultFormatters()
		{
			ConcurrentDictionary<Type, ILogValueFormatter> formatters = new();
			formatters.TryAdd(typeof(bool), new BoolFormatter());
			formatters.TryAdd(typeof(int), new Int32Formatter());
			formatters.TryAdd(typeof(uint), new UInt32Formatter());
			formatters.TryAdd(typeof(long), new Int64Formatter());
			formatters.TryAdd(typeof(ulong), new UInt64Formatter());
			formatters.TryAdd(typeof(float), new FloatFormatter());
			formatters.TryAdd(typeof(double), new DoubleFormatter());
			formatters.TryAdd(typeof(string), s_stringFormatter);
			formatters.TryAdd(typeof(Dictionary<string, object>), new DictionaryFormatter());
			return formatters;
		}

		/// <summary>Writes a typed (type, text) JSON record.</summary>
		public static void WriteTypedValue(Utf8JsonWriter writer, string type, string text)
		{
			writer.WriteStartObject();
			writer.WriteString("$type", type);
			writer.WriteString("$text", text);
			writer.WriteEndObject();
		}

		/// <summary>Registers a formatter for a particular type</summary>
		public static void RegisterFormatter(Type type, ILogValueFormatter formatter)
		{
			s_formatters.TryAdd(type, formatter);
		}

		/// <summary>Registers a formatter for a particular type that adds a $type field to the serialized output</summary>
		public static void RegisterTypeAnnotation<T>(string type)
		{
			s_formatters.TryAdd(typeof(T), new AnnotateTypeFormatter(type));
		}

		/// <summary>Gets a formatter for the specified type</summary>
		public static ILogValueFormatter GetFormatter(Type type)
		{
			for (; ; )
			{
				ILogValueFormatter? formatter;
				if (s_formatters.TryGetValue(type, out formatter))
				{
					return formatter;
				}

				formatter = GetFormatterUncached(type);
				if (s_formatters.TryAdd(type, formatter))
				{
					return formatter;
				}
			}
		}

		static ILogValueFormatter GetFormatterUncached(Type type)
		{
			LogValueFormatterAttribute? formatterAttribute = type.GetCustomAttribute<LogValueFormatterAttribute>();
			if (formatterAttribute != null)
			{
				return (ILogValueFormatter)Activator.CreateInstance(formatterAttribute.Type)!;
			}

			LogValueTypeAttribute? typeAttribute = type.GetCustomAttribute<LogValueTypeAttribute>();
			if (typeAttribute != null)
			{
				return new AnnotateTypeFormatter(typeAttribute.Name ?? type.Name);
			}

			if (TryGetEnumerableType(type, out Type? elementType))
			{
				return new EnumerableFormatter(GetFormatter(elementType));
			}

			return s_stringFormatter;
		}

		static bool TryGetEnumerableType(Type type, [NotNullWhen(true)] out Type? elementType)
		{
			foreach (Type interfaceType in type.GetInterfaces())
			{
				if (interfaceType.IsGenericType && interfaceType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
				{
					elementType = interfaceType.GetGenericArguments()[0];
					return true;
				}
			}

			elementType = null;
			return false;
		}

		/// <summary>Formats a value to a Json log stream</summary>
		public static void Format(object? value, Utf8JsonWriter writer)
		{
			if (value == null)
			{
				writer.WriteNullValue();
				return;
			}

			ILogValueFormatter formatter = GetFormatter(value.GetType());
			formatter.Format(value, writer);
		}
	}
}
