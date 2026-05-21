// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.Configuration;

/// <summary>
/// Phase 1 Roslyn escape hatch for module descriptors. Per Toolchain
/// Contract Rev 13 Section 9.6 and <c>/Documents/XBT.html</c> Rev 4
/// Section 3.6, a module that ships a <c>.Build.cs</c> opts into a
/// per-module Roslyn fallback: XBT compiles the <c>.Build.cs</c> into
/// an assembly, locates the unique public concrete
/// <see cref="ModuleRules"/>-derived class with a public
/// <c>(TargetRules)</c> constructor, instantiates it against the active
/// target, and returns the populated rules.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cost model.</b> Roslyn is referenced by the
/// <c>XBT.Configuration</c> project at build time but the compile path
/// only runs when a <c>.Build.cs</c> is actually seen during discovery
/// (per <see cref="ModuleEnumerator"/> in <c>XBT.Discovery</c>);
/// pure-TOML projects never invoke this class. Per Contract Section 9.6
/// the escape hatch isolates the Roslyn cold-start cost to the modules
/// that genuinely need full C# muscle.
/// </para>
/// <para>
/// <b>Cache.</b> Compiled assemblies are cached at
/// <c>Intermediate/Build/XBT/BuildCsCache/&lt;Hash&gt;.dll</c> keyed by
/// BLAKE3 over the source bytes, <see cref="ContractVersion.Current"/>,
/// and the Roslyn assembly version. A cache hit skips compilation
/// entirely; a cache miss compiles, persists, and then loads.
/// </para>
/// <para>
/// <b>Trust model.</b> The <c>.Build.cs</c> runs with full .NET access
/// (no sandbox). Per Contract Section 9.6 the escape hatch is for
/// "legitimate edge cases" where the developer authoring the module is
/// the same person authoring the engine; sandboxing the descriptor would
/// not improve the security posture and is out of scope for Phase 1.
/// </para>
/// <para>
/// <b>Determinism.</b> The compile sets
/// <see cref="CSharpCompilationOptions.Deterministic"/> = <c>true</c>
/// so two byte-identical source files produce byte-identical assemblies
/// per the Phase 1 reproducibility envelope (Contract Section 2.1).
/// </para>
/// </remarks>
public static class BuildCsCompiler
{
    /// <summary>
    /// Standard <c>.Build.cs</c> filename suffix. Matches the regex
    /// used by <c>ModuleEnumerator</c>'s discovery walk.
    /// </summary>
    public const string BuildCsSuffix = ".Build.cs";

    /// <summary>
    /// Standard subdirectory (relative to the cache root passed in or
    /// derived from the source path) where the compiled DLL artifacts
    /// land. Per the task spec the canonical layout is
    /// <c>Intermediate/Build/XBT/BuildCsCache/</c>.
    /// </summary>
    public const string CacheSubpath = "Intermediate/Build/XBT/BuildCsCache";

