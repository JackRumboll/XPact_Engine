// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using Simgenics.XPact.XHT.Core;

namespace Simgenics.XPact.XHT.Entry.Modes;

/// <summary>
/// Parsed CLI options shared by the module-targeting modes
/// (<c>parse-module</c>, <c>emit-module</c>, <c>validate-only</c>,
/// <c>dump-ast</c>, <c>query-symbols</c>) per
/// <c>/Documents/XHT.html</c> Rev 8 Section 1.2.
/// </summary>
/// <remarks>
/// <para>
/// Required flags are validated by <see cref="Parse"/>; missing or
/// malformed values throw <see cref="CliArgumentException"/> which
/// <c>Program</c> maps to <see cref="ExitCodes.CliArgumentError"/> (10).
/// Per <c>/Documents/XHT.html</c> Rev 8 Section 1.3.
/// </para>
/// </remarks>
/// <param name="ManifestPath">Absolute path to XBT's <c>Manifest.json</c>.</param>
/// <param name="ManifestBinPath">Absolute path to the FBS sidecar (optional; null when not supplied).</param>
/// <param name="ModuleName">Module the mode targets.</param>
/// <param name="OutputDir">Output directory for the mode's writes.</param>
/// <param name="JsonFd">CLI <c>-JsonFd=N</c> value; null when not supplied.</param>
/// <param name="NoMutexWait">True when <c>-NoMutexWait</c> was supplied per Section 11.6.</param>
internal sealed record ModuleModeOptions(
    string ManifestPath,
    string? ManifestBinPath,
    string ModuleName,
    string OutputDir,
    int? JsonFd,
    bool NoMutexWait)
{
    /// <summary>
    /// Parse <paramref name="args"/> into a populated
    /// <see cref="ModuleModeOptions"/>. Recognised flags:
    /// <list type="bullet">
    ///   <item><description><c>-Manifest=&lt;path&gt;</c> (required).</description></item>
    ///   <item><description><c>-ManifestBin=&lt;path&gt;</c> (optional; Phase 1c+).</description></item>
    ///   <item><description><c>-Module=&lt;name&gt;</c> (required).</description></item>
    ///   <item><description><c>-Out=&lt;dir&gt;</c> (required).</description></item>
    ///   <item><description><c>-JsonFd=&lt;int&gt;</c> (optional).</description></item>
    ///   <item><description><c>-NoMutexWait</c> (optional flag; Phase 1b accepted but the mutex itself ships in Phase 1c+).</description></item>
    /// </list>
    /// </summary>
    /// <param name="args">CLI arguments (after the mode name has been stripped).</param>
    /// <param name="requireOutput">When true, <c>-Out=</c> is required (parse-module / emit-module). When false, the mode does not write outputs (validate-only).</param>
    /// <returns>Parsed options.</returns>
    /// <exception cref="CliArgumentException">On any missing required flag or malformed value.</exception>
    public static ModuleModeOptions Parse(string[] args, bool requireOutput = true)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? manifestPath = null;
        string? manifestBinPath = null;
        string? moduleName = null;
        string? outputDir = null;
        int? jsonFd = null;
        bool noMutexWait = false;

        foreach (string arg in args)
        {
            if (arg.StartsWith("-Manifest=", StringComparison.OrdinalIgnoreCase)
                && !arg.StartsWith("-ManifestBin=", StringComparison.OrdinalIgnoreCase))
            {
                manifestPath = arg["-Manifest=".Length..];
            }
            else if (arg.StartsWith("-ManifestBin=", StringComparison.OrdinalIgnoreCase))
            {
                manifestBinPath = arg["-ManifestBin=".Length..];
            }
            else if (arg.StartsWith("-Module=", StringComparison.OrdinalIgnoreCase))
            {
                moduleName = arg["-Module=".Length..];
            }
            else if (arg.StartsWith("-Out=", StringComparison.OrdinalIgnoreCase))
            {
                outputDir = arg["-Out=".Length..];
            }
            else if (arg.StartsWith("-JsonFd=", StringComparison.OrdinalIgnoreCase))
            {
                string raw = arg["-JsonFd=".Length..];
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fd) || fd < 0)
                {
                    throw new CliArgumentException(
                        $"Invalid -JsonFd value '{raw}'. Expected a non-negative integer (0 disables).");
                }
                jsonFd = fd;
            }
            else if (arg.Equals("-NoMutexWait", StringComparison.OrdinalIgnoreCase))
            {
                noMutexWait = true;
            }
            else
            {
                throw new CliArgumentException($"Unknown argument '{arg}'.");
            }
        }

        if (string.IsNullOrEmpty(manifestPath))
        {
            throw new CliArgumentException("-Manifest=<path> is required.");
        }
        if (string.IsNullOrEmpty(moduleName))
        {
            throw new CliArgumentException("-Module=<name> is required.");
        }
        if (requireOutput && string.IsNullOrEmpty(outputDir))
        {
            throw new CliArgumentException("-Out=<dir> is required.");
        }

        return new ModuleModeOptions(
            ManifestPath: manifestPath,
            ManifestBinPath: string.IsNullOrEmpty(manifestBinPath) ? null : manifestBinPath,
            ModuleName: moduleName,
            OutputDir: outputDir ?? string.Empty,
            JsonFd: jsonFd,
            NoMutexWait: noMutexWait);
    }
}

/// <summary>
/// Thrown by <see cref="ModuleModeOptions.Parse"/> on malformed CLI input.
/// Caller maps to <see cref="ExitCodes.CliArgumentError"/> (10).
/// </summary>
internal sealed class CliArgumentException : Exception
{
    /// <summary>Construct the exception with a diagnostic message.</summary>
    /// <param name="message">Human-readable diagnostic text.</param>
    public CliArgumentException(string message) : base(message) { }
}
