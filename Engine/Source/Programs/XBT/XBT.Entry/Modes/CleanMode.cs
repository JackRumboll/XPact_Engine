// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.Entry;

/// <summary>
/// Audit fix C11: <c>clean</c> mode. Deletes the per-target intermediate
/// build tree and the per-target binaries output directory for a given
/// (Target, Configuration) tuple. Useful for forcing a from-scratch
/// rebuild without modifying the build descriptor.
/// </summary>
/// <remarks>
/// <para>
/// CLI surface (spec-canonical per XBT.html Section 1.2):
/// </para>
/// <list type="bullet">
///   <item><c>-Target=&lt;name&gt;</c> (required) -- the target whose
///   intermediates / binaries get removed.</item>
///   <item><c>-Platform=&lt;p&gt;</c> (optional) -- restrict to a single
///   platform's binaries subtree.</item>
///   <item><c>-Configuration=&lt;cfg&gt;</c> (optional, default
///   <c>Development</c>) -- restrict the intermediate sweep to one
///   configuration. The binaries directory is shared across
///   configurations and is removed unconditionally for the target.</item>
///   <item><c>-Project=&lt;path&gt;</c> (optional) -- path to a project
///   <c>.xproject</c> descriptor (or a project root directory). When
///   supplied the clean operates on the project's
///   <c>Intermediate/Build/&lt;Target&gt;</c> and
///   <c>Binaries</c> trees in addition to the engine's. When unset, only
///   the engine-side trees are cleaned.</item>
///   <item><c>-EngineRoot=&lt;path&gt;</c> (optional) -- engine root
///   override. When unset, the mode walks up from CWD looking for an
///   <c>Engine.xengine</c> sibling.</item>
///   <item><c>-Engine=&lt;path&gt;</c> (legacy alias of
///   <c>-EngineRoot=</c>, optional).</item>
/// </list>
/// <para>
/// Exit codes: 0 on success; 10 on CLI argument error; 1 on unexpected
/// filesystem error.
/// </para>
/// </remarks>
[XBTMode("clean")]
public sealed class CleanMode : IToolMode<CleanMode>
{
    public static string Name => "clean";

    public static string Description =>
        "Delete the per-target intermediate + binaries output directories.";

    public Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        cancellationToken.ThrowIfCancellationRequestedWithDiagnostic("CleanMode.ExecuteAsync");

