// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.Entry;

/// <summary>
/// CLI wrapper mode for invoking the XHT (XPact Header Tool) subprocess
/// directly through XBT's discipline. Per <c>/Documents/XBT.html</c>
/// Rev 10 Section 1.1 + Section 9.1 + <c>/Documents/XHT.html</c> Rev 5
/// Section 9 + <c>/Documents/XToolchainContract.html</c> Rev 13.6
/// Section 10.1 step 3.
/// </summary>
/// <remarks>
/// <para>
/// <b>CLI surface</b> (spec-canonical):
/// </para>
/// <list type="bullet">
///   <item><c>-Manifest=&lt;path&gt;</c> (required) -- absolute path to the
///   XBT-emitted <c>Manifest.json</c> XHT reads.</item>
///   <item><c>-Module=&lt;name&gt;</c> (required) -- the module name to
///   process. XHT pulls the matching record from the manifest's
///   <c>Modules</c> array.</item>
///   <item><c>-Out=&lt;dir&gt;</c> (required) -- output directory for the
///   per-module emit (<c>.gen.h</c> / <c>.gen.cpp</c> /
///   <c>.init.gen.cpp</c> / <c>.gen.manifest</c>).</item>
///   <item><c>-XhtExe=&lt;path&gt;</c> (optional) -- override the XHT
///   executable path. Phase 1 default is
///   <c>&lt;EngineRoot&gt;/Binaries/&lt;Platform&gt;/xht.exe</c> on Win64
///   and <c>xht</c> on Linux / macOS, derived from the manifest's
///   <c>RootLocalPath</c>.</item>
///   <item><c>-Strict=true|false</c> (optional, default <c>true</c>) --
///   passed through to XHT.</item>
///   <item><c>-JsonFd=&lt;int&gt;</c> (optional) -- diagnostic JSON channel
///   file descriptor; passed through to XHT.</item>
///   <item><c>-Mode=parse-module|emit-module</c> (optional, default
///   <c>emit-module</c>) -- which XHT mode to invoke. The mode value is
///   forwarded as XHT's first positional argument.</item>
/// </list>
/// <para>
/// <b>Job Object discipline.</b> A <see cref="WindowsJobObject"/> wraps
/// the spawned XHT process so a SIGINT / taskkill on XBT cascades to
/// XHT. Mirrors the discipline in
/// <see cref="Simgenics.XPact.XBT.ActionGraph.ParallelExecutor"/>. On
/// non-Windows the wrapper is a no-op stub.
/// </para>
/// <para>
/// <b>Exit-code mapping</b> (Toolchain Contract Rev 13 Section 13.3): XHT
/// exits with codes <c>0</c> (success), <c>50</c> (manifest malformed),
/// <c>62</c> (XHT internal failure), or <c>130</c> (cancelled). XBT
/// translates the child exit to <c>60</c> (<c>XhtSubprocessFailure</c>)
/// at the aggregation layer when wiring XHT into a build action graph;
/// when invoked through this CLI wrapper, the child exit code passes
/// through verbatim so IDE / CI consumers see the canonical XHT exit
/// surface. The wrapper itself emits <c>10</c> for missing / malformed
/// args and <c>24</c> for "XHT executable not found" (closest-fit code
/// per Toolchain Contract Section 13.1 entry 24
/// <c>"PluginNotFound"</c> -- reused for "tool not found" pending a
/// dedicated mnemonic).
/// </para>
/// </remarks>
[XBTMode("run-xht")]
public sealed class RunXHTMode : IToolMode<RunXHTMode>
{
    public static string Name => "run-xht";

    public static string Description =>
        "Invoke XHT (XPact Header Tool) on a manifest+module pair through XBT's discipline.";

    /// <summary>
    /// Toolchain Contract Section 13 exit code for missing / malformed
    /// CLI arguments.
    /// </summary>
    public const int CliArgumentExitCode = 10;

