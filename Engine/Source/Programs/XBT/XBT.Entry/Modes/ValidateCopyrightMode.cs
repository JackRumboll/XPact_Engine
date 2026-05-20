// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.ActionGraph.Actions;
using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.Entry;

/// <summary>
/// Audit fix C11: <c>validate-copyright</c> mode. Walks every authored
/// <c>.cs</c>/<c>.cpp</c>/<c>.h</c>/etc. under the repo root and verifies
/// each carries the canonical Simgenics copyright header on its first
/// non-empty line. The validator uses the same file-extension matrix
/// and skip rules as <see cref="ValidateCopyrightAction"/>'s in-graph
/// counterpart -- so the standalone CLI mode and the action-graph node
/// agree on what they validate.
/// </summary>
/// <remarks>
/// <para>
/// CLI surface (spec-canonical per XBT.html Section 1.2):
/// </para>
/// <list type="bullet">
///   <item><c>-Paths=&lt;p1&gt;[,&lt;p2&gt;,...]</c> (optional, repeatable) --
///   one or more root directories to scan. Comma-separated within a flag
///   and the flag itself may appear multiple times; both forms are
///   accumulated. When absent the validator falls back to the engine
///   root discovered via the usual walk-up logic.</item>
///   <item><c>-Exclude=&lt;p1&gt;[,&lt;p2&gt;,...]</c> (optional, repeatable) --
///   one or more paths to exclude from the scan (e.g.
///   <c>Engine/Source/ThirdParty/</c>). A file is excluded if its full
///   path begins with any of the exclude entries (after both paths are
///   normalised to forward slashes and absolute).</item>
///   <item><c>-Root=&lt;path&gt;</c> (legacy alias, optional) -- single
///   root directory. Equivalent to <c>-Paths=&lt;path&gt;</c>. Kept for
///   backwards compatibility with R4-M6 callers.</item>
/// </list>
/// <para>
/// Exit codes: 0 on success; 40 (<c>CopyrightHeaderMissing</c>) on any
/// failure with the offending paths printed; 10 on CLI argument error.
/// The exit code matches <see cref="Manifest.ContractSurface.ExitCodes"/>
/// code 40 per Toolchain Contract Rev 13 Section 13.1.
/// </para>
/// </remarks>
[XBTMode("validate-copyright")]
public sealed class ValidateCopyrightMode : IToolMode<ValidateCopyrightMode>
{
    public static string Name => "validate-copyright";

    public static string Description =>
        "Walk authored source files and verify the canonical copyright header.";

    public Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        cancellationToken.ThrowIfCancellationRequestedWithDiagnostic("ValidateCopyrightMode.ExecuteAsync");

        try
        {
            ValidateCopyrightOptions options = ValidateCopyrightOptions.Parse(args);
            IReadOnlyList<string> roots = options.Paths.Count == 0
                ? new[] { DiscoverEngineRoot() }
                : options.Paths;
            IReadOnlyList<string> excludes = options.Excludes;

            List<string> failures = new();
            int filesChecked = 0;

            foreach (string root in roots)
            {
                foreach (string path in EnumerateAuthoredFiles(root, excludes))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    filesChecked++;

                    ValidationOutcome outcome = ValidateCopyrightAction.ValidateOne(path);
                    if (!outcome.HasHeader)
                    {
                        failures.Add(path);
                    }
                }
            }

            if (failures.Count == 0)
            {
                Logger.Info(
                    $"validate-copyright: {filesChecked} file(s) checked; all carry the canonical header.",
                    new DiagnosticContext { Action = "validate-copyright" });
                return Task.FromResult(0);
            }

