// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// Slim port of UnrealBuildTool.RulesCompiler. The UE 5.9 version does .Target.cs
// discovery via cached source-file metadata, builds a sibling .csproj on the
// fly, and tracks rules-source attribution for hot-reload. XBT Task 0.2 does
// none of that: we recursively discover *.Target.cs / *.Build.cs under a search
// root, compile them all into one in-memory assembly via Roslyn, and resolve
// the class name from the filename stem.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using XPact.Build;
using XPact.Core.IO;
using XPact.Core.Logging;

namespace XBT.Configuration.Rules
{
	/// <summary>
	/// Compiles .Target.cs and .Build.cs files into a RulesAssembly via Roslyn.
	/// </summary>
	public static class RulesCompiler
	{
		/// <summary>
		/// Compile all rules files under the given search roots.
		/// </summary>
		/// <param name="searchRoots">Roots to scan recursively.</param>
		/// <returns>The compiled RulesAssembly.</returns>
		public static RulesAssembly Compile(IEnumerable<DirectoryReference> searchRoots)
		{
			ArgumentNullException.ThrowIfNull(searchRoots);

			List<FileReference> targetFiles = [];
			List<FileReference> moduleFiles = [];
			foreach (DirectoryReference root in searchRoots)
			{
				if (!Directory.Exists(root.FullName))
				{
					continue;
				}
				foreach (string path in Directory.EnumerateFiles(root.FullName, "*.Target.cs", SearchOption.AllDirectories))
				{
					targetFiles.Add(new FileReference(path));
				}
				foreach (string path in Directory.EnumerateFiles(root.FullName, "*.Build.cs", SearchOption.AllDirectories))
				{
					moduleFiles.Add(new FileReference(path));
				}
			}

			if (targetFiles.Count == 0 && moduleFiles.Count == 0)
			{
				throw new BuildException("No .Target.cs or .Build.cs files found under any of the supplied search roots.");
			}

			Log.TraceLog("RulesCompiler: discovered {0} .Target.cs and {1} .Build.cs files", targetFiles.Count, moduleFiles.Count);

			// Build syntax trees, keeping a parallel list so we can attribute classes back to files.
			List<SyntaxTree> trees = [];
			List<FileReference> allFiles = [];
			foreach (FileReference f in targetFiles.Concat(moduleFiles))
			{
				string text = File.ReadAllText(f.FullName);
				trees.Add(CSharpSyntaxTree.ParseText(text, path: f.FullName));
				allFiles.Add(f);
			}

			// Reference assemblies: System runtime, XBT, XPact.Build, XPact.Core.
			List<MetadataReference> refs = BuildReferenceList();

			CSharpCompilation compilation = CSharpCompilation.Create(
				assemblyName: "XBT.GeneratedRules",
				syntaxTrees: trees,
				references: refs,
				options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable));

			using MemoryStream peStream = new();
			EmitResult result = compilation.Emit(peStream);
			if (!result.Success)
			{
				foreach (Diagnostic d in result.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error))
				{
					Log.TraceError("{0}", d.ToString());
				}
				throw new BuildException("Failed to compile rules files; see preceding errors.");
			}
			peStream.Seek(0, SeekOrigin.Begin);
			Assembly compiled = Assembly.Load(peStream.ToArray());

			// Bind each rules file to a Type by matching the filename stem to the class name.
			// e.g. HelloWorld.Target.cs => class HelloWorldTarget : TargetRules { ... }
			// e.g. HelloWorld.Build.cs => class HelloWorld : ModuleRules { ... }
			List<(string, Type, FileReference)> targetEntries = [];
			List<(string, Type, FileReference)> moduleEntries = [];
			foreach (Type t in compiled.GetTypes())
			{
				if (t.IsAbstract || !t.IsClass)
				{
					continue;
				}
				if (typeof(TargetRules).IsAssignableFrom(t))
				{
					string name = StripSuffix(t.Name, "Target");
					FileReference file = FindFileForType(allFiles, t.Name, suffixesToTry: [".Target.cs"]);
					targetEntries.Add((name, t, file));
				}
				else if (typeof(ModuleRules).IsAssignableFrom(t))
				{
					string name = t.Name;
					FileReference file = FindFileForType(allFiles, t.Name, suffixesToTry: [".Build.cs"]);
					moduleEntries.Add((name, t, file));
				}
			}

			Log.TraceLog("RulesCompiler: registered {0} targets, {1} modules", targetEntries.Count, moduleEntries.Count);
			return new RulesAssembly(compiled, targetEntries, moduleEntries);
		}

		private static List<MetadataReference> BuildReferenceList()
		{
			// We reference every loaded assembly that has a non-empty location. That covers:
			// * System.Runtime / System.Collections / System.IO / etc (TPA)
			// * XBT (this assembly) - defines TargetRules / ModuleRules
			// * XPact.Build - defines UnrealTargetPlatform etc.
			// * XPact.Core - defines FileReference etc.
			List<MetadataReference> refs = [];
			HashSet<string> added = new(StringComparer.OrdinalIgnoreCase);
			foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
			{
				if (a.IsDynamic || String.IsNullOrEmpty(a.Location))
				{
					continue;
				}
				if (added.Add(a.Location))
				{
					refs.Add(MetadataReference.CreateFromFile(a.Location));
				}
			}
			// Make sure XBT itself is in. Activator-loaded base types must be findable.
			Assembly xbt = typeof(TargetRules).Assembly;
			if (!String.IsNullOrEmpty(xbt.Location) && added.Add(xbt.Location))
			{
				refs.Add(MetadataReference.CreateFromFile(xbt.Location));
			}
			return refs;
		}

		private static string StripSuffix(string name, string suffix)
		{
			return name.EndsWith(suffix, StringComparison.Ordinal) ? name[..^suffix.Length] : name;
		}

		private static FileReference FindFileForType(List<FileReference> candidates, string typeName, string[] suffixesToTry)
		{
			// For class HelloWorldTarget, we want file HelloWorld.Target.cs
			// For class HelloWorld (extends ModuleRules), we want file HelloWorld.Build.cs
			string stripped = StripSuffix(typeName, "Target");
			foreach (FileReference f in candidates)
			{
				string fileName = f.GetFileName();
				foreach (string suffix in suffixesToTry)
				{
					if (fileName.Equals(stripped + suffix, StringComparison.OrdinalIgnoreCase))
					{
						return f;
					}
				}
			}
			// Fallback: best effort, return the first matching by suffix
			foreach (FileReference f in candidates)
			{
				foreach (string suffix in suffixesToTry)
				{
					if (f.GetFileName().EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
					{
						return f;
					}
				}
			}
			throw new BuildException("Cannot locate rules file for type '{0}'", typeName);
		}
	}
}
