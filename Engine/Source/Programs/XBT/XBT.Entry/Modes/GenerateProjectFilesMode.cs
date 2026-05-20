// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Discovery;
using Simgenics.XPact.XBT.Manifest;
using Simgenics.XPact.XBT.ProjectFiles;
using Simgenics.XPact.XBT.Toolchain;

namespace Simgenics.XPact.XBT.Entry;

/// <summary>
/// CLI mode: <c>xbt generate-project-files</c>. Per
/// <c>/Documents/XBT.html</c> Rev 4 Section 14. Discovers plugins +
/// modules for the requested target, then runs one or more
/// <see cref="IProjectFileGenerator"/>s. Default <c>-Generator=all</c>
/// runs both clangd + Rider; <c>-Generator=clangd</c> or
/// <c>-Generator=rider</c> selects exactly one.
/// </summary>
/// <remarks>
/// <para>
/// CLI:
/// <code>
/// xbt generate-project-files [-Engine=&lt;path&gt;] [-Project=&lt;path&gt;]
///                            [-Target=&lt;TargetName&gt;] [-Configuration=&lt;Config&gt;] [-Platform=&lt;Platform&gt;]
///                            [-Generator=clangd|rider|all]
/// </code>
/// </para>
/// <para>
/// <b>Defaults.</b> <c>-Engine=</c> defaults to the discovered repo root.
/// <c>-Target=</c> defaults to <c>EditorTarget</c>; <c>-Configuration=</c>
/// to <c>Development</c>; <c>-Platform=</c> to the host platform.
/// </para>
/// <para>
/// <b>Exit codes (Contract Section 13).</b> 0 on success;
/// 10 on a malformed CLI flag; 50 on generation failure
/// (manifest / write failure family).
/// </para>
/// </remarks>
[XBTMode("generate-project-files")]
public sealed class GenerateProjectFilesMode : IToolMode<GenerateProjectFilesMode>
{
    /// <inheritdoc/>
    public static string Name => "generate-project-files";

    /// <inheritdoc/>
    public static string Description => "Generate IDE project files (clangd compile_commands.json + Rider .idea/) for the active target.";

    /// <inheritdoc/>
    public async Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        cancellationToken.ThrowIfCancellationRequestedWithDiagnostic("GenerateProjectFilesMode.ExecuteAsync");

        // ----- 1. Parse args. -----
        ParsedArgs parsed;
        try
        {
            parsed = ParseArgs(args);
        }
        catch (XBTException ex)
        {
            Logger.Error(ex.Message, exitCode: ex.ExitCode, context: new DiagnosticContext { Action = "generate-project-files" });
            return ex.ExitCode;
        }

        // ----- 2. Resolve generators (fail fast on unknown selector). -----
        List<IProjectFileGenerator> generators = ResolveGenerators(parsed.Generator);
        if (generators.Count == 0)
        {
            Logger.Error(
                $"Unknown -Generator={parsed.Generator}. Expected one of: clangd, rider, all.",
                exitCode: 10,
                context: new DiagnosticContext { Action = "generate-project-files" });
            return 10;
        }

        // ----- 3. Discover plugins + modules. -----
        DiscoveryDiagnostics discoveryDiag = DiscoveryDiagnostics.Default;

        PluginCatalog pluginCatalog;
        ModuleCatalog moduleCatalog;
        try
        {
            pluginCatalog = PluginEnumerator.Enumerate(
                engineRoot: parsed.EngineRoot,
                studioRoot: parsed.StudioRoot,
                projectRoots: parsed.ProjectRoot is null ? Array.Empty<string>() : new[] { parsed.ProjectRoot },
                diagnostics: discoveryDiag);
        }
        catch (XBTException ex)
        {
            Logger.Error(
                $"Plugin discovery failed: {ex.Message}",
                exitCode: ex.ExitCode,
                context: new DiagnosticContext { Action = "generate-project-files" });
            return ex.ExitCode;
        }

        // Collect source roots for module enumeration. We walk every
        // tier's Source/ directory plus every reachable plugin's
        // Source/ directory.
        List<string> sourceRoots = new();
        AddSourceRoot(sourceRoots, parsed.EngineRoot);
        AddSourceRoot(sourceRoots, parsed.StudioRoot);
        AddSourceRoot(sourceRoots, parsed.ProjectRoot);