    /// <summary>
    /// Compile and execute the <c>.Build.cs</c> at
    /// <paramref name="buildCsPath"/>. Returns the populated
    /// <see cref="ModuleRules"/> instance.
    /// </summary>
    /// <param name="buildCsPath">Absolute path to the <c>.Build.cs</c> file.</param>
    /// <param name="target">
    /// The <see cref="TargetRules"/> instance the <c>.Build.cs</c>
    /// constructor receives. Provides the <c>target.*</c> conditionals
    /// the C# author writes (platform, FIPS mode, etc.).
    /// </param>
    /// <param name="cacheDirectory">
    /// Optional absolute path of the cache directory. When null, the
    /// cache lives next to the source file under
    /// <c>&lt;source-ancestor&gt;/Intermediate/Build/XBT/BuildCsCache/</c>;
    /// see <see cref="ResolveCacheDirectory"/> for the search rule.
    /// Tests pass an isolated scratch directory.
    /// </param>
    /// <returns>The populated rules instance.</returns>
    /// <exception cref="DescriptorParseException">
    /// Thrown on read failure, Roslyn compilation error, missing /
    /// multiple <see cref="ModuleRules"/> subclasses, or any exception
    /// raised by the user-authored constructor. Exit code 30 per
    /// Contract Section 13.
    /// </exception>
    public static ModuleRules Compile(
        string buildCsPath,
        TargetRules target,
        string? cacheDirectory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(buildCsPath);
        ArgumentNullException.ThrowIfNull(target);

        // --- 1. Read source bytes.
        byte[] sourceBytes;
        try
        {
            sourceBytes = File.ReadAllBytes(buildCsPath);
        }
        catch (IOException ex)
        {
            throw new DescriptorParseException(
                $"Could not read .Build.cs at {buildCsPath}: {ex.Message}",
                filePath: buildCsPath);
        }

        // --- 2. Compute cache key (BLAKE3 over source + contract +
        // Roslyn version). The contract version flows through every
        // cache decision per /Documents/XBT.html Section 15.5.
        IoHash cacheKey = ComputeCacheKey(sourceBytes);
        string cacheDir = cacheDirectory ?? ResolveCacheDirectory(buildCsPath);
        string cachedDllPath = Path.Combine(cacheDir, cacheKey + ".dll");
        string cachedPdbPath = Path.Combine(cacheDir, cacheKey + ".pdb");

        // --- 3. Cache hit -- load and instantiate without recompiling.
        Assembly assembly;
        if (File.Exists(cachedDllPath))
        {
            assembly = LoadCachedAssembly(cachedDllPath, cachedPdbPath, buildCsPath);
        }
        else
        {
            assembly = CompileFresh(
                sourceBytes,
                buildCsPath,
                cacheDir,
                cachedDllPath,
                cachedPdbPath);
        }

        // --- 4. Discover the unique ModuleRules subclass.
        Type rulesType = FindModuleRulesSubclass(assembly, buildCsPath);

        // --- 5. Locate the public (TargetRules) constructor.
        ConstructorInfo? ctor = rulesType.GetConstructor(
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(TargetRules) },
            modifiers: null);
        if (ctor is null)
        {
            throw new DescriptorParseException(
                $"Class '{rulesType.FullName}' in {buildCsPath} must declare a public " +
                $"constructor with the single signature " +
                $"`{rulesType.Name}(TargetRules target)`. None was found.",
                filePath: buildCsPath);
        }

        // --- 6. Invoke the constructor.
        ModuleRules rules;
        try
        {
            rules = (ModuleRules)ctor.Invoke(new object[] { target })!;
        }
        catch (TargetInvocationException tex) when (tex.InnerException is not null)
        {
            Exception inner = tex.InnerException;
            (int? line, int? column) = TryGetSourceLocation(inner, buildCsPath);
            throw new DescriptorParseException(
                $".Build.cs constructor threw {inner.GetType().Name}: {inner.Message} " +
                (line is not null
                    ? $"(at line {line}{(column is not null ? ", column " + column : string.Empty)})"
                    : "(during constructor execution; source line unavailable)"),
                filePath: buildCsPath,
                line: line,
                column: column);
        }

        // --- 7. Audit fix R8-M3: inject the descriptor content hash.
        // The user's constructor cannot set this because the discovery
        // layer is the one that holds the descriptor bytes; emit the
        // first 16 hex chars of the BLAKE3 hash directly off the
        // sourceBytes we already have in hand.
        string descriptorHash = IoHash.Compute(sourceBytes).ToString()[..16];
        rules.ApplyDescriptorContentHash(descriptorHash);

