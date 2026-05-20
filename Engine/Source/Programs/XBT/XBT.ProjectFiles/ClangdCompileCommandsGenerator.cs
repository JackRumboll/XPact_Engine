// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.ActionGraph;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Discovery;
using Simgenics.XPact.XBT.Toolchain;

namespace Simgenics.XPact.XBT.ProjectFiles;

/// <summary>
/// Emits <c>compile_commands.json</c> per the LLVM JSON Compilation
/// Database spec (<a href="https://clang.llvm.org/docs/JSONCompilationDatabase.html">clang.llvm.org</a>).
/// Per <c>/Documents/XBT.html</c> Rev 4 Section 14.3: one entry per
/// <c>.cpp</c> in every module enabled by the target.
/// </summary>
/// <remarks>
/// <para>
/// <b>MSVC -&gt; Clang flag translation.</b> If the target is Win64
/// (MSVC), the toolchain emits MSVC-flavour flags (<c>/DFOO</c>,
/// <c>/Iinclude\path</c>); clangd expects Clang-flavour
/// (<c>-DFOO</c>, <c>-I include/path</c>). The translation is mechanical
/// and the standard pattern -- it lets clangd index the codebase on an
/// MSVC build host. Forward slashes are emitted for portability.
/// </para>
/// <para>
/// <b>Determinism.</b> Entries are sorted by the <c>file</c> field
/// using ordinal string comparison; the JSON output is pretty-printed
/// with 2-space indent and ends with a single trailing newline. Two
/// re-runs against the same inputs produce byte-identical output.
/// </para>
/// <para>
/// <b>Lazy generation.</b> The generator emits only for the target /
/// configuration the user specified (footgun #9 preempt per
/// Section 14.1) -- never the full target &times; platform &times;
/// configuration cross-product.
/// </para>
/// </remarks>
public sealed class ClangdCompileCommandsGenerator : IProjectFileGenerator
{
    /// <inheritdoc/>
    public string Name => "clangd";

    /// <summary>
    /// The output file name placed at the engine root. The LLVM spec
    /// fixes the filename; clangd discovers it by walking up from the
    /// active source file.
    /// </summary>
    public const string OutputFileName = "compile_commands.json";

    /// <inheritdoc/>
    public Task<GenerationResult> GenerateAsync(GenerationContext context, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(context);
        token.ThrowIfCancellationRequestedWithDiagnostic("ClangdCompileCommandsGenerator.GenerateAsync");

        try
        {
            string outputPath = Path.Combine(context.EngineRoot, OutputFileName);
            List<Entry> entries = CollectEntries(context, token);

            // Determinism: sort by 'file' ordinal then by 'directory'
            // (one .cpp ought to belong to exactly one module under a
            // single working directory; the secondary sort is defence
            // in depth in case the catalog adds multi-directory builds).
            entries.Sort(static (a, b) =>
            {
                int c = string.CompareOrdinal(a.File, b.File);
                return c != 0 ? c : string.CompareOrdinal(a.Directory, b.Directory);
            });

            string json = SerializeEntries(entries);
            WriteAtomic(outputPath, json);

            return Task.FromResult(new GenerationResult(
                Succeeded: true,
                WrittenFiles: new[] { outputPath }));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(new GenerationResult(
                Succeeded: false,
                WrittenFiles: Array.Empty<string>(),
                ErrorMessage: $"clangd compile_commands.json generation failed: {ex.GetType().Name}: {ex.Message}"));
        }
    }

    /// <summary>
    /// One JSON entry in the compilation database. The shape matches
    /// the LLVM JSON Compilation Database spec verbatim. Internal so
    /// the test suite can exercise <see cref="SerializeEntries"/>
    /// directly under the <see cref="System.Runtime.CompilerServices.InternalsVisibleToAttribute"/>
    /// declared on the assembly.
    /// </summary>
    internal sealed record Entry(string Directory, string Command, string File);