            StringBuilder sb = new();
            sb.AppendLine($"validate-copyright: {failures.Count} file(s) missing the canonical header:");
            foreach (string path in failures)
            {
                sb.AppendLine("  " + path);
            }
            Logger.Error(
                sb.ToString().TrimEnd(),
                exitCode: ValidateCopyrightAction.MissingHeaderExitCode,
                new DiagnosticContext { Action = "validate-copyright" });
            return Task.FromResult(ValidateCopyrightAction.MissingHeaderExitCode);
        }
        catch (XBTException ex)
        {
            Logger.Error(ex.Message, exitCode: ex.ExitCode);
            return Task.FromResult(ex.ExitCode);
        }
    }

    private static IEnumerable<string> EnumerateAuthoredFiles(
        string root, IReadOnlyList<string> excludes)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }
        EnumerationOptions opts = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchType = MatchType.Simple,
        };

        // Pre-normalise excludes once. Each exclude is matched as a
        // path-prefix against the candidate file's full path after both
        // sides are normalised to forward slashes and absolute.
        string[] normalisedExcludes = excludes
            .Select(e => NormaliseForwardAbsolute(e, root))
            .Where(e => e.Length > 0)
            .ToArray();

        // Walk once; filter via ValidateCopyrightAction.ShouldValidate so
        // skip rules (ThirdParty, obj/, bin/, generated markers) stay in
        // lockstep with the in-graph action.
        foreach (string path in Directory.EnumerateFiles(root, "*", opts))
        {
            if (!ValidateCopyrightAction.ShouldValidate(path))
            {
                continue;
            }
            if (IsExcluded(path, normalisedExcludes))
            {
                continue;
            }
            yield return path;
        }
    }

    private static bool IsExcluded(string path, string[] normalisedExcludes)
    {
        if (normalisedExcludes.Length == 0)
        {
            return false;
        }
        string norm = path.Replace('\\', '/');
        foreach (string ex in normalisedExcludes)
        {
            if (norm.StartsWith(ex, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string NormaliseForwardAbsolute(string raw, string root)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length == 0) return string.Empty;
        // Resolve relative excludes against the containing root so a
        // caller can pass either "ThirdParty" or an absolute path.
        string absolute = Path.IsPathRooted(trimmed)
            ? Path.GetFullPath(trimmed)
            : Path.GetFullPath(Path.Combine(root, trimmed));
        return absolute.Replace('\\', '/');
    }

    private sealed record ValidateCopyrightOptions
    {
        public required IReadOnlyList<string> Paths { get; init; }
        public required IReadOnlyList<string> Excludes { get; init; }

        public static ValidateCopyrightOptions Parse(string[] args)
        {
            List<string> paths = new();
            List<string> excludes = new();

            foreach (string arg in args)
            {
                if (arg.StartsWith("-Paths=", StringComparison.OrdinalIgnoreCase))
                {
                    AddSplit(paths, arg["-Paths=".Length..]);
                }
                else if (arg.StartsWith("-Exclude=", StringComparison.OrdinalIgnoreCase))
                {
                    AddSplit(excludes, arg["-Exclude=".Length..]);
                }
                else if (arg.StartsWith("-Root=", StringComparison.OrdinalIgnoreCase))
                {
                    // Legacy alias preserved for backwards compatibility
                    // with R4-M6 callers that pass a single root.
                    AddSplit(paths, arg["-Root=".Length..]);
                }
                else
                {
                    throw new XBTException($"Unknown argument '{arg}'.", exitCode: 10);
                }
            }

            return new ValidateCopyrightOptions
            {
                Paths = paths,
                Excludes = excludes,
            };
        }

        private static void AddSplit(List<string> sink, string raw)
        {
            foreach (string piece in raw.Split(','))
            {
                string trimmed = piece.Trim();
                if (trimmed.Length > 0)
                {
                    sink.Add(trimmed);
                }
            }
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
                // Return the parent (so the validator walks Engine/ +
                // any sibling Studio/ / Project/ trees together).
                return cursor;
            }
            candidate = Path.Combine(cursor, "Engine.xengine");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(cursor) ?? cursor;
            }
            DirectoryInfo? parent = Directory.GetParent(cursor);
            if (parent is null)
            {
                break;
            }
            cursor = parent.FullName;
        }
        throw new XBTException(
            "Could not discover the repo root. Pass -Root=<path> or run xbt from inside the repo.",
            exitCode: 10);
    }
}
