// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Blake3;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.ActionGraph.Actions;

/// <summary>
/// Validates that every authored source file in a build carries the
/// Simgenics copyright header on its first non-empty line. Maps to
/// Toolchain Contract Rev 13 Section 10.1 step 5 +
/// <c>/Documents/XBT.html</c> Rev 4 Section 11.
/// </summary>
/// <remarks>
/// <para>
/// <b>File-extension matrix.</b> Different source forms use different
/// comment markers and the validator handles them all uniformly: it
/// scans the file's first non-empty line for the literal text
/// <c>"Copyright Simgenics. All Rights Reserved."</c>. The leading
/// comment marker (<c>//</c>, <c>&lt;!--</c>, <c>#</c>, etc.) is
/// irrelevant to the substring match.
/// </para>
/// <para>
/// <b>Microsoft .sln special case.</b> A Visual Studio solution file's
/// first line is the format header
/// (<c>"Microsoft Visual Studio Solution File, Format Version XX"</c>);
/// the copyright header appears on line 2 as a <c>#</c> comment. The
/// validator scans the first three non-empty lines for <c>.sln</c> only.
/// </para>
/// <para>
/// <b>.xplugin / .xproject / .xengine special case.</b> These are JSON
/// descriptors that cannot legitimately carry a comment on their first
/// line (strict JSON parse, no comments). The required header is a
/// top-level <c>"Copyright"</c> string property.
/// </para>
/// <para>
/// <b>Skip rules</b> (per XBT.html Section 11 + 22.1):
/// </para>
/// <list type="bullet">
///   <item><c>/Engine/Source/ThirdParty/</c> -- carries upstream licensing.</item>
///   <item>Any path containing <c>obj/</c> or <c>bin/</c> segments -- generated MSBuild output.</item>
///   <item>Any path containing the FlatSharp marker <c>FlatSharp.generated.cs</c>.</item>
/// </list>
/// <para>
/// <b>Caching.</b> <see cref="bUseActionHistory"/> is true; the
/// <see cref="CommandVersion"/> hashes the union of all source files'
/// content plus the static "what we check" table (extension list,
/// expected literal). A file's content change invalidates the
/// validator's cached output; the validator re-runs only over the
/// changed files in a follow-up build because the per-file content
/// hash governs.
/// </para>
/// <para>
/// <b>Failure mode.</b> On any missing header, the action's runner
/// surfaces exit code <c>40</c> (Toolchain Contract Section 13
/// <c>CopyrightHeaderMissing</c>) and the diagnostic batches every
/// offender into one report so the developer can fix them in a single
/// pass.
/// </para>
/// </remarks>
public sealed class ValidateCopyrightAction : ActionBase
{
    /// <summary>The literal header text every authored file must contain.</summary>
    public const string ExpectedText = "Copyright Simgenics. All Rights Reserved.";

    /// <summary>Toolchain Contract Section 13 exit code for missing-header failures.</summary>
    public const int MissingHeaderExitCode = 40;

    /// <summary>
    /// The set of file extensions the validator considers authored source.
    /// Sorted alphabetically because <see cref="ComputeCommandVersion"/>
    /// hashes the table -- a deterministic order locks the cache key.
    /// </summary>
    /// <remarks>
    /// Order matters: this list participates in the action's cache key
    /// via <see cref="ComputeCommandVersion"/>. Changing the matrix
    /// (adding a new extension, retiring one) automatically busts every
    /// previous cache entry for the action.
    /// </remarks>
    public static readonly IReadOnlyList<string> ValidatedExtensions = new[]
    {
        ".Build.cs",
        ".Build.expr",
        ".Build.toml",
        ".cpp",
        ".cs",
        ".csproj",
        ".fbs",
        ".h",
        ".html",
        ".props",
        ".sln",
        ".targets",
        ".xengine",
        ".xplugin",
        ".xproject",
    };

