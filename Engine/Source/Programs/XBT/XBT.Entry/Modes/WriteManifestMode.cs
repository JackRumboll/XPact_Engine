// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.Entry;

/// <summary>
/// Audit fix C11: <c>write-manifest</c> mode. Runs the same discovery +
/// configuration + manifest-emission path as <c>build</c> but skips the
/// action graph and executor pump. Useful for IDE integration:
/// "give me a manifest for this target" without paying the cost of a
/// full build.
/// </summary>
/// <remarks>
/// <para>
/// CLI surface (subset of <c>build</c>'s flags, spec-canonical per
/// XBT.html Section 1.2):
/// </para>
/// <list type="bullet">
///   <item><c>-Target=&lt;name&gt;</c> (required)</item>
///   <item><c>-Configuration=&lt;cfg&gt;</c> (optional, default <c>Development</c>)</item>
///   <item><c>-Platform=&lt;p&gt;</c> (optional, defaults to host)</item>
///   <item><c>-EngineRoot=&lt;path&gt;</c> (optional) / <c>-Engine=&lt;path&gt;</c> (legacy alias)</item>
///   <item><c>-Project=&lt;path&gt;</c> (optional)</item>
///   <item><c>-Studio=&lt;path&gt;</c> (optional)</item>
///   <item><c>-Architecture=&lt;arch&gt;</c> (optional)</item>
///   <item><c>-Out=&lt;dir&gt;</c> (optional, NEW Round-6 cleanup M2) --
///   custom output directory for the manifest pair. Defaults to
///   <c>Intermediate/Build/&lt;Target&gt;/&lt;Configuration&gt;/</c>
///   under the engine root. The directory is created if missing; both
///   <c>Manifest.json</c> and <c>Manifest.fbs.bin</c> are written into
///   it atomically.</item>
/// </list>
/// <para>
/// Exit codes: 0 on success; the same exit codes <c>build</c> emits for
/// discovery / validation failures.
/// </para>
/// </remarks>
[XBTMode("write-manifest")]
public sealed class WriteManifestMode : IToolMode<WriteManifestMode>
{
    public static string Name => "write-manifest";

    public static string Description =>
        "Emit Manifest.json + Manifest.fbs.bin for the named target without compiling.";

    public Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        cancellationToken.ThrowIfCancellationRequestedWithDiagnostic("WriteManifestMode.ExecuteAsync");

        try
        {
            // Extract -Out=<dir> first (BuildOptions.Parse does not know
            // about this flag), then hand the remaining args to the
            // BuildMode parser. -Out= may appear at most once.
            (string[] passthroughArgs, string? outDir) = ExtractOutDir(args);

            // Reuse BuildMode's option parser and add the ManifestOnly
            // flag. The parser rejects unknown args via exit 10; the
            // accept-and-warn block in BuildOptions.Parse covers the
            // forward-compat surface.
            BuildOptions baseOptions = BuildOptions.Parse(passthroughArgs);
            BuildOptions manifestOptions = baseOptions with
            {
                ManifestOnly = true,
                ManifestOutputDirectory = outDir,
            };

            BuildResult result = BuildMode.Run(manifestOptions, cancellationToken);
            if (result.Success)
            {
                Logger.Info(
                    $"write-manifest: succeeded for target '{baseOptions.TargetName}'.",
                    new DiagnosticContext { Action = "write-manifest" });
                return Task.FromResult(0);
            }
            return Task.FromResult(result.FirstFailingExitCode);
        }
        catch (BuildOptionsParseException ex)
        {
            Logger.Error(ex.Message, exitCode: ex.ExitCode);
            return Task.FromResult(ex.ExitCode);
        }
        catch (XBTException ex)
        {
            Logger.Error(ex.Message, exitCode: ex.ExitCode);
            return Task.FromResult(ex.ExitCode);
        }
        catch (OperationCanceledException)
        {
            Logger.Error("write-manifest cancelled.", exitCode: 130);
            return Task.FromResult(130);
        }
    }

    /// <summary>
    /// Strip a single <c>-Out=&lt;dir&gt;</c> entry out of the CLI args
    /// vector and return the value alongside the remaining args. The
    /// <c>BuildOptions.Parse</c> contract rejects unknown flags, so the
    /// <c>-Out=</c> flag must be consumed before delegation. Multiple
    /// <c>-Out=</c> occurrences raise <see cref="XBTException"/> with
    /// exit code 10 to mirror the rest of the CLI parser's behaviour on
    /// duplicate / malformed input.
    /// </summary>
    private static (string[] Passthrough, string? OutDir) ExtractOutDir(string[] args)
    {
        string? outDir = null;
        List<string> passthrough = new(args.Length);
        foreach (string arg in args)
        {
            if (arg.StartsWith("-Out=", StringComparison.OrdinalIgnoreCase))
            {
                if (outDir is not null)
                {
                    throw new XBTException(
                        "-Out=<dir> may only be specified once.",
                        exitCode: 10);
                }
                outDir = arg["-Out=".Length..];
                if (string.IsNullOrWhiteSpace(outDir))
                {
                    throw new XBTException(
                        "-Out= requires a non-empty directory path.",
                        exitCode: 10);
                }
            }
            else
            {
                passthrough.Add(arg);
            }
        }
        return (passthrough.ToArray(), outDir);
    }
}
