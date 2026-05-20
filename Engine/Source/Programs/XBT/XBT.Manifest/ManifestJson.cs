// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Simgenics.XPact.XBT.Manifest;

/// <summary>
/// Serialize / deserialize <see cref="Manifest"/> in the JSON form
/// defined by Toolchain Contract Rev 13 Section 10.2. The hardened
/// reader options per <c>/Documents/XBT.html</c> Section 8.4 are
/// enforced at the deserialization boundary so a malformed or
/// malicious manifest cannot DoS the build agent.
/// </summary>
public static class ManifestJson
{
    /// <summary>Maximum manifest size in bytes. Matches XBT.html Section 8.4.</summary>
    public const long MaxManifestBytes = 100L * 1024L * 1024L; // 100 MB

    /// <summary>Maximum string-field length. Matches XBT.html Section 8.4.</summary>
    public const int MaxStringField = 16 * 1024; // 16 KB

    /// <summary>Maximum array length. Matches XBT.html Section 8.4.</summary>
    public const int MaxArrayCount = 65536;

    /// <summary>Hardened JSON reader options for manifest parsing.</summary>
    private static readonly JsonReaderOptions s_defaultReaderOptions = new()
    {
        MaxDepth = 64,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    /// <summary>Serializer options shared between read and write.</summary>
    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions s_readOptions = new()
    {
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Render a manifest to its canonical JSON form. Output is
    /// indented, enum values are PascalCase strings, no field is
    /// elided -- the JSON is the audit-trail form and must round-trip
    /// every field of the C# record.
    /// </summary>
    public static string SerializeToJson(Manifest m)
    {
        ArgumentNullException.ThrowIfNull(m);
        SanitizeForOutput(m);
        return JsonSerializer.Serialize(m, s_writeOptions);
    }

    /// <summary>
    /// Serialize a manifest to disk at <paramref name="destinationPath"/>
    /// using the atomic temp-file + rename pattern documented in
    /// <c>/Documents/XBT.html</c> Section 6.4. The destination is written
    /// in UTF-8 without a byte-order mark; readers (XHT, XIL2CPP) consume
    /// raw UTF-8.
    /// </summary>
    /// <param name="manifest">The manifest to encode. Must not be null.</param>
    /// <param name="destinationPath">Absolute filesystem path of the output file.</param>
    /// <returns>The number of bytes written to disk.</returns>
    /// <exception cref="ArgumentNullException">
    /// If <paramref name="manifest"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// If <paramref name="destinationPath"/> is null, empty, or whitespace.
    /// </exception>
    public static long Serialize(Manifest manifest, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string json = SerializeToJson(manifest);
        // System.Text.Json emits LF, never CRLF -- the writer keeps the
        // bytes verbatim so the on-disk representation is byte-identical
        // across Win64 and Linux hosts (matches the reproducibility
        // envelope's stance on line endings).
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        AtomicWriteAllBytes(destinationPath, bytes);
        return bytes.LongLength;
    }

    /// <summary>
    /// Atomic write helper: write to a uniquely-named temp file in the
    /// destination directory, flush to disk, then rename over the target.
    /// The rename is atomic on every supported filesystem (NTFS, ext4,
    /// APFS).
    /// </summary>
    /// <remarks>
    /// Audit fix M12: <see cref="FileStream.Flush(bool)"/> with
    /// <c>flushToDisk = true</c> issues an fsync before the rename, so
    /// a power loss after rename cannot leave a zero-byte
    /// post-allocation hole in the destination. Without fsync the
    /// rename can complete while the data still sits in the OS page
    /// cache; a power loss in that window leaves a corrupt destination.
    /// </remarks>
    private static void AtomicWriteAllBytes(string destinationPath, byte[] bytes)
    {
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        string parent = string.IsNullOrEmpty(directory) ? "." : directory;
        // Temp-file naming: <basename>.tmp.<pid>.<guid> -- no timestamp
        // anywhere per Toolchain Contract Rev 13 Section 2.1 (footgun #1
        // preempt: timestamps banned from any artefact name engine-wide).
        // Mirrors ActionHistory.Save's pattern at ActionHistory.cs:336-339.
        int pid = Environment.ProcessId;
        string nonce = Guid.NewGuid().ToString("N");
        string baseName = Path.GetFileName(destinationPath);
        string tempPath = Path.Combine(parent, $"{baseName}.tmp.{pid}.{nonce}");

        using (FileStream fs = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            // Audit fix M12: fsync before rename so the rename's atomic
            // window does not include an empty / partially-flushed file.
            fs.Flush(flushToDisk: true);
        }
        // Audit fix R6-C5: wrap File.Move in the AV-retry helper. Windows
        // Defender (and other AV products) transiently lock just-written
        // files for scanning, causing IOException sharing violations on
        // the rename. FileSystemOps.RetryOnTransientIOException retries
        // on the documented backoff schedule before surrendering.
        Simgenics.XPact.XBT.Core.FileSystemOps.RetryOnTransientIOException(
            () => File.Move(tempPath, destinationPath, overwrite: true));
    }

    /// <summary>
    /// Parse a manifest from JSON bytes. The reader options are
    /// hardened at <see cref="JsonReaderOptions"/> level
    /// (<see cref="JsonReaderOptions.MaxDepth"/>, no comments, no
    /// trailing commas) and the post-parse pass validates string-field
    /// and array-count limits per XBT.html Section 8.4.
    /// </summary>
    /// <param name="json">UTF-8 encoded JSON bytes. Caller is responsible for size cap.</param>
    /// <param name="readerOptions">
    /// Override the default reader options. Default values:
    /// <c>MaxDepth = 64</c>, <c>AllowTrailingCommas = false</c>,
    /// <c>CommentHandling = Disallow</c>.
    /// </param>
    public static Manifest DeserializeFromJson(
        ReadOnlySpan<byte> json,
        JsonReaderOptions? readerOptions = null)
    {
        if (json.Length == 0)
        {
            throw new ManifestMalformedException("Manifest payload is empty.");
        }

        if (json.Length > MaxManifestBytes)
        {
            throw new ManifestMalformedException(
                $"Manifest payload exceeds {MaxManifestBytes} bytes ({json.Length} bytes received).");
        }

        JsonReaderOptions effectiveReader = readerOptions ?? s_defaultReaderOptions;
        // Verify the JSON is parsable under the hardened reader before
        // handing it to the binder. JsonSerializer accepts a relaxed
        // subset by default; we re-tokenize here to enforce depth + no
        // comments + no trailing commas explicitly.
        Utf8JsonReader reader = new(json, effectiveReader);
        while (reader.Read())
        {
            // Drain. Reader throws on protocol violations -- the
            // hardened options reject what we don't allow.
        }

        Manifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(json, s_readOptions);
        }
        catch (JsonException ex)
        {
            throw new ManifestMalformedException("Manifest JSON failed to deserialize.", ex);
        }

        if (manifest is null)
        {
            throw new ManifestMalformedException("Manifest JSON deserialized to null.");
        }

        ValidateLimits(manifest);
        return manifest;
    }

    /// <summary>
    /// Convenience overload reading from a string (the JSON is
    /// transcoded to UTF-8 once at the boundary).
    /// </summary>
    public static Manifest DeserializeFromJson(string json, JsonReaderOptions? readerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        return DeserializeFromJson(Encoding.UTF8.GetBytes(json), readerOptions);
    }

    private static void SanitizeForOutput(Manifest m)
    {
        // The serialize path is internal to XBT so the writer is
        // expected to obey limits by construction. We still validate
        // here so any future code-path that synthesizes a Manifest
        // from user-controlled input cannot smuggle oversized strings
        // through.
        ValidateLimits(m);
    }

    private static void ValidateLimits(Manifest m)
    {
        CheckString(m.ContractVersion, nameof(m.ContractVersion));
        CheckString(m.EngineVersion, nameof(m.EngineVersion));
        CheckString(m.RootLocalPath, nameof(m.RootLocalPath));
        CheckOptionalString(m.ExternalDependenciesFile, nameof(m.ExternalDependenciesFile));
        // Per Toolchain Contract Rev 13 Section 10.2: per-target fields
        // live under the nested Target object so they mirror the FBS
        // TargetInfo table.
        if (m.Target is null)
        {
            throw new ManifestMalformedException($"{nameof(m.Target)} is null (required).");
        }
        CheckString(m.Target.Name, $"{nameof(m.Target)}.{nameof(m.Target.Name)}");
        // Audit fix C1/C10: ABI envelope fields.
        CheckString(m.Target.Architecture, $"{nameof(m.Target)}.{nameof(m.Target.Architecture)}");
        CheckString(m.Target.GCRootABI, $"{nameof(m.Target)}.{nameof(m.Target.GCRootABI)}");
        CheckString(m.Target.ExceptionABI, $"{nameof(m.Target)}.{nameof(m.Target.ExceptionABI)}");
        CheckString(m.Target.ManglingScheme, $"{nameof(m.Target)}.{nameof(m.Target.ManglingScheme)}");

        if (m.Modules.Count > MaxArrayCount)
        {
            throw new ManifestMalformedException(
                $"Manifest.Modules array exceeds {MaxArrayCount} entries ({m.Modules.Count}).");
        }

        foreach (Module mod in m.Modules)
        {
            CheckString(mod.Name, $"Module[{mod.Name}].Name");
            CheckString(mod.BaseDirectory, $"Module[{mod.Name}].BaseDirectory");
            CheckString(mod.GeneratedCPPFilenameBase, $"Module[{mod.Name}].GeneratedCPPFilenameBase");
            CheckString(mod.EngineVersionCompat, $"Module[{mod.Name}].EngineVersionCompat");
            CheckOptionalString(mod.DeprecationMessage, $"Module[{mod.Name}].DeprecationMessage");
            CheckOptionalString(mod.MinimumToolchainVersion, $"Module[{mod.Name}].MinimumToolchainVersion");

            CheckList(mod.SourceFiles, $"Module[{mod.Name}].SourceFiles");
            CheckStringList(mod.PublicHeaders, $"Module[{mod.Name}].PublicHeaders");
            CheckStringList(mod.PrivateHeaders, $"Module[{mod.Name}].PrivateHeaders");
            CheckStringList(mod.InternalHeaders, $"Module[{mod.Name}].InternalHeaders");
            CheckStringList(mod.CSharpSources, $"Module[{mod.Name}].CSharpSources");
            CheckStringList(mod.IncludePaths, $"Module[{mod.Name}].IncludePaths");
            CheckStringList(mod.PublicDefines, $"Module[{mod.Name}].PublicDefines");
            CheckList(mod.ModuleDependencies, $"Module[{mod.Name}].ModuleDependencies");

            foreach (SourceFile sf in mod.SourceFiles)
            {
                CheckString(sf.RelativePath, $"Module[{mod.Name}].SourceFile.RelativePath");
            }

            foreach (ModuleDep dep in mod.ModuleDependencies)
            {
                CheckString(dep.Name, $"Module[{mod.Name}].Dep.Name");
            }
        }
    }

    private static void CheckString(string value, string context)
    {
        if (value is null)
        {
            throw new ManifestMalformedException($"{context} is null (required).");
        }
        if (value.Length > MaxStringField)
        {
            throw new ManifestMalformedException(
                $"{context} exceeds {MaxStringField} chars ({value.Length}).");
        }
    }

    private static void CheckOptionalString(string? value, string context)
    {
        if (value is null)
        {
            return;
        }
        if (value.Length > MaxStringField)
        {
            throw new ManifestMalformedException(
                $"{context} exceeds {MaxStringField} chars ({value.Length}).");
        }
    }

    private static void CheckStringList(IReadOnlyList<string> list, string context)
    {
        if (list is null)
        {
            throw new ManifestMalformedException($"{context} is null (required).");
        }
        if (list.Count > MaxArrayCount)
        {
            throw new ManifestMalformedException(
                $"{context} exceeds {MaxArrayCount} entries ({list.Count}).");
        }
        foreach (string s in list)
        {
            CheckString(s, $"{context}[]");
        }
    }

    private static void CheckList<T>(IReadOnlyList<T> list, string context)
    {
        if (list is null)
        {
            throw new ManifestMalformedException($"{context} is null (required).");
        }
        if (list.Count > MaxArrayCount)
        {
            throw new ManifestMalformedException(
                $"{context} exceeds {MaxArrayCount} entries ({list.Count}).");
        }
    }
}

/// <summary>
/// Thrown when a manifest payload fails validation. XBT.Entry maps this
/// to exit code 50 ("manifest malformed / write failure") per Toolchain
/// Contract Rev 13 Section 13.
/// </summary>
public sealed class ManifestMalformedException : Exception
{
    public ManifestMalformedException(string message)
        : base(message)
    {
    }

    public ManifestMalformedException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