        // Plugin Source/ roots (project plugins from the resolved
        // catalog; engine + studio plugins are reachable through the
        // walk too).
        foreach (PluginEntry entry in pluginCatalog.Entries)
        {
            string pluginRoot = Path.GetDirectoryName(entry.Resolved.DescriptorPath) ?? string.Empty;
            string pluginSourceRoot = Path.Combine(pluginRoot, "Source");
            if (Directory.Exists(pluginSourceRoot))
            {
                sourceRoots.Add(pluginSourceRoot);
            }
        }

        // Build a synthetic TargetRules for the requested target so
        // module enumeration can evaluate the Roslyn escape hatch.
        TargetRules target = BuildSyntheticTargetRules(parsed);

        try
        {
            moduleCatalog = ModuleEnumerator.Enumerate(
                sourceRoots: sourceRoots,
                diagnostics: discoveryDiag,
                target: target);
        }
        catch (XBTException ex)
        {
            Logger.Error(
                $"Module discovery failed: {ex.Message}",
                exitCode: ex.ExitCode,
                context: new DiagnosticContext { Action = "generate-project-files" });
            return ex.ExitCode;
        }

        // ----- 4. Build toolchain. -----
        XToolChain? toolchain;
        try
        {
            toolchain = BuildToolchain(parsed.EngineRoot, target.Platform);
        }
        catch (XBTException ex)
        {
            Logger.Error(
                $"Toolchain initialisation failed: {ex.Message}",
                exitCode: ex.ExitCode,
                context: new DiagnosticContext { Action = "generate-project-files" });
            return ex.ExitCode;
        }
        if (toolchain is null)
        {
            Logger.Error(
                $"No toolchain available for platform {target.Platform}; generate-project-files cannot emit clangd database. " +
                "Run on a supported host (Win64 MSVC, Linux clang, or Android NDK).",
                exitCode: 50,
                context: new DiagnosticContext { Action = "generate-project-files" });
            return 50;
        }

        // ----- 5. Run generators sequentially. -----
        GenerationContext genContext = new(
            EngineRoot: parsed.EngineRoot,
            StudioRoot: parsed.StudioRoot,
            ProjectRoot: parsed.ProjectRoot,
            Target: target,
            Modules: moduleCatalog.Modules,
            Plugins: pluginCatalog.Entries,
            ToolChain: toolchain,
            LogChannel: "ProjectFiles");

        int overallExitCode = 0;
        foreach (IProjectFileGenerator gen in generators)
        {
            cancellationToken.ThrowIfCancellationRequestedWithDiagnostic("GenerateProjectFilesMode.gen");

            GenerationResult result;
            try
            {
                result = await gen.GenerateAsync(genContext, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(
                    $"Generator '{gen.Name}' threw {ex.GetType().Name}: {ex.Message}",
                    exitCode: 50,
                    context: new DiagnosticContext { Action = "generate-project-files" });
                overallExitCode = 50;
                continue;
            }

            if (!result.Succeeded)
            {
                Logger.Error(
                    $"Generator '{gen.Name}' failed: {result.ErrorMessage}",
                    exitCode: 50,
                    context: new DiagnosticContext { Action = "generate-project-files" });
                overallExitCode = 50;
                continue;
            }

            foreach (string written in result.WrittenFiles)
            {
                Logger.Info(
                    $"[{gen.Name}] wrote {written}",
                    new DiagnosticContext { Action = "generate-project-files", File = written });
            }
        }

        return overallExitCode;
    }

    // -------------------------------------------------------------------
    // CLI arg parsing.
    // -------------------------------------------------------------------

    /// <summary>
    /// Parsed CLI args, ready for the generator pipeline.
    /// </summary>
    private sealed record ParsedArgs(
        string EngineRoot,
        string? StudioRoot,
        string? ProjectRoot,
        string TargetName,
        BuildConfiguration Configuration,
        Platform Platform,
        string Generator);