        return rules;
    }

    // ---------------------------------------------------------------------
    // Cache key derivation
    // ---------------------------------------------------------------------

    /// <summary>
    /// BLAKE3 hash over: the source bytes, the contract structure hash
    /// hex, and the Roslyn assembly version. Any change to any input
    /// produces a different key and a cache miss; identical input
    /// produces a hit and skips compilation.
    /// </summary>
    private static IoHash ComputeCacheKey(byte[] sourceBytes)
    {
        // Compose a small canonical buffer:
        //   [contract version bytes][NUL][roslyn version bytes][NUL][source bytes]
        // The NUL separators stop a trailing-bytes-of-A appearing
        // identical to a leading-bytes-of-B in any pathological case.
        byte[] contractBytes = Encoding.UTF8.GetBytes(ContractVersion.Current);
        byte[] roslynBytes = Encoding.UTF8.GetBytes(GetRoslynVersionTag());

        int total = contractBytes.Length + 1 + roslynBytes.Length + 1 + sourceBytes.Length;
        byte[] combined = new byte[total];
        int off = 0;
        Buffer.BlockCopy(contractBytes, 0, combined, off, contractBytes.Length);
        off += contractBytes.Length;
        combined[off++] = 0x00;
        Buffer.BlockCopy(roslynBytes, 0, combined, off, roslynBytes.Length);
        off += roslynBytes.Length;
        combined[off++] = 0x00;
        Buffer.BlockCopy(sourceBytes, 0, combined, off, sourceBytes.Length);

        return IoHash.Compute(combined);
    }

    /// <summary>
    /// Roslyn assembly version, used as a cache-key component so that
    /// updating the NuGet package invalidates every prior cached DLL.
    /// </summary>
    private static string GetRoslynVersionTag()
    {
        AssemblyName name = typeof(CSharpCompilation).Assembly.GetName();
        return $"Microsoft.CodeAnalysis.CSharp/{name.Version}";
    }

    // ---------------------------------------------------------------------
    // Cache directory resolution
    // ---------------------------------------------------------------------

    /// <summary>
    /// Locate the directory under which the
    /// <c>Intermediate/Build/XBT/BuildCsCache/</c> tree sits. Walks
    /// from the descriptor's directory upward looking for an ancestor
    /// that contains an <c>Engine/</c> sibling (the canonical layout
    /// signature). Falls back to the descriptor's own directory if
    /// no ancestor matches; this keeps the cache co-located with the
    /// module in test fixtures and in flat scratch workspaces.
    /// </summary>
    private static string ResolveCacheDirectory(string buildCsPath)
    {
        string? dir = Path.GetDirectoryName(buildCsPath);
        if (string.IsNullOrEmpty(dir))
        {
            dir = Directory.GetCurrentDirectory();
        }

        DirectoryInfo? cursor = new(dir);
        while (cursor is not null)
        {
            // Found a workspace root: contains "Engine" as a child folder.
            if (Directory.Exists(Path.Combine(cursor.FullName, "Engine")))
            {
                return Path.Combine(cursor.FullName, CacheSubpath);
            }
            cursor = cursor.Parent;
        }

        // Fallback: live next to the .Build.cs. The path is still under
        // the canonical CacheSubpath so callers / tools can clean by
        // pattern.
        return Path.Combine(dir, CacheSubpath);
    }

    // ---------------------------------------------------------------------
    // Compile (fresh)
    // ---------------------------------------------------------------------

    private static Assembly CompileFresh(
        byte[] sourceBytes,
        string buildCsPath,
        string cacheDir,
        string cachedDllPath,
        string cachedPdbPath)
    {
        Directory.CreateDirectory(cacheDir);

        // Roslyn wants a SourceText. We supply the source with the
        // .Build.cs's path so diagnostics carry the original file
        // location; the embedded PDB will also reference it.
        SourceText sourceText = SourceText.From(
            sourceBytes,
            sourceBytes.Length,
            Encoding.UTF8,
            SourceHashAlgorithm.Sha256,
            throwIfBinaryDetected: false,
            canBeEmbedded: true);
        CSharpParseOptions parseOptions = new CSharpParseOptions(
                languageVersion: LanguageVersion.CSharp12,
                documentationMode: DocumentationMode.None,
                kind: SourceCodeKind.Regular)
            .WithFeatures(Array.Empty<KeyValuePair<string, string>>());
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(
            sourceText,
            parseOptions,
            path: buildCsPath);

        // The descriptor compiles against the public surface of the
        // build-tool host: ModuleRules and TargetRules from
        // XBT.Configuration, the enums and ModuleDep from XBT.Manifest,
        // IoHash + Logger from XBT.Core, and the BCL types ordinary
        // C# code uses (object, List<T>, string, etc.).
        ImmutableArray<MetadataReference> references = BuildMetadataReferences();

        CSharpCompilationOptions options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release,
            allowUnsafe: false,
            deterministic: true,
            platform: Microsoft.CodeAnalysis.Platform.AnyCpu,
            warningLevel: 4,
            nullableContextOptions: NullableContextOptions.Enable);

        string assemblyName = $"XBT.BuildCs.{Path.GetFileNameWithoutExtension(buildCsPath)}";
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { syntaxTree },
            references,
            options);

        // Emit to memory first so a compile failure doesn't leave a
        // half-written .dll behind. Embed the source so portable PDB
        // line lookups for constructor exceptions succeed even if the
        // .Build.cs file is later moved / deleted.
        using MemoryStream dllStream = new();
        using MemoryStream pdbStream = new();
        EmitOptions emitOptions = new EmitOptions(
            debugInformationFormat: DebugInformationFormat.PortablePdb);
        EmitResult emitResult = compilation.Emit(
            peStream: dllStream,
            pdbStream: pdbStream,
            embeddedTexts: new[] { EmbeddedText.FromSource(buildCsPath, sourceText) },
            options: emitOptions);

        if (!emitResult.Success)
        {
            string formatted = FormatDiagnostics(emitResult.Diagnostics, buildCsPath);
            throw new DescriptorParseException(
                $".Build.cs Roslyn compilation failed for {buildCsPath}:\n{formatted}",
                filePath: buildCsPath);
        }

        // Persist atomically: write to a sibling temp file then rename
        // so a concurrent reader either sees the prior file or the new
        // one, never a torn write.
        WriteAtomically(cachedDllPath, dllStream.ToArray());
        WriteAtomically(cachedPdbPath, pdbStream.ToArray());

        // Load the freshly-compiled assembly through the cache path
        // (same path the next caller would hit). This is the only path
        // we ever take when loading -- cache miss collapses into the
        // cache-hit code path after persistence.
        return LoadCachedAssembly(cachedDllPath, cachedPdbPath, buildCsPath);
    }

    private static void WriteAtomically(string targetPath, byte[] data)
    {
        string temp = targetPath + ".tmp." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, data);
        try
        {
            File.Move(temp, targetPath, overwrite: true);
        }
        catch (IOException) when (File.Exists(targetPath))
        {
            // Narrow catch: only "another writer won the rename race"
            // is suppressed. The cache is content-addressable -- their
            // bytes equal ours -- so dropping our temp and adopting
            // their file is correct. Disk-full, permission-denied,
            // and other IOExceptions where the target does NOT exist
            // propagate (no fallback is correct in those cases).
            // UnauthorizedAccessException, OutOfMemoryException, etc.
            // also propagate.
            try { File.Delete(temp); } catch (IOException) { /* best effort */ }
        }
    }

    private static Assembly LoadCachedAssembly(
        string dllPath,
        string pdbPath,
        string buildCsPath)
    {
        try
        {
            // Read the bytes -- loading via path leaves a file lock
            // on Windows that blocks subsequent recompiles from
            // overwriting the DLL on cache invalidation in the same
            // process.
            byte[] dllBytes = File.ReadAllBytes(dllPath);
            byte[]? pdbBytes = File.Exists(pdbPath)
                ? File.ReadAllBytes(pdbPath)
                : null;
            return pdbBytes is not null
                ? Assembly.Load(dllBytes, pdbBytes)
                : Assembly.Load(dllBytes);
        }
        catch (IOException ex)
        {
            throw new DescriptorParseException(
                $"Failed to load cached .Build.cs assembly at {dllPath}: {ex.Message}",
                filePath: buildCsPath);
        }
        catch (BadImageFormatException ex)
        {
            throw new DescriptorParseException(
                $"Cached .Build.cs assembly at {dllPath} is corrupt: {ex.Message}. " +
                "Delete the cache entry and rebuild.",
                filePath: buildCsPath);
        }
    }

    // ---------------------------------------------------------------------
    // Reference assembly resolution
    // ---------------------------------------------------------------------

    /// <summary>
    /// Build the MetadataReference list passed to
    /// <see cref="CSharpCompilation.Create"/>. Includes:
    /// <list type="bullet">
    ///   <item>The BCL runtime assemblies (System.Runtime, System.Collections, etc.) needed for ordinary code patterns.</item>
    ///   <item><c>XBT.Core</c>, <c>XBT.Manifest</c>, <c>XBT.Configuration</c> so the descriptor can name <see cref="ModuleRules"/>, <see cref="TargetRules"/>, <see cref="ModuleDep"/>, the enums, etc.</item>
    /// </list>
    /// </summary>
    private static ImmutableArray<MetadataReference> BuildMetadataReferences()
    {
        ImmutableArray<MetadataReference>.Builder builder = ImmutableArray.CreateBuilder<MetadataReference>();

        // The trusted-platform-assemblies (TPA) list is the canonical
        // way to enumerate BCL DLLs at runtime for a .NET 8+ host. It
        // lists every DLL the runtime considers part of the framework.
        string? tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        HashSet<string> tpaSet = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(tpa))
        {
            foreach (string raw in tpa.Split(Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(raw))
                {
                    continue;
                }
                tpaSet.Add(raw);
            }
        }

        // Reference every TPA DLL by path. This gives the descriptor
        // access to anything in the framework; the cost is bounded
        // (Roslyn loads metadata lazily) and the surface is exactly
        // what a normal .NET 8 csproj sees.
        foreach (string path in tpaSet)
        {
            try
            {
                builder.Add(MetadataReference.CreateFromFile(path));
            }
            catch
            {
                // A TPA entry that isn't a valid PE / metadata file
                // (rare; e.g. native libs accidentally listed) is
                // skipped. Worst case the descriptor fails to compile
                // with a clear "unresolved reference" diagnostic.
            }
        }

        // Add the three XBT assemblies the descriptor consumes. They
        // are loaded by the host process so the location is reachable
        // on disk -- no temp-file games needed.
        AddAssemblyReference(builder, typeof(ModuleRules).Assembly);    // XBT.Configuration
        AddAssemblyReference(builder, typeof(ModuleDep).Assembly);      // XBT.Manifest
        AddAssemblyReference(builder, typeof(IoHash).Assembly);         // XBT.Core

        return builder.ToImmutable();
    }

    private static void AddAssemblyReference(
        ImmutableArray<MetadataReference>.Builder builder,
        Assembly asm)
    {
        if (string.IsNullOrEmpty(asm.Location))
        {
            // Single-file or trimmed scenarios: assembly has no disk
            // location. We don't support such hosts in Phase 1 (XBT
            // itself runs from a normal published layout).
            throw new InvalidOperationException(
                $"Assembly '{asm.GetName().Name}' has no on-disk location. " +
                "BuildCsCompiler requires a non-single-file host.");
        }
        builder.Add(MetadataReference.CreateFromFile(asm.Location));
    }

    // ---------------------------------------------------------------------
    // Diagnostic formatting
    // ---------------------------------------------------------------------

    private static string FormatDiagnostics(
        ImmutableArray<Diagnostic> diagnostics,
        string buildCsPath)
    {
        StringBuilder sb = new();
        foreach (Diagnostic d in diagnostics)
        {
            if (d.Severity != DiagnosticSeverity.Error)
            {
                continue;
            }
            FileLinePositionSpan span = d.Location.GetLineSpan();
            // Lines/columns are 0-based in Roslyn; convert to 1-based
            // for the MSBuild-style "path:line:column: message" prefix.
            int line = span.StartLinePosition.Line + 1;
            int col = span.StartLinePosition.Character + 1;
            string path = string.IsNullOrEmpty(span.Path) ? buildCsPath : span.Path;
            sb.Append(path).Append(':').Append(line).Append(':').Append(col)
              .Append(": error ").Append(d.Id).Append(": ").AppendLine(d.GetMessage());
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------------
    // ModuleRules subclass discovery
    // ---------------------------------------------------------------------

    private static Type FindModuleRulesSubclass(Assembly assembly, string buildCsPath)
    {
        Type[] candidates;
        try
        {
            candidates = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException rtle)
        {
            // Roslyn can produce an assembly whose type list partially
            // loads if a reference is unresolved at runtime. Use the
            // types it could resolve and prefer them; ignore nulls.
            candidates = rtle.Types.Where(t => t is not null).ToArray()!;
        }

        List<Type> matches = new();
        foreach (Type t in candidates)
        {
            if (t.IsPublic
                && !t.IsAbstract
                && !t.IsInterface
                && typeof(ModuleRules).IsAssignableFrom(t)
                && t != typeof(ModuleRules))
            {
                matches.Add(t);
            }
        }

        if (matches.Count == 0)
        {
            throw new DescriptorParseException(
                $"No ModuleRules subclass found in {buildCsPath}. The file " +
                "must declare exactly one public, non-abstract class derived " +
                "from `Simgenics.XPact.XBT.Configuration.ModuleRules` with a " +
                "public constructor taking a single `TargetRules` parameter.",
                filePath: buildCsPath);
        }
        if (matches.Count > 1)
        {
            string list = string.Join(", ", matches.Select(t => t.FullName));
            throw new DescriptorParseException(
                $"Multiple ModuleRules subclasses found in {buildCsPath}: [{list}]. " +
                "Exactly one is required.",
                filePath: buildCsPath);
        }
        return matches[0];
    }

    // ---------------------------------------------------------------------
    // Constructor-exception source location lookup
    // ---------------------------------------------------------------------

    /// <summary>
    /// Inspect the runtime <see cref="StackTrace"/> of an exception
    /// thrown by the <c>.Build.cs</c> constructor and pull the
    /// (line, column) from the first frame whose file matches the
    /// .Build.cs source path. The compile embeds source text into the
    /// portable PDB so frame line lookups remain valid even after the
    /// original file is moved on disk.
    /// </summary>
    /// <returns>
    /// A tuple of nullable line + column. Both null when no PDB
    /// information is available for the frame (e.g. release builds
    /// of the user assembly without symbols, or an exception thrown
    /// from a TPA frame above the constructor).
    /// </returns>
    private static (int? line, int? column) TryGetSourceLocation(
        Exception inner,
        string buildCsPath)
    {
        try
        {
            // StackTrace(Exception, bool) -- the bool enables file-info
            // (line numbers + filename) lookup from the portable PDB.
            StackTrace trace = new(inner, fNeedFileInfo: true);
            string canonicalSource = NormalizePath(buildCsPath);
            for (int i = 0; i < trace.FrameCount; i++)
            {
                StackFrame? frame = trace.GetFrame(i);
                if (frame is null)
                {
                    continue;
                }
                string? file = frame.GetFileName();
                if (string.IsNullOrEmpty(file))
                {
                    continue;
                }
                if (string.Equals(NormalizePath(file), canonicalSource, StringComparison.OrdinalIgnoreCase))
                {
                    int line = frame.GetFileLineNumber();
                    int col = frame.GetFileColumnNumber();
                    return (line > 0 ? line : null, col > 0 ? col : null);
                }
            }
        }
        catch
        {
            // Best effort -- never let a stack-walk failure mask the
            // underlying user exception.
        }
        return (null, null);
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').Trim();
}