    private static List<Entry> CollectEntries(GenerationContext context, CancellationToken token)
    {
        List<Entry> entries = new();
        foreach (ModuleRecord rec in context.Modules)
        {
            token.ThrowIfCancellationRequestedWithDiagnostic("ClangdCompileCommandsGenerator.CollectEntries");

            // C# / interface-only modules contribute no C++ TUs; skip.
            if (!HasCppLanguage(rec.Rules))
            {
                continue;
            }

            string moduleDir = Path.GetDirectoryName(rec.DescriptorPath)
                ?? throw new InvalidOperationException(
                    $"Module {rec.Rules.Name}: descriptor path '{rec.DescriptorPath}' has no parent directory.");

            // Look for source files under the module's directory tree
            // (Private/, Public/, anywhere the user dropped them).
            foreach (string sourceFile in EnumerateCppSources(moduleDir))
            {
                token.ThrowIfCancellationRequestedWithDiagnostic("ClangdCompileCommandsGenerator.CollectEntries.file");

                IExternalAction compileAction = GetCompileAction(rec.Rules, context, sourceFile);
                string clangCommand = BuildClangCommand(compileAction, context.ToolChain.Platform);

                string normalisedFile = NormalisePath(sourceFile);
                entries.Add(new Entry(
                    Directory: NormalisePath(context.EngineRoot),
                    Command: clangCommand,
                    File: normalisedFile));
            }
        }
        return entries;
    }

    private static bool HasCppLanguage(ModuleRules rules)
    {
        // ModuleRules.Languages is a [Flags] enum; Cpp = 1.
        // We accept any combination including Cpp.
        return (rules.Languages & Simgenics.XPact.XBT.Manifest.Languages.Cpp) != 0;
    }

    private static IEnumerable<string> EnumerateCppSources(string moduleDir)
    {
        if (!Directory.Exists(moduleDir))
        {
            yield break;
        }

        // Enumerate every .cpp / .cc / .cxx under the module directory.
        // Stable ordinal sort so re-runs produce identical output.
        // Excludes generated files (XHT / XIL2CPP outputs) for now -- those
        // are emitted into Intermediate/, not the module's authored source
        // tree, and they appear in compile_commands.json via the same
        // path once Subagent A's full BuildMode pipeline emits them. For
        // Phase 1.3 we cover the authored TUs only.
        string[] cppPatterns = new[] { "*.cpp", "*.cc", "*.cxx" };
        List<string> all = new();
        foreach (string pat in cppPatterns)
        {
            all.AddRange(Directory.EnumerateFiles(moduleDir, pat, SearchOption.AllDirectories));
        }
        all.Sort(StringComparer.Ordinal);
        foreach (string s in all)
        {
            yield return s;
        }
    }

    private static IExternalAction GetCompileAction(
        ModuleRules module,
        GenerationContext context,
        string sourceFilePath)
    {
        // Compose the synthetic FileItem for the source TU and a
        // scratch output directory under Intermediate/. The output
        // path appears only in the emitted command's /Fo or -o flag;
        // we never write to it from this generator.
        FileItem sourceItem = FileItem.GetItemByPath(sourceFilePath);
        string scratchOut = Path.Combine(
            context.EngineRoot,
            "Intermediate",
            "Build",
            context.Target.Name,
            context.Target.Configuration.ToString(),
            "Compile",
            module.Name);

        IReadOnlyList<IExternalAction> actions = context.ToolChain.CompileSource(
            module: module,
            target: context.Target,
            sourceFile: sourceItem,
            outputDir: scratchOut);

        // The toolchain returns a single CompileCppAction for most
        // modules. Multi-action returns are reserved for paths like
        // banned-API preprocess + compile; for clangd we want only the
        // compile, which is the last action in the list.
        return actions[actions.Count - 1];
    }