    private static ParsedArgs ParseArgs(string[] args)
    {
        string? engineFlag = null;
        string? projectFlag = null;
        string? targetFlag = null;
        string? configFlag = null;
        string? platformFlag = null;
        string? generatorFlag = null;

        foreach (string arg in args)
        {
            if (TryConsumeFlag(arg, "-Engine=", out string? v)) { engineFlag = v; continue; }
            if (TryConsumeFlag(arg, "-Project=", out v)) { projectFlag = v; continue; }
            if (TryConsumeFlag(arg, "-Target=", out v)) { targetFlag = v; continue; }
            if (TryConsumeFlag(arg, "-Configuration=", out v)) { configFlag = v; continue; }
            if (TryConsumeFlag(arg, "-Platform=", out v)) { platformFlag = v; continue; }
            if (TryConsumeFlag(arg, "-Generator=", out v)) { generatorFlag = v; continue; }
            // Unknown flag -- 10 = CLI argument error.
            throw new GenerateProjectFilesArgException($"Unrecognised argument '{arg}'.");
        }

        // Defaults.
        string engineRoot = engineFlag ?? DiscoverDefaultEngineRoot();
        if (!Directory.Exists(engineRoot))
        {
            throw new GenerateProjectFilesArgException(
                $"Engine root '{engineRoot}' does not exist on disk. Pass an explicit -Engine=<path>.");
        }

        string? studioRoot = DiscoverDefaultStudioRoot(engineRoot);
        string? projectRoot = projectFlag;
        if (projectRoot is not null && !Directory.Exists(projectRoot))
        {
            throw new GenerateProjectFilesArgException(
                $"Project root '{projectRoot}' does not exist on disk.");
        }

        string targetName = targetFlag ?? "EditorTarget";
        BuildConfiguration config = ParseEnumFlag<BuildConfiguration>(configFlag, defaultValue: BuildConfiguration.Development, flagName: "-Configuration");
        Platform platform = ParseEnumFlag<Platform>(platformFlag, defaultValue: HostPlatform(), flagName: "-Platform");
        string generator = generatorFlag ?? "all";

        return new ParsedArgs(
            EngineRoot: engineRoot,
            StudioRoot: studioRoot,
            ProjectRoot: projectRoot,
            TargetName: targetName,
            Configuration: config,
            Platform: platform,
            Generator: generator);
    }