        try
        {
            CleanOptions options = CleanOptions.Parse(args);
            string engineRoot = options.EngineRoot ?? DiscoverEngineRoot();
            string? projectRoot = ResolveProjectRoot(options.ProjectRoot);

            int deleted = 0;
            // Sweep the engine-side Intermediate + Binaries trees first,
            // then the project-side trees if -Project= was supplied.
            deleted += SweepRoot(engineRoot, options);
            if (projectRoot is not null)
            {
                deleted += SweepRoot(projectRoot, options);
            }

            string projectQual = projectRoot is null
                ? string.Empty
                : $" (project='{projectRoot}')";
            Logger.Info(
                $"xbt clean: removed {deleted} director(ies) for target '{options.TargetName}'{projectQual}.",
                new DiagnosticContext { Action = "clean" });
            return Task.FromResult(0);
        }
        catch (XBTException ex)
        {
            Logger.Error(ex.Message, exitCode: ex.ExitCode);
            return Task.FromResult(ex.ExitCode);
        }
        catch (IOException ex)
        {
            Logger.Error(
                $"xbt clean failed: {ex.GetType().Name}: {ex.Message}",
                exitCode: 1);
            return Task.FromResult(1);
        }
    }

    private static int SweepRoot(string root, CleanOptions options)
    {
        int deleted = 0;
        string intermediateBuild = Path.Combine(
            root, "Intermediate", "Build", options.TargetName);
        if (options.Configuration is { } cfg)
        {
            string configDir = Path.Combine(intermediateBuild, cfg.ToString());
            deleted += DeleteIfExists(configDir);
        }
        else
        {
            deleted += DeleteIfExists(intermediateBuild);
        }

        string binariesRoot = Path.Combine(root, "Binaries");
        if (options.Platform is { } platform)
        {
            string platformBin = Path.Combine(binariesRoot, platform.ToString());
            deleted += DeleteTargetBinariesUnder(platformBin, options.TargetName);
        }
        else if (Directory.Exists(binariesRoot))
        {
            foreach (string platformDir in Directory.EnumerateDirectories(binariesRoot))
            {
                deleted += DeleteTargetBinariesUnder(platformDir, options.TargetName);
            }
        }
        return deleted;
    }

    /// <summary>
    /// Accept either a project root directory or a path to an
    /// <c>.xproject</c> descriptor; return the containing directory in
    /// the descriptor case. Null input passes through.
    /// </summary>
    private static string? ResolveProjectRoot(string? rawProject)
    {
        if (rawProject is null)
        {
            return null;
        }
        if (File.Exists(rawProject) &&
            rawProject.EndsWith(".xproject", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(Path.GetFullPath(rawProject))
                ?? rawProject;
        }
        return rawProject;
    }

    private static int DeleteIfExists(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return 0;
        }
        Directory.Delete(dir, recursive: true);
        return 1;
    }

    private static int DeleteTargetBinariesUnder(string platformDir, string targetName)
    {
        // Phase 1 binaries layout is "Binaries/<Platform>/<file>.dll" --
        // there is no per-target subdirectory yet. We delete files
        // whose name starts with "<targetName>" or "<targetName>." to
        // match both bare-target and target-with-extension naming. This
        // is intentionally narrow: a Phase 2 binaries layout that adds
        // per-target subdirs gets a more aggressive sweep here.
        if (!Directory.Exists(platformDir))
        {
            return 0;
        }
        int deleted = 0;
        foreach (string file in Directory.EnumerateFiles(platformDir))
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith(targetName + ".", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(name), targetName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (IOException)
                {
                    // Best-effort.
                }
            }
        }
        return deleted;
    }

    private static string DiscoverEngineRoot()
    {
        string? cursor = Environment.CurrentDirectory;
        while (!string.IsNullOrEmpty(cursor))
        {
            string candidate = Path.Combine(cursor, "Engine", "Engine.xengine");
            if (File.Exists(candidate))
            {
                return Path.Combine(cursor, "Engine");
            }
            candidate = Path.Combine(cursor, "Engine.xengine");
            if (File.Exists(candidate))
            {
                return cursor;
            }
            DirectoryInfo? parent = Directory.GetParent(cursor);
            if (parent is null)
            {
                break;
            }
            cursor = parent.FullName;
        }
        throw new XBTException(
            "Could not discover the engine root. Pass -EngineRoot=<path> (or the legacy -Engine=<path>) or run xbt from inside the repo.",
            exitCode: 10);
    }

    private sealed record CleanOptions
    {
        public required string TargetName { get; init; }
        public BuildConfiguration? Configuration { get; init; }
        public Platform? Platform { get; init; }
        public string? EngineRoot { get; init; }
        public string? ProjectRoot { get; init; }

        public static CleanOptions Parse(string[] args)
        {
            string? target = null;
            BuildConfiguration? config = null;
            Platform? platform = null;
            string? engine = null;
            string? project = null;

            foreach (string arg in args)
            {
                if (arg.StartsWith("-Target=", StringComparison.OrdinalIgnoreCase))
                {
                    target = arg["-Target=".Length..];
                }
                else if (arg.StartsWith("-Configuration=", StringComparison.OrdinalIgnoreCase))
                {
                    string raw = arg["-Configuration=".Length..];
                    if (!Enum.TryParse(raw, ignoreCase: true, out BuildConfiguration parsed))
                    {
                        throw new XBTException(
                            $"Invalid -Configuration value '{raw}'.",
                            exitCode: 10);
                    }
                    config = parsed;
                }
                else if (arg.StartsWith("-Platform=", StringComparison.OrdinalIgnoreCase))
                {
                    string raw = arg["-Platform=".Length..];
                    if (!Enum.TryParse(raw, ignoreCase: true, out Platform parsed))
                    {
                        throw new XBTException(
                            $"Invalid -Platform value '{raw}'.",
                            exitCode: 10);
                    }
                    platform = parsed;
                }
                else if (arg.StartsWith("-Project=", StringComparison.OrdinalIgnoreCase))
                {
                    // Spec-canonical -Project= flag (XBT.html Section 1.2):
                    // accept either a .xproject descriptor or a project
                    // root directory.
                    project = arg["-Project=".Length..];
                }
                else if (arg.StartsWith("-EngineRoot=", StringComparison.OrdinalIgnoreCase))
                {
                    // Spec-canonical name per XBT.html Section 1.2.
                    engine = arg["-EngineRoot=".Length..];
                }
                else if (arg.StartsWith("-Engine=", StringComparison.OrdinalIgnoreCase))
                {
                    // Legacy alias preserved for backwards compatibility.
                    engine = arg["-Engine=".Length..];
                }
                else
                {
                    throw new XBTException(
                        $"Unknown argument '{arg}'.",
                        exitCode: 10);
                }
            }

            if (string.IsNullOrEmpty(target))
            {
                throw new XBTException(
                    "-Target=<TargetName> is required.",
                    exitCode: 10);
            }

            return new CleanOptions
            {
                TargetName = target,
                Configuration = config,
                Platform = platform,
                EngineRoot = engine,
                ProjectRoot = project,
            };
        }
    }
}