    /// <summary>
    /// Compose the clangd command vector by translating MSVC-style
    /// flags to Clang-style and concatenating into a single space-
    /// delimited command string per the LLVM spec.
    /// </summary>
    internal static string BuildClangCommand(IExternalAction compile, Simgenics.XPact.XBT.Manifest.Platform platform)
    {
        bool isMsvc = platform == Simgenics.XPact.XBT.Manifest.Platform.Win64;

        // The compiler executable: when MSVC drives the build we still
        // emit clang++ in compile_commands.json so clangd picks the
        // right driver. When Clang drives the build, pass through.
        string compiler = isMsvc ? "clang++" : compile.CommandPath;

        StringBuilder sb = new();
        AppendQuoted(sb, NormalisePath(compiler));
        foreach (string arg in compile.CommandArguments)
        {
            string translated = isMsvc ? TranslateMsvcFlag(arg) : NormalisePathArgument(arg);
            // A translation may produce a multi-flag emission (e.g.
            // MSVC's "/I path" sometimes maps to "-I" + " path");
            // currently every translator returns a single token (the
            // MSVC convention here uses /Ipath, /Dfoo, etc. -- no
            // separate path argument).
            sb.Append(' ');
            AppendQuoted(sb, translated);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Translate a single MSVC-flavour flag to its Clang equivalent.
    /// Forward slashes are emitted for portability; backslash path
    /// separators in arguments are normalised to forward slashes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mapping table:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>/DFOO</c>, <c>/DFOO=1</c> -&gt; <c>-DFOO</c>, <c>-DFOO=1</c>.</item>
    ///   <item><c>/Ipath</c> -&gt; <c>-Ipath</c> (with backslash normalisation).</item>
    ///   <item><c>/c</c> -&gt; <c>-c</c>.</item>
    ///   <item><c>/EHsc</c> -&gt; <c>-fexceptions</c> (closest Clang equivalent).</item>
    ///   <item><c>/GR-</c> -&gt; <c>-fno-rtti</c>.</item>
    ///   <item><c>/WX</c> -&gt; <c>-Werror</c>.</item>
    ///   <item><c>/Od</c> -&gt; <c>-O0</c>; <c>/O1</c> -&gt; <c>-O1</c>; <c>/O2</c> -&gt; <c>-O2</c>.</item>
    ///   <item><c>/fp:precise</c> -&gt; <c>-ffp-contract=off</c>; <c>/fp:fast</c> -&gt; <c>-ffast-math</c>.</item>
    ///   <item><c>/arch:AVX</c> family -&gt; <c>-mavx</c> family.</item>
    ///   <item><c>/Fo&lt;out&gt;</c> -&gt; <c>-o &lt;out&gt;</c> (collapsed back to <c>-o&lt;out&gt;</c> form for single-token emission).</item>
    ///   <item>Reproducibility-envelope MSVC flags (<c>/Brepro</c>, <c>/pathmap:</c>, <c>/nologo</c>, <c>/d2:-cgmanifestencoded-</c>) are dropped -- they confuse clangd's IntelliSense without adding any indexing value.</item>
    ///   <item>Unknown flags starting with <c>/</c> are dropped silently to avoid breaking clangd on toolchain-specific knobs.</item>
    ///   <item>Anything else (the source-file path at the end) passes through with backslash-&gt;forward-slash normalisation.</item>
    /// </list>
    /// </remarks>
    internal static string TranslateMsvcFlag(string arg)
    {
        ArgumentNullException.ThrowIfNull(arg);
        if (arg.Length == 0)
        {
            return arg;
        }

        // SimPath /FI XSimPathMathOverrides.h is a two-token form in
        // the MSVC toolchain ("/FI", "XSimPathMathOverrides.h"). We
        // can't see across argument boundaries here; pass the bare
        // /FI through as -include, and the next argument (the header
        // path) flows through NormalisePathArgument unchanged.
        if (arg == "/FI")
        {
            return "-include";
        }

        if (!arg.StartsWith('/'))
        {
            // Not a flag -- presumably a source-file path. Normalise
            // backslashes to forward slashes for portability.
            return NormalisePathArgument(arg);
        }

        // /D<name> or /D<name>=<value>
        if (arg.StartsWith("/D", StringComparison.Ordinal) && arg.Length > 2)
        {
            return "-D" + arg.Substring(2);
        }
        // /I<path>
        if (arg.StartsWith("/I", StringComparison.Ordinal) && arg.Length > 2)
        {
            return "-I" + NormalisePath(arg.Substring(2));
        }
        // /Fo<path> -> -o<path>
        if (arg.StartsWith("/Fo", StringComparison.Ordinal) && arg.Length > 3)
        {
            return "-o" + NormalisePath(arg.Substring(3));
        }

        // Simple one-for-one flags.
        switch (arg)
        {
            case "/c": return "-c";
            case "/EHsc": return "-fexceptions";
            case "/EHa": return "-fexceptions";
            case "/GR-": return "-fno-rtti";
            case "/WX": return "-Werror";
            case "/Od": return "-O0";
            case "/O1": return "-O1";
            case "/O2": return "-O2";
            case "/Os": return "-Os";
            case "/Oz": return "-Oz";
            case "/fp:precise": return "-ffp-contract=off";
            case "/fp:fast": return "-ffast-math";
            case "/arch:SSE2": return "-msse2";
            case "/arch:SSE42": return "-msse4.2";
            case "/arch:AVX": return "-mavx";
            case "/arch:AVX2": return "-mavx2";
            case "/arch:AVX512": return "-mavx512f";
        }

        // Reproducibility envelope + IDE noise -- drop. clangd does
        // not need to know about pathmap / Brepro / nologo for
        // indexing; they're emitted only for compiler reproducibility.
        if (arg == "/nologo" || arg == "/Brepro" || arg == "/BREPRO"
            || arg.StartsWith("/pathmap:", StringComparison.Ordinal)
            || arg.StartsWith("/d2:", StringComparison.Ordinal)
            || arg == "/INCREMENTAL:NO")
        {
            return string.Empty;
        }

        // Unknown MSVC flag -- silently drop so an unrecognised knob
        // does not break clangd's IntelliSense for the whole TU.
        return string.Empty;
    }

    /// <summary>
    /// Convert backslash path separators to forward slashes inside an
    /// argument. The argument may be a bare path or an already-prefixed
    /// path (e.g. <c>-Iinclude\path</c>). We touch only the path-like
    /// portion; flags like <c>-DFOO\=bar</c> are heuristically not
    /// rewritten (a literal backslash inside a define value is
    /// unusual but legal).
    /// </summary>
    private static string NormalisePathArgument(string arg)
    {
        // If the argument starts with a flag prefix that's path-like
        // (-I / -include / a bare path), normalise. We keep -D values
        // verbatim since a backslash inside a define value is a real
        // C preprocessor concern.
        if (arg.StartsWith("-D", StringComparison.Ordinal))
        {
            return arg;
        }
        return NormalisePath(arg);
    }

    /// <summary>
    /// Convert backslash path separators to forward slashes. Idempotent
    /// on already-normalised paths.
    /// </summary>
    private static string NormalisePath(string path)
    {
        return path.Replace('\\', '/');
    }

    /// <summary>
    /// Append <paramref name="token"/> to <paramref name="sb"/> with
    /// shell-style quoting (only when the token contains a space; bare
    /// tokens are appended verbatim). Skips empty tokens -- the
    /// translator returns an empty string for "drop this flag".
    /// </summary>
    private static void AppendQuoted(StringBuilder sb, string token)
    {
        if (token.Length == 0)
        {
            return;
        }
        if (token.Contains(' '))
        {
            sb.Append('"');
            sb.Append(token);
            sb.Append('"');
        }
        else
        {
            sb.Append(token);
        }
    }

    /// <summary>
    /// Serialize the entry list to the canonical JSON form: pretty-
    /// printed with 2-space indent, LF line endings, terminating
    /// newline. <see cref="JsonSerializerOptions.WriteIndented"/> uses
    /// 2-space indent by default in .NET 8.
    /// </summary>
    internal static string SerializeEntries(IReadOnlyList<Entry> entries)
    {
        // Allocate a fresh JsonWriter with LF line endings; the default
        // System.Text.Json writer uses \n which matches our determinism
        // goal.
        JsonSerializerOptions opts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            // CamelCase keys: the LLVM JSON Compilation Database spec
            // uses lowercase "directory", "command", "file"; our C#
            // record's property names are PascalCase, so the naming
            // policy maps them to camelCase on serialization. (The
            // CamelCase policy lowercases the first letter and leaves
            // the rest, so Directory -> directory, Command -> command,
            // File -> file -- matching the spec verbatim.)
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // Encoder.UnsafeRelaxedJsonEscaping prevents escaping of
            // forward-slashes in path strings -- without this, the
            // emitted JSON would contain "C:\\\\repo" etc. which is
            // ugly to read though valid. Per the LLVM example we want
            // the literal path bytes.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        string json = JsonSerializer.Serialize(entries, opts);
        // System.Text.Json uses \n line endings on every platform when
        // the indent setting is enabled; verify by replacing \r\n with
        // \n just in case a future .NET version shifts to CRLF on
        // Windows.
        json = json.Replace("\r\n", "\n");
        if (!json.EndsWith('\n'))
        {
            json += '\n';
        }
        return json;
    }

    /// <summary>
    /// Write <paramref name="json"/> to <paramref name="outputPath"/>
    /// via temp-file + atomic rename per <c>/Documents/XBT.html</c>
    /// Rev 4 Section 6.4 -- so a killed XBT process never leaves a
    /// torn compile_commands.json. Bytes are UTF-8 with no BOM.
    /// </summary>
    private static void WriteAtomic(string outputPath, string json)
    {
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tmpPath = outputPath + ".tmp";
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        File.WriteAllBytes(tmpPath, bytes);
        // File.Move with overwrite is atomic at the OS layer on every
        // platform XBT supports.
        File.Move(tmpPath, outputPath, overwrite: true);
    }
}