    private static bool TryConsumeFlag(string arg, string prefix, out string? value)
    {
        if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = arg.Substring(prefix.Length);
            return true;
        }
        value = null;
        return false;
    }

    private static TEnum ParseEnumFlag<TEnum>(string? raw, TEnum defaultValue, string flagName)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrEmpty(raw))
        {
            return defaultValue;
        }
        if (Enum.TryParse(raw, ignoreCase: true, out TEnum parsed))
        {
            return parsed;
        }
        throw new GenerateProjectFilesArgException(
            $"Argument {flagName}={raw} is not a recognised {typeof(TEnum).Name}. " +
            $"Allowed values: {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }

    private static Platform HostPlatform()
    {
        if (OperatingSystem.IsWindows()) return Platform.Win64;
        if (OperatingSystem.IsLinux()) return Platform.Linux;
        if (OperatingSystem.IsAndroid()) return Platform.Android;
        // Fallback: Win64 is the production host.
        return Platform.Win64;
    }

    private static string DiscoverDefaultEngineRoot()
    {
        // Walk up from the CWD looking for an Engine.xengine descriptor.
        // RepoRoot.GetRepoRoot() finds the .git directory; we want the
        // sibling Engine/ directory below it.
        string? repoRoot = RepoRoot.GetRepoRoot();
        if (repoRoot is null)
        {
            // Fall back to CWD/Engine/.
            return Path.Combine(Directory.GetCurrentDirectory(), "Engine");
        }
        return Path.Combine(repoRoot, "Engine");
    }

    private static string? DiscoverDefaultStudioRoot(string engineRoot)
    {
        // Studio/ lives as a sibling of Engine/.
        string? parent = Path.GetDirectoryName(engineRoot);
        if (parent is null)
        {
            return null;
        }
        string studio = Path.Combine(parent, "Studio");
        return Directory.Exists(studio) ? studio : null;
    }

    private static void AddSourceRoot(List<string> sourceRoots, string? tierRoot)
    {
        if (string.IsNullOrEmpty(tierRoot))
        {
            return;
        }
        string sourceDir = Path.Combine(tierRoot, "Source");
        if (Directory.Exists(sourceDir))
        {
            sourceRoots.Add(sourceDir);
        }
    }

    // -------------------------------------------------------------------
    // Target + toolchain assembly.
    // -------------------------------------------------------------------

    private static TargetRules BuildSyntheticTargetRules(ParsedArgs parsed)
    {
        // Per /Documents/XBT.html Section 14.1 we generate for the
        // target the IDE has selected, identified by the CLI flags.
        // The full target descriptor is the project / engine on-disk
        // .Target.toml; for the project-file-generation pass we
        // construct a synthetic TargetRules sufficient for module
        // enumeration's .Build.cs Roslyn escape hatch + toolchain
        // dispatch.
        BuildTargetType targetType = InferTargetType(parsed.TargetName);
        return new TargetRules
        {
            Name = parsed.TargetName,
            TargetType = targetType,
            Configuration = parsed.Configuration,
            Platform = parsed.Platform,
            StationRole = targetType == BuildTargetType.Game ? StationRole.Engineer : StationRole.None,
            SimdLevelDefault = SimdLevel.SSE42,
        };
    }

    private static BuildTargetType InferTargetType(string targetName)
    {
        // EditorTarget / FooEditor / ...Editor -> Editor.
        // ServerTarget / FooServer / ...Server -> Server.
        // Anything else -> Game (the production default).
        if (targetName.EndsWith("Editor", StringComparison.OrdinalIgnoreCase) || targetName.Equals("EditorTarget", StringComparison.OrdinalIgnoreCase))
        {
            return BuildTargetType.Editor;
        }
        if (targetName.EndsWith("Server", StringComparison.OrdinalIgnoreCase) || targetName.Equals("ServerTarget", StringComparison.OrdinalIgnoreCase))
        {
            return BuildTargetType.Server;
        }
        return BuildTargetType.Game;
    }

    private static XToolChain? BuildToolchain(string engineRoot, Platform platform)
    {
        // Repo root: prefer the discovered git root so the toolchain's
        // /pathmap normalisation lines up across machines. Fall back
        // to the engine root parent if no git root is found.
        string repoRoot = RepoRoot.GetRepoRoot() ?? Path.GetDirectoryName(engineRoot) ?? engineRoot;

        switch (platform)
        {
            case Platform.Win64:
                {
                    VCEnvironment.DiscoveryResult result = VCEnvironment.TryDiscover(out VCEnvironment? env);
                    if (result != VCEnvironment.DiscoveryResult.Found || env is null)
                    {
                        return null;
                    }
                    return new XMSVCToolChain(env, repoRoot);
                }
            case Platform.Linux:
            case Platform.Android:
                {
                    if (!XClangToolChain.TryDiscover(platform, repoRoot, out XClangToolChain? chain) || chain is null)
                    {
                        return null;
                    }
                    return chain;
                }
            default:
                return null;
        }
    }

    private static List<IProjectFileGenerator> ResolveGenerators(string generatorFlag)
    {
        List<IProjectFileGenerator> list = new();
        string canon = generatorFlag.ToLower(CultureInfo.InvariantCulture);
        switch (canon)
        {
            case "clangd":
                list.Add(new ClangdCompileCommandsGenerator());
                break;
            case "rider":
                list.Add(new RiderProjectGenerator());
                break;
            case "all":
            case "*":
                list.Add(new ClangdCompileCommandsGenerator());
                list.Add(new RiderProjectGenerator());
                break;
            default:
                // Unknown selector -- caller emits exit 10.
                break;
        }
        return list;
    }
}

/// <summary>
/// CLI argument parse failure. Maps to Contract Section 13 exit code
/// 10 (CLI argument error).
/// </summary>
internal sealed class GenerateProjectFilesArgException : XBTException
{
    public GenerateProjectFilesArgException(string message) : base(message, exitCode: 10) { }
}