    private static readonly HashSet<string> s_validatedExtensions = new(
        ValidatedExtensions,
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Path patterns whose subtrees are excluded entirely. Compared
    /// case-insensitively against the normalised forward-slash form of
    /// the file path so Windows and Linux paths match the same way.
    /// </summary>
    private static readonly string[] s_excludedPathPatterns = new[]
    {
        "/Engine/Source/ThirdParty/",
        "/obj/",
        "/bin/",
    };

    /// <summary>
    /// Substrings that mark a generated file regardless of where it
    /// lives. FlatSharp emits <c>*.FlatSharp.generated.cs</c> with the
    /// FlatSharp preamble, not Simgenics's.
    /// </summary>
    private static readonly string[] s_generatedFileMarkers = new[]
    {
        "FlatSharp.generated.cs",
    };

    private readonly IReadOnlyList<FileItem> _filesToScan;
    private readonly IReadOnlyList<FileItem> _producedItems;
    private readonly string _workingDirectory;
    private readonly BuildConfiguration _configuration;
    private readonly Platform _platform;
    private readonly FileItem? _markerFile;

    /// <inheritdoc/>
    public override XActionType ActionType => XActionType.ValidateCopyrightAction;

    /// <inheritdoc/>
    public override IReadOnlyList<FileItem> PrerequisiteItems => _filesToScan;

    /// <inheritdoc/>
    public override IReadOnlyList<FileItem> ProducedItems => _producedItems;

    /// <inheritdoc/>
    public override string WorkingDirectory => _workingDirectory;

    /// <inheritdoc/>
    public override string CommandDescription => "ValidateCopyright";

    /// <inheritdoc/>
    public override string StatusDescription =>
        _filesToScan.Count == 1
            ? Path.GetFileName(_filesToScan[0].FullPath)
            : $"{_filesToScan.Count} files";

    /// <inheritdoc/>
    public override BuildConfiguration Configuration => _configuration;

    /// <inheritdoc/>
    public override Platform Platform => _platform;

    /// <inheritdoc/>
    public override double Weight => 0.5;     // I/O-bound, low cost per file.

    /// <inheritdoc/>
    /// <remarks>
    /// True only when a <c>markerFilePath</c> was supplied at
    /// construction. Without a marker, the action has no
    /// <see cref="ProducedItems"/>; <see cref="ActionHistory"/> trivially
    /// considers no-produced-item actions up-to-date and would never
    /// re-run -- which defeats the validator's purpose. The marker file
    /// is the cache anchor.
    /// </remarks>
    public override bool bUseActionHistory => _markerFile is not null;

    /// <summary>Construct a copyright-validation action over a set of files.</summary>
    /// <param name="filesToScan">
    /// The full set of source files the build will compile or reference.
    /// The constructor filters out the excluded subtrees + generated
    /// files; the resulting (filtered) list is stored as the action's
    /// <see cref="IExternalAction.PrerequisiteItems"/> and sorted by
    /// <see cref="FileItem.FullPath"/> ordinal for the action-graph
    /// invariant.
    /// </param>
    /// <param name="workingDirectory">
    /// Absolute path the action runs in. Typically the repo root.
    /// </param>
    /// <param name="configuration">Build configuration tag for diagnostics.</param>
    /// <param name="platform">Target platform tag for diagnostics.</param>
    /// <param name="markerFilePath">
    /// Optional absolute path to a "validation succeeded" sentinel file.
    /// When non-null, the action writes a small marker on success which
    /// becomes the action's <see cref="IExternalAction.ProducedItems"/>
    /// for <see cref="ActionHistory"/>'s sake; that lets the cache hit on
    /// a second run with unchanged inputs (otherwise an action with no
    /// produced items appears up-to-date trivially and never runs). When
    /// null, the action runs unconditionally on every build -- useful in
    /// tests where the marker file would pollute scratch fixtures.
    /// </param>
    public ValidateCopyrightAction(
        IReadOnlyList<FileItem> filesToScan,
        string workingDirectory,
        BuildConfiguration configuration,
        Platform platform,
        string? markerFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(filesToScan);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        // Filter: keep only validated extensions; drop excluded subtrees
        // + generated markers.
        List<FileItem> filtered = new(filesToScan.Count);
        foreach (FileItem file in filesToScan)
        {
            if (ShouldValidate(file.FullPath))
            {
                filtered.Add(file);
            }
        }

        // Sort ordinal so the ExternalAction sort invariant holds even
        // though we are a hand-rolled IExternalAction implementation; the
        // hash-of-prerequisites invariant requires the same order across
        // builds.
        filtered.Sort(static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));

        // De-duplicate (defensive; the build's enumerator may yield the
        // same logical path through two roots).
        if (filtered.Count > 1)
        {
            List<FileItem> deduped = new(filtered.Count);
            string? lastPath = null;
            foreach (FileItem file in filtered)
            {
                if (!string.Equals(lastPath, file.FullPath, StringComparison.Ordinal))
                {
                    deduped.Add(file);
                    lastPath = file.FullPath;
                }
            }
            filtered = deduped;
        }

        _filesToScan = filtered;
        _workingDirectory = workingDirectory;
        _configuration = configuration;
        _platform = platform;

        if (!string.IsNullOrEmpty(markerFilePath))
        {
            _markerFile = FileItem.GetItemByPath(markerFilePath);
            _producedItems = new[] { _markerFile };
        }
        else
        {
            _markerFile = null;
            _producedItems = EmptyFileItems;
        }
    }

