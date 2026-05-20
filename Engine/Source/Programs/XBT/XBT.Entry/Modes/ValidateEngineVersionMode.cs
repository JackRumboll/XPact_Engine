// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Discovery;

namespace Simgenics.XPact.XBT.Entry;

/// <summary>
/// Audit fix C11: <c>validate-engine-version</c> mode. Reads
/// <c>Engine/Engine.xengine</c>, then walks every reachable
/// <c>.xplugin</c> verifying each plugin's
/// <c>[MinEngineVersion, MaxEngineVersion]</c> range contains the
/// engine's actual semver. Delegates to
/// <see cref="EngineVersionValidator"/> so the standalone CLI and the
/// in-build check share one implementation.
/// </summary>
/// <remarks>
/// <para>
/// CLI surface (spec-canonical per XBT.html Section 1.2):
/// </para>
/// <list type="bullet">
///   <item><c>-EngineRoot=&lt;path&gt;</c> (optional) -- engine root
///   override. When unset, the mode walks up from CWD.</item>
///   <item><c>-Engine=&lt;path&gt;</c> (legacy alias of
///   <c>-EngineRoot=</c>, optional) -- preserved for backwards
///   compatibility with the Round-5a callers.</item>
///   <item><c>-Studio=&lt;path&gt;</c> (optional) -- studio root for
///   plugin discovery.</item>
///   <item><c>-Project=&lt;path&gt;</c> (optional) -- project root for
///   plugin + project descriptor discovery (may be either a project
///   root directory or a path to an <c>.xproject</c> descriptor).</item>
/// </list>
/// <para>
/// Exit codes: 0 on success; 23
/// (<c>EngineOrToolchainVersionMismatch</c>) on engine-version
/// incompatibility, per the Toolchain Contract Rev 13 Section 13
/// exit-code surface (<see cref="Simgenics.XPact.XBT.Manifest.ContractSurface.ExitCodes"/>).
/// Code 22 is <c>CycleDetected</c> in the same table and must not be
/// reused for version mismatches.
/// </para>
/// </remarks>
[XBTMode("validate-engine-version")]
public sealed class ValidateEngineVersionMode : IToolMode<ValidateEngineVersionMode>
{
    public static string Name => "validate-engine-version";

    public static string Description =>
        "Validate engine + plugin EngineVersion compatibility across the repo.";

    /// <summary>
    /// Engine-version-mismatch exit code per Toolchain Contract Rev 13
    /// Section 13: <c>23 = EngineOrToolchainVersionMismatch</c>. The
    /// standalone <c>validate-engine-version</c> mode and the in-build
    /// check both map their failure to this code so callers observe one
    /// stable signal for the version-mismatch family. Code 22 in the
    /// same table is <c>CycleDetected</c> and must not be reused here.
    /// </summary>
    public const int EngineVersionMismatchExitCode = 23;

    public Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        cancellationToken.ThrowIfCancellationRequestedWithDiagnostic("ValidateEngineVersionMode.ExecuteAsync");

        try
        {
            ValidateOptions options = ValidateOptions.Parse(args);
            string engineRoot = options.EngineRoot ?? DiscoverEngineRoot();

            // 1. Discover the engine semver.
            SemanticVersion engineVersion =
                EngineVersionValidator.DiscoverEngineVersion(engineRoot);
            Logger.Info(
                $"Engine version {engineVersion} discovered from {engineRoot}/Engine.xengine.",
                new DiagnosticContext { Action = "validate-engine-version" });

            // 2. Discover plugins under engine + studio + project. A
            // -Project= value pointing at an .xproject descriptor is
            // resolved to its containing directory before the plugin
            // enumerator runs (the enumerator wants a directory root).
            string? projectRootResolved = ResolveProjectRoot(options.ProjectRoot);
            IReadOnlyList<string> projectRoots = projectRootResolved is null
                ? Array.Empty<string>()
                : new[] { projectRootResolved };
            PluginCatalog plugins = PluginEnumerator.Enumerate(
                engineRoot,
                options.StudioRoot,
                projectRoots,
                DiscoveryDiagnostics.Default);

            // 3. Validate each plugin's range against the engine semver.
            List<PluginDescriptor> descriptors = new(plugins.Entries.Count);
            foreach (PluginEntry entry in plugins.Entries)
            {
                descriptors.Add(entry.Resolved.Descriptor);
            }
            EngineVersionValidator.ValidatePlugins(descriptors, engineVersion);

            Logger.Info(
                $"validate-engine-version: {plugins.Entries.Count} plugin(s) all compatible with engine {engineVersion}.",
                new DiagnosticContext { Action = "validate-engine-version" });
            return Task.FromResult(0);
        }
        catch (EngineVersionMismatchException ex)
        {
            Logger.Error(ex.Message, exitCode: EngineVersionMismatchExitCode);
            return Task.FromResult(EngineVersionMismatchExitCode);
        }
        catch (XBTException ex)
        {
            Logger.Error(ex.Message, exitCode: ex.ExitCode);
            return Task.FromResult(ex.ExitCode);
        }
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

    private sealed record ValidateOptions
    {
        public string? EngineRoot { get; init; }
        public string? StudioRoot { get; init; }
        public string? ProjectRoot { get; init; }

        public static ValidateOptions Parse(string[] args)
        {
            string? engine = null;
            string? studio = null;
            string? project = null;

            foreach (string arg in args)
            {
                if (arg.StartsWith("-EngineRoot=", StringComparison.OrdinalIgnoreCase))
                {
                    // Spec-canonical name per XBT.html Section 1.2.
                    engine = arg["-EngineRoot=".Length..];
                }
                else if (arg.StartsWith("-Engine=", StringComparison.OrdinalIgnoreCase))
                {
                    // Legacy alias preserved for backwards compatibility.
                    engine = arg["-Engine=".Length..];
                }
                else if (arg.StartsWith("-Studio=", StringComparison.OrdinalIgnoreCase))
                {
                    studio = arg["-Studio=".Length..];
                }
                else if (arg.StartsWith("-Project=", StringComparison.OrdinalIgnoreCase))
                {
                    project = arg["-Project=".Length..];
                }
                else
                {
                    throw new XBTException($"Unknown argument '{arg}'.", exitCode: 10);
                }
            }

            return new ValidateOptions
            {
                EngineRoot = engine,
                StudioRoot = studio,
                ProjectRoot = project,
            };
        }
    }
}
