// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace XPact.Build
{
	/// <summary>
	/// Maps a unique string to an integer. Used to back UnrealTargetPlatform / UnrealArch / etc.
	/// </summary>
	public class UniqueStringRegistry
	{
		// protect the InstanceMap
		private readonly Lock _lockObject = new();

		// holds a mapping of string name to single instance
		private Dictionary<string, int> _stringToInstanceMap = new(StringComparer.OrdinalIgnoreCase);

		// holds a mapping of string aliases to original string name
		private Dictionary<string, string> _aliasToStringMap = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Default constructor
		/// </summary>
		public UniqueStringRegistry()
		{
		}

		/// <summary>
		/// See if string is in registry
		/// </summary>
		/// <returns>True if string is present in registry</returns>
		public bool HasString(string name)
		{
			return _stringToInstanceMap.ContainsKey(name);
		}

		/// <summary>
		/// Add string if missing and/or lookup
		/// </summary>
		/// <returns>String instance id</returns>
		public int FindOrAddByName(string name)
		{
			// look for existing one
			if (!_stringToInstanceMap.TryGetValue(name, out int instance))
			{
				lock (_lockObject)
				{
					if (!_stringToInstanceMap.TryGetValue(name, out instance))
					{
						// copy over the dictionary and add, so that other threads can keep reading out of the old one until it's time
						Dictionary<string, int> newStringToInstanceMap = new(_stringToInstanceMap, StringComparer.OrdinalIgnoreCase);

						// make and add a new instance number
						instance = _stringToInstanceMap.Count;
						newStringToInstanceMap[name] = instance;

						// replace the class's map
						_stringToInstanceMap = newStringToInstanceMap;
					}
				}
			}
			return instance;
		}

		/// <summary>
		/// Get list of strings
		/// </summary>
		/// <returns>String list</returns>
		public string[] GetStringNames()
		{
			return _stringToInstanceMap.Keys.ToArray();
		}

		/// <summary>
		/// Get list of string ids
		/// </summary>
		/// <returns>String id list</returns>
		public int[] GetStringIds()
		{
			return _stringToInstanceMap.Values.ToArray();
		}

		/// <summary>
		/// Get string given instance id
		/// </summary>
		/// <returns>String</returns>
		public string GetStringForId(int id)
		{
			return _stringToInstanceMap.First(x => x.Value == id).Key;
		}

		/// <summary>
		/// See if alias exists in registry
		/// </summary>
		/// <returns>True if alias exists</returns>
		public bool HasAlias(string alias)
		{
			return _aliasToStringMap.ContainsKey(alias);
		}

		/// <summary>
		/// Get instance id of alias
		/// </summary>
		/// <returns>Instance id of alias</returns>
		public int FindExistingAlias(string alias)
		{
			string? name;
			if (!_aliasToStringMap.TryGetValue(alias, out name) || name == null)
			{
				throw new BuildException($"Alias {alias} not found");
			}

			return FindOrAddByName(name);
		}

		/// <summary>
		/// Add alias if missing and/or lookup
		/// </summary>
		/// <returns>Instance id of alias</returns>
		public int FindOrAddAlias(string alias, string originalName)
		{
			string? name;
			if (!_aliasToStringMap.TryGetValue(alias, out name))
			{
				lock (_lockObject)
				{
					// copy over the dictionary and add, so that other threads can keep reading out of the old one until it's time
					Dictionary<string, string> newAliasToStringMap = new(_aliasToStringMap, StringComparer.OrdinalIgnoreCase);

					// add a new alias
					newAliasToStringMap[alias] = originalName;

					// replace the instance map
					_aliasToStringMap = newAliasToStringMap;
				}
				name = originalName;
			}
			else
			{
				if (name != originalName || name == null)
				{
					throw new BuildException($"{alias} is already an alias for {name}. Can't associate with {originalName}");
				}
			}

			return FindOrAddByName(name);
		}
	}
}