    /// <summary>
    /// Determine whether the validator should consider a given file.
    /// Public so the build orchestration can use the same filter when
    /// building the input set, avoiding the runtime cost of constructing
    /// <see cref="FileItem"/>s for files we'll drop anyway.
    /// </summary>
    public static bool ShouldValidate(string fullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);

        // Normalise to forward slashes for stable substring matches that
        // work on both Win64 and Linux paths.
        string normalised = fullPath.Replace('\\', '/');

        // Exclude generated subtrees.
        foreach (string pattern in s_excludedPathPatterns)
        {
            if (normalised.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // Exclude FlatSharp-generated files anywhere.
        foreach (string marker in s_generatedFileMarkers)
        {
            if (normalised.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // Compound suffix check: prefer the longest matching suffix so
        // ".Build.cs" wins over ".cs" for a file like "X.Build.cs".
        foreach (string ext in ValidatedExtensions)
        {
            if (normalised.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <inheritdoc/>
    protected override IoHash ComputeCommandVersion()
    {
        using Hasher hasher = Hasher.New();
        Span<byte> intBuffer = stackalloc byte[4];

        // 1. ActionType ordinal.
        BitConverter.TryWriteBytes(intBuffer, (int)ActionType);
        hasher.Update(intBuffer);

        // 2. The expected-text literal -- if a future contract revision
        //    changes the wording, the action's cache invalidates.
        UpdateUtf8(hasher, ExpectedText);

        // 3. The static "what we check" table. The matrix is sorted at
        //    declaration time so the hash is stable across runs.
        BitConverter.TryWriteBytes(intBuffer, ValidatedExtensions.Count);
        hasher.Update(intBuffer);
        foreach (string ext in ValidatedExtensions)
        {
            UpdateUtf8(hasher, ext);
        }

        BitConverter.TryWriteBytes(intBuffer, s_excludedPathPatterns.Length);
        hasher.Update(intBuffer);
        foreach (string pattern in s_excludedPathPatterns)
        {
            UpdateUtf8(hasher, pattern);
        }

        BitConverter.TryWriteBytes(intBuffer, s_generatedFileMarkers.Length);
        hasher.Update(intBuffer);
        foreach (string marker in s_generatedFileMarkers)
        {
            UpdateUtf8(hasher, marker);
        }

        // 4. The union of input file paths AND their content hashes.
        //    Content participation means changing a file's bytes (e.g.
        //    fixing the header) invalidates the cache so re-validation
        //    runs.
        BitConverter.TryWriteBytes(intBuffer, _filesToScan.Count);
        hasher.Update(intBuffer);
        foreach (FileItem file in _filesToScan)
        {
            UpdateUtf8(hasher, file.FullPath);
            if (File.Exists(file.FullPath))
            {
                IoHash content = file.ContentHash;
                Span<byte> contentBytes = stackalloc byte[IoHash.Length];
                content.CopyTo(contentBytes);
                hasher.Update(contentBytes);
            }
            else
            {
                // Missing file: a stable placeholder so the hash is
                // stable even when the file doesn't exist yet. Set the
                // byte to a discriminating value so the cache key for
                // "missing" never collides with "present-and-empty".
                Span<byte> missingMarker = stackalloc byte[IoHash.Length];
                missingMarker[0] = 0xFF;
                hasher.Update(missingMarker);
            }
        }

        Span<byte> digest = stackalloc byte[IoHash.Length];
        hasher.Finalize(digest);
        return new IoHash(digest);
    }

    /// <summary>
    /// Run the validator. Returns a <see cref="ValidationReport"/>
    /// listing every file missing the header. The caller decides whether
    /// to surface this as an exit-40 build failure or a warning.
    /// </summary>
    /// <param name="cancellationToken">
    /// Honoured at every file boundary; partial results are returned on
    /// cancellation.
    /// </param>
    public ValidationReport Run(CancellationToken cancellationToken = default)
    {
        List<string> failures = new();
        int filesChecked = 0;

        foreach (FileItem file in _filesToScan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(file.FullPath))
            {
                // Missing file is not a copyright issue; the build's
                // separate "file exists" check catches that. Skip.
                continue;
            }

            filesChecked++;
            ValidationOutcome outcome = ValidateOne(file.FullPath);
            if (outcome.HasHeader)
            {
                continue;
            }

            failures.Add(FormatFailure(file.FullPath, outcome.FirstLineSeen));
        }

        return new ValidationReport(filesChecked, failures);
    }

    /// <summary>
    /// Validate a single file. Public so the standalone
    /// <c>ValidateCopyright</c> CLI mode (XBT.html Section 11.3) can
    /// reuse the same code path without spinning up an action graph.
    /// </summary>
    public static ValidationOutcome ValidateOne(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        string ext = GetCanonicalExtension(filePath);

        // .xplugin / .xproject / .xengine: JSON descriptors with a
        // top-level "Copyright" field.
        if (IsXDescriptorExtension(ext))
        {
            return ValidateXDescriptor(filePath);
        }

        // .sln: first line is the VS format header; copyright is line 2.
        int linesToScan = string.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
        return ValidateFirstNonEmptyLines(filePath, linesToScan);
    }

    /// <summary>
    /// Compound-extension picker preferring the longest match so
    /// <c>X.Build.toml</c> picks <c>.Build.toml</c> and not <c>.toml</c>
    /// (the validator's matrix lists the compound form deliberately).
    /// </summary>
    private static string GetCanonicalExtension(string path)
    {
        string normalised = path.Replace('\\', '/');
        foreach (string ext in ValidatedExtensions)
        {
            if (normalised.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return ext;
            }
        }
        return Path.GetExtension(path);
    }

    private static bool IsXDescriptorExtension(string ext)
        => string.Equals(ext, ".xplugin", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ext, ".xproject", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ext, ".xengine", StringComparison.OrdinalIgnoreCase);

    private static ValidationOutcome ValidateFirstNonEmptyLines(string filePath, int maxLines)
    {
        string? firstSeen = null;
        int seen = 0;
        foreach (string line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            firstSeen ??= line;
            seen++;
            if (line.Contains(ExpectedText, StringComparison.Ordinal))
            {
                return new ValidationOutcome(HasHeader: true, FirstLineSeen: firstSeen);
            }
            if (seen >= maxLines)
            {
                break;
            }
        }
        return new ValidationOutcome(HasHeader: false, FirstLineSeen: firstSeen);
    }

    private static ValidationOutcome ValidateXDescriptor(string filePath)
    {
        // Inspect the JSON root object for a top-level "Copyright"
        // string. If parsing fails, treat as missing-header (the file
        // is malformed and a separate diagnostic from the descriptor
        // parser will fire too -- the copyright validator's job is
        // narrowly scoped).
        try
        {
            using FileStream stream = File.OpenRead(filePath);
            using JsonDocument doc = JsonDocument.Parse(stream);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("Copyright", out JsonElement copyright)
                && copyright.ValueKind == JsonValueKind.String)
            {
                string? text = copyright.GetString();
                if (text is not null && text.Contains(ExpectedText, StringComparison.Ordinal))
                {
                    return new ValidationOutcome(HasHeader: true, FirstLineSeen: text);
                }
                return new ValidationOutcome(HasHeader: false, FirstLineSeen: text);
            }
            return new ValidationOutcome(HasHeader: false, FirstLineSeen: null);
        }
        catch (Exception)
        {
            return new ValidationOutcome(HasHeader: false, FirstLineSeen: null);
        }
    }

    private static string FormatFailure(string filePath, string? actualFirstLine)
    {
        StringBuilder sb = new();
        sb.AppendLine($"  File: {filePath}");
        sb.AppendLine($"  Required: contains '{ExpectedText}' on the first non-empty line (line 2 for .sln; \"Copyright\" JSON field for .x* descriptors).");
        if (actualFirstLine is not null)
        {
            string truncated = actualFirstLine.Length > 160
                ? actualFirstLine.Substring(0, 160) + "..."
                : actualFirstLine;
            sb.Append($"  Actual:   {truncated}");
        }
        else
        {
            sb.Append("  Actual:   (file is empty or unreadable)");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Per-file outcome from <see cref="ValidateCopyrightAction.ValidateOne"/>.
/// </summary>
/// <param name="HasHeader">True iff the file carries the Simgenics header.</param>
/// <param name="FirstLineSeen">The first non-empty line / JSON field text we saw, for diagnostics.</param>
public readonly record struct ValidationOutcome(bool HasHeader, string? FirstLineSeen);

/// <summary>
/// Aggregate report from <see cref="ValidateCopyrightAction.Run"/>.
/// </summary>
/// <param name="FilesChecked">Total number of files inspected.</param>
/// <param name="Failures">
/// Pre-formatted, multi-line failure entries, one per offending file.
/// Empty list = success.
/// </param>
public sealed record ValidationReport(int FilesChecked, IReadOnlyList<string> Failures)
{
    /// <summary>True iff every file passed.</summary>
    public bool Success => Failures.Count == 0;
}