    /// <summary>
    /// Toolchain Contract Section 13.1 exit code for "XHT executable not
    /// found" -- closest-fit mnemonic <c>PluginNotFound</c> per
    /// <see cref="Manifest.ContractSurface.ExitCodes"/>. Preserved on the
    /// type so the smoke test asserts against a single source of truth;
    /// Phase 2 may introduce a dedicated <c>ToolNotFound</c> mnemonic at
    /// which point this constant flips.
    /// </summary>
    public const int ToolNotFoundExitCode = 24;

    /// <summary>
    /// Subprocess wait-poll cadence; matches
    /// <see cref="Simgenics.XPact.XBT.ActionGraph.ProcessActionRunner.WaitForExitPollMs"/>.
    /// </summary>
    private const int WaitForExitPollMs = 250;

    public Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        cancellationToken.ThrowIfCancellationRequestedWithDiagnostic("RunXHTMode.ExecuteAsync");

        try
        {
            RunXHTOptions options = RunXHTOptions.Parse(args);
            return Task.FromResult(RunInternal(options, cancellationToken));
        }
        catch (RunXHTOptionsParseException ex)
        {
            Logger.Error(ex.Message, exitCode: ex.ExitCode,
                new DiagnosticContext { Action = "run-xht" });
            return Task.FromResult(ex.ExitCode);
        }
        catch (XBTException ex)
        {
            Logger.Error(ex.Message, exitCode: ex.ExitCode,
                new DiagnosticContext { Action = "run-xht" });
            return Task.FromResult(ex.ExitCode);
        }
        catch (OperationCanceledException)
        {
            Logger.Error("run-xht cancelled.", exitCode: 130,
                new DiagnosticContext { Action = "run-xht" });
            return Task.FromResult(130);
        }
    }

    /// <summary>
    /// Resolve the XHT executable path: if the caller supplied
    /// <c>-XhtExe=</c>, use it; otherwise derive from the manifest's
    /// <c>RootLocalPath</c> + the host platform's binaries directory.
    /// </summary>
    /// <remarks>
    /// Phase 1 default layout per <c>/Documents/XBT.html</c> Section 18
    /// + Toolchain Contract Section 10.1: XBT and XHT both live under
    /// <c>&lt;EngineRoot&gt;/Binaries/&lt;Platform&gt;/</c>. The Phase 2
    /// XPactBuildAccelerator will resolve through a content-addressable
    /// lookup; the override CLI flag remains the escape hatch.
    /// </remarks>
    internal static string ResolveXhtExecutable(string? overridePath, string manifestPath)
    {
        if (!string.IsNullOrEmpty(overridePath))
        {
            if (!File.Exists(overridePath))
            {
                throw new XBTException(
                    $"-XhtExe=\"{overridePath}\" does not point to an existing file.",
                    exitCode: ToolNotFoundExitCode);
            }
            return Path.GetFullPath(overridePath);
        }

        // Derive engine root from the manifest's directory. The
        // canonical layout per the contract is
        // <EngineRoot>/Intermediate/Build/<Target>/<Configuration>/Manifest.json,
        // so walk up four parents to land on <EngineRoot>.
        string manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))
            ?? throw new XBTException(
                $"Could not determine the manifest's directory from -Manifest=\"{manifestPath}\".",
                exitCode: CliArgumentExitCode);

        string? cursor = manifestDir;
        for (int i = 0; i < 4 && cursor is not null; i++)
        {
            cursor = Path.GetDirectoryName(cursor);
        }
        if (cursor is null)
        {
            throw new XBTException(
                $"Could not derive engine root from -Manifest=\"{manifestPath}\"; pass -XhtExe=<path> explicitly.",
                exitCode: ToolNotFoundExitCode);
        }

        string platformDir = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "Win64"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "Mac" : "Linux";
        string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "xht.exe"
            : "xht";

        string candidate = Path.Combine(cursor, "Binaries", platformDir, exeName);
        if (!File.Exists(candidate))
        {
            throw new XBTException(
                $"XHT executable not found at default location '{candidate}'. "
                + $"Pass -XhtExe=<path> to override.",
                exitCode: ToolNotFoundExitCode);
        }
        return candidate;
    }

    private static int RunInternal(RunXHTOptions options, CancellationToken cancellationToken)
    {
        if (!File.Exists(options.ManifestPath))
        {
            throw new XBTException(
                $"-Manifest=\"{options.ManifestPath}\" does not point to an existing file.",
                exitCode: CliArgumentExitCode);
        }

        string xhtExe = ResolveXhtExecutable(options.XhtExeOverride, options.ManifestPath);

        // Ensure the output directory exists; XHT writes its outputs
        // there (and the action graph's pre-discovery already expects it).
        string outDir = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outDir);

        // Build the XHT command line. Mode-as-positional follows the
        // XHT public CLI (parse-module / emit-module / validate-only /
        // dump-ast / help / version).
        List<string> xhtArgs = new()
        {
            options.Mode,
            $"-Manifest={Path.GetFullPath(options.ManifestPath)}",
            $"-Module={options.ModuleName}",
            $"-Out={outDir}",
        };
        if (options.Strict is bool strict)
        {
            xhtArgs.Add($"-Strict={(strict ? "true" : "false")}");
        }
        if (options.JsonFd is int jsonFd)
        {
            xhtArgs.Add($"-JsonFd={jsonFd}");
        }
        if (options.NoMutexWait)
        {
            xhtArgs.Add("-NoMutexWait");
        }

        Logger.Info(
            $"run-xht: invoking {Path.GetFileName(xhtExe)} {options.Mode} -Module={options.ModuleName}",
            new DiagnosticContext { Action = "run-xht", Module = options.ModuleName });

        // Job object: mirror the ParallelExecutor pattern so a SIGINT
        // on XBT cascades to the XHT subprocess.
        using WindowsJobObject jobObject = new();
        jobObject.Create();

        ProcessStartInfo psi = new()
        {
            FileName = xhtExe,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string arg in xhtArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process process = new() { StartInfo = psi };
        process.OutputDataReceived += static (_, e) =>
        {
            if (e.Data is not null)
            {
                Console.Out.WriteLine(e.Data);
            }
        };
        process.ErrorDataReceived += static (_, e) =>
        {
            if (e.Data is not null)
            {
                Console.Error.WriteLine(e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                Logger.Error(
                    $"Process.Start returned false for {xhtExe}.",
                    exitCode: ToolNotFoundExitCode,
                    new DiagnosticContext { Action = "run-xht" });
                return ToolNotFoundExitCode;
            }
            jobObject.AssignProcess(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            while (!process.WaitForExit(WaitForExitPollMs))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Best-effort.
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            // Drain the async output handlers.
            process.WaitForExit();

            int exitCode = process.ExitCode;
            if (exitCode == 0)
            {
                Logger.Info(
                    $"run-xht: XHT succeeded for module '{options.ModuleName}'.",
                    new DiagnosticContext { Action = "run-xht", Module = options.ModuleName });
                return 0;
            }

            // Propagate the XHT exit code verbatim so consumers see the
            // canonical XHT exit surface (60 / 62 / 50 / 130 per Contract
            // Section 13).
            Logger.Error(
                $"run-xht: XHT exited with code {exitCode} for module '{options.ModuleName}'.",
                exitCode: exitCode,
                new DiagnosticContext { Action = "run-xht", Module = options.ModuleName });
            return exitCode;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Process.Start can throw Win32Exception when the file isn't
            // an executable or the OS denies launch. Treat as
            // "tool not found".
            Logger.Error(
                $"Failed to launch XHT from '{xhtExe}': {ex.Message}",
                exitCode: ToolNotFoundExitCode,
                new DiagnosticContext { Action = "run-xht" });
            return ToolNotFoundExitCode;
        }
    }
}

/// <summary>
/// Parsed CLI surface for <see cref="RunXHTMode"/>.
/// </summary>
internal sealed record RunXHTOptions(
    string ManifestPath,
    string ModuleName,
    string OutputDirectory,
    string Mode,
    string? XhtExeOverride,
    bool? Strict,
    int? JsonFd,
    bool NoMutexWait)
{
    /// <summary>Parse the CLI args vector into a strongly-typed options record.</summary>
    /// <exception cref="RunXHTOptionsParseException">
    /// Thrown for any unknown / malformed / missing-required argument.
    /// Carries exit code 10 (<c>CliArgumentError</c>).
    /// </exception>
    public static RunXHTOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? manifest = null;
        string? module = null;
        string? outDir = null;
        string? xhtExe = null;
        bool? strict = null;
        int? jsonFd = null;
        bool noMutexWait = false;
        string mode = "emit-module";

        foreach (string arg in args)
        {
            if (TryExtract(arg, "-Manifest=", out string val))
            {
                manifest = val;
            }
            else if (TryExtract(arg, "-Module=", out val))
            {
                module = val;
            }
            else if (TryExtract(arg, "-Out=", out val))
            {
                outDir = val;
            }
            else if (TryExtract(arg, "-XhtExe=", out val))
            {
                xhtExe = val;
            }
            else if (TryExtract(arg, "-Mode=", out val))
            {
                mode = val;
            }
            else if (TryExtract(arg, "-Strict=", out val))
            {
                if (!bool.TryParse(val, out bool b))
                {
                    throw new RunXHTOptionsParseException(
                        $"-Strict= requires 'true' or 'false'; got '{val}'.");
                }
                strict = b;
            }
            else if (TryExtract(arg, "-JsonFd=", out val))
            {
                if (!int.TryParse(val, out int fd))
                {
                    throw new RunXHTOptionsParseException(
                        $"-JsonFd= requires an integer; got '{val}'.");
                }
                jsonFd = fd;
            }
            else if (string.Equals(arg, "-NoMutexWait", StringComparison.OrdinalIgnoreCase))
            {
                noMutexWait = true;
            }
            else
            {
                throw new RunXHTOptionsParseException(
                    $"Unknown argument '{arg}'. Run 'xbt help run-xht' for the supported flags.");
            }
        }

        if (string.IsNullOrEmpty(manifest))
        {
            throw new RunXHTOptionsParseException(
                "Missing required argument -Manifest=<path>.");
        }
        if (string.IsNullOrEmpty(module))
        {
            throw new RunXHTOptionsParseException(
                "Missing required argument -Module=<name>.");
        }
        if (string.IsNullOrEmpty(outDir))
        {
            throw new RunXHTOptionsParseException(
                "Missing required argument -Out=<dir>.");
        }
        if (string.IsNullOrEmpty(mode))
        {
            throw new RunXHTOptionsParseException(
                "-Mode= requires a non-empty value (e.g. emit-module, parse-module).");
        }

        return new RunXHTOptions(
            ManifestPath: manifest!,
            ModuleName: module!,
            OutputDirectory: outDir!,
            Mode: mode,
            XhtExeOverride: xhtExe,
            Strict: strict,
            JsonFd: jsonFd,
            NoMutexWait: noMutexWait);
    }

    private static bool TryExtract(string arg, string prefix, out string value)
    {
        if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = arg[prefix.Length..];
            return true;
        }
        value = string.Empty;
        return false;
    }
}

/// <summary>
/// Argument-parse failure for <see cref="RunXHTMode"/>. Always carries
/// exit code 10 (<c>CliArgumentError</c>).
/// </summary>
internal sealed class RunXHTOptionsParseException : Exception
{
    public int ExitCode { get; } = 10;

    public RunXHTOptionsParseException(string message) : base(message) { }
}
