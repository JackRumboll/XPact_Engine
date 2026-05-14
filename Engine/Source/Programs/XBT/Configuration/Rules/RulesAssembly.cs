// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealBuildTool.RulesAssembly. The UE 5.9 version maintains a
// hierarchy of parent assemblies (engine -> plugin -> project) and tracks
// .Target.cs vs .Build.cs vs platform-extension files; XBT Task 0.2 only needs
// a flat map from name -> Type, since we have one engine root and no plugins yet.

using System;
using System.Collections.Generic;
using System.Reflection;
using XPact.Build;
using XPact.Core.IO;

namespace XBT.Configuration.Rules
{
	/// <summary>
	/// A compiled assembly produced by RulesCompiler, providing lookup of
	/// TargetRules / ModuleRules subclasses by name.
	/// </summary>
	public sealed class RulesAssembly
	{
		private readonly Assembly _assembly;
		private readonly Dictionary<string, (Type type, FileReference file)> _targetRules = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, (Type type, FileReference file)> _moduleRules = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>The underlying compiled assembly.</summary>
		public Assembly Assembly => _assembly;

		/// <summary>Construct from a compiled assembly and the source mappings.</summary>
		public RulesAssembly(Assembly assembly, IEnumerable<(string name, Type type, FileReference file)> targetRules, IEnumerable<(string name, Type type, FileReference file)> moduleRules)
		{
			ArgumentNullException.ThrowIfNull(assembly);
			ArgumentNullException.ThrowIfNull(targetRules);
			ArgumentNullException.ThrowIfNull(moduleRules);

			_assembly = assembly;
			foreach ((string name, Type type, FileReference file) in targetRules)
			{
				_targetRules[name] = (type, file);
			}
			foreach ((string name, Type type, FileReference file) in moduleRules)
			{
				_moduleRules[name] = (type, file);
			}
		}

		/// <summary>True if the assembly knows a TargetRules for the given name.</summary>
		public bool HasTarget(string name) => _targetRules.ContainsKey(name);

		/// <summary>Returns the names of every TargetRules subclass known.</summary>
		public IReadOnlyCollection<string> TargetNames => _targetRules.Keys;

		/// <summary>Returns the names of every ModuleRules subclass known.</summary>
		public IReadOnlyCollection<string> ModuleNames => _moduleRules.Keys;

		/// <summary>Instantiate a TargetRules subclass by name.</summary>
		public TargetRules CreateTargetRules(string targetName, TargetInfo info)
		{
			ArgumentNullException.ThrowIfNull(info);
			if (!_targetRules.TryGetValue(targetName, out (Type type, FileReference file) entry))
			{
				throw new BuildException("Unknown target '{0}'. Known targets: {1}", targetName, String.Join(", ", _targetRules.Keys));
			}

			object? instance;
			try
			{
				instance = Activator.CreateInstance(entry.type, info);
			}
			catch (Exception ex) when (ex is MissingMethodException or TargetInvocationException)
			{
				throw new BuildException(ex, "Failed to instantiate TargetRules '{0}' from '{1}': {2}", targetName, entry.file.FullName, ex.Message);
			}

			if (instance is not TargetRules rules)
			{
				throw new BuildException("Type '{0}' from '{1}' does not derive from TargetRules", entry.type.FullName ?? entry.type.Name, entry.file.FullName);
			}

			rules.File = entry.file;
			return rules;
		}

		/// <summary>Instantiate a ModuleRules subclass by name.</summary>
		public ModuleRules CreateModuleRules(string moduleName, TargetRules targetRules)
		{
			ArgumentNullException.ThrowIfNull(targetRules);
			if (!_moduleRules.TryGetValue(moduleName, out (Type type, FileReference file) entry))
			{
				throw new BuildException("Unknown module '{0}'. Known modules: {1}", moduleName, String.Join(", ", _moduleRules.Keys));
			}

			object? instance;
			try
			{
				instance = Activator.CreateInstance(entry.type, targetRules);
			}
			catch (Exception ex) when (ex is MissingMethodException or TargetInvocationException)
			{
				throw new BuildException(ex, "Failed to instantiate ModuleRules '{0}' from '{1}': {2}", moduleName, entry.file.FullName, ex.Message);
			}

			if (instance is not ModuleRules rules)
			{
				throw new BuildException("Type '{0}' from '{1}' does not derive from ModuleRules", entry.type.FullName ?? entry.type.Name, entry.file.FullName);
			}

			rules.Name = moduleName;
			rules.File = entry.file;
			rules.Directory = entry.file.Directory;
			return rules;
		}
	}
}
