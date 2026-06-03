// Copyright Simgenics. All Rights Reserved.

using System;
using System.Globalization;
using Simgenics.XPact.XIL2CPP.Core;

namespace Simgenics.XPact.XIL2CPP.Entry.Modes;

/// <summary>
/// Parsed CLI options for the module-targeting modes (Phase 6.a:
/// <c>transpile-module</c>) per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 15. Mirrors XHT.Entry's <c>ModuleModeOptions</c>
/// (<c>/Engine/Source/Programs/XHT/XHT.Entry/Modes/ModuleModeOptions.cs</c>).
/// </summary>
/// <remarks>
/// <para>
/// Required flags are validated by <see cref="Parse"/>; missing or
/// malformed values throw <see cref="CliArgumentException"/> which
/// <c>Program</c> maps to <see cref="ExitCodes.CliArgumentError"/> (10).
/// Unlike XHT (which declares its own <c>CliArgumentException</c> alongside
/// the options), XIL2CPP already owns a single
/// <see cref="CliArgumentException"/> in the
/// <c>Simgenics.XPact.XIL2CPP.Entry</c> namespace; this parser reuses it so
/// there is one CLI-error type tool-wide.
/// </para>
/// </remarks>
/// <param name="ManifestPath">Absolute path to XBT's <c>Manifest.json</c>.</param>
/// <param name="ManifestBinPath">Absolute path to the FBS sidecar (optional; null when not supplied).</param>
/// <param name="ModuleName">Module the mode targets.</param>
/// <param name="OutputDir">Output directory for the mode's writes (empty when not required by the mode).</param>
/// <param name="JsonFd">CLI <c>-JsonFd=N</c> value; null when not supplied.</param>
internal sealed record ModuleModeOptions(
    string ManifestPath,
    string? ManifestBinPath,
    string ModuleName,
    string OutputDir,
    int? JsonFd)
{
    /// <summary>
    /// Parse <paramref name="args"/> into a populated
    /// <see cref="ModuleModeOptions"/>. Recognised flags:
    /// <list type="bullet">
    ///   <item><description><c>-Manifest=&lt;path&gt;</c> (required).</description></item>
    ///   <item><description><c>-ManifestBin=&lt;path&gt;</c> (optional; FBS sidecar -- deferred per the JSON-only Phase 6.a reader).</description></item>
    ///   <item><description><c>-Module=&lt;name&gt;</c> (required).</description></item>
    ///   <item><description><c>-Out=&lt;dir&gt;</c> (required when <paramref name="requireOutput"/> is true).</description></item>
    ///   <item><description><c>-JsonFd=&lt;int&gt;</c> (optional).</description></item>
    /// </list>
    /// </summary>
    /// <param name="args">CLI arguments (after the mode name has been stripped). Must not be null.</param>
    /// <param name="requireOutput">When true, <c>-Out=</c> is required.</param>
    /// <returns>Parsed options.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="args"/> is null.</exception>
    /// <exception cref="CliArgumentException">On any missing required flag, unknown flag, or malformed value.</exception>
    public static ModuleModeOptions Parse(string[] args, bool requireOutput = true)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? manifestPath = null;
        string? manifestBinPath = null;
        string? moduleName = null;
        string? outputDir = null;
        int? jsonFd = null;

        foreach (string arg in args)
        {
            if (arg.StartsWith("-ManifestBin=", StringComparison.OrdinalIgnoreCase))
            {
                // Checked BEFORE -Manifest= so the longer prefix wins (a
                // bare StartsWith("-Manifest=") would also match
                // "-ManifestBin=").
                manifestBinPath = arg["-ManifestBin=".Length..];
            }
            else if (arg.StartsWith("-Manifest=", StringComparison.OrdinalIgnoreCase))
            {
                manifestPath = arg["-Manifest=".Length..];
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
            JsonFd: jsonFd);
    }
}
