// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using FlatSharp;

// Generated FlatSharp namespace per Manifest.fbs's `namespace XPact.Build.Manifest;`.
// Aliased so the binary types are clearly distinguishable from the JSON POCOs
// declared in this same C# namespace (Simgenics.XPact.XBT.Manifest). The
// global:: prefix avoids the inner namespace resolution snare where C# would
// otherwise try Simgenics.XPact.Build.Manifest.* first.
using FbsManifest        = global::XPact.Build.Manifest.Manifest;
using FbsTargetInfo      = global::XPact.Build.Manifest.TargetInfo;
using FbsModule          = global::XPact.Build.Manifest.Module;
using FbsModuleDep       = global::XPact.Build.Manifest.ModuleDep;
using FbsSourceFile      = global::XPact.Build.Manifest.SourceFile;
using FbsModuleTier      = global::XPact.Build.Manifest.ModuleTier;
using FbsModuleType      = global::XPact.Build.Manifest.ModuleType;
using FbsLanguages       = global::XPact.Build.Manifest.Languages;
using FbsSimdLevel       = global::XPact.Build.Manifest.SimdLevel;
using FbsConfiguration   = global::XPact.Build.Manifest.Configuration;
using FbsTargetType      = global::XPact.Build.Manifest.TargetType;
using FbsPlatform        = global::XPact.Build.Manifest.Platform;
using FbsStationRole     = global::XPact.Build.Manifest.StationRole;
using FbsPCHUsageMode    = global::XPact.Build.Manifest.PCHUsageMode;

namespace Simgenics.XPact.XBT.Manifest;

/// <summary>
/// Thrown when the manifest binary sidecar fails to parse, fails identifier
/// verification, or exceeds one of the verifier limits required by the
/// Toolchain Contract Rev 13 Section 10.2 / <c>/Documents/XBT.html</c>
/// Section 8.4.
/// </summary>
public sealed class ManifestException : Exception
{
    /// <summary>
    /// Section 13 of Toolchain Contract Rev 13: parse-safety violations and
    /// malformed-manifest cases map to exit code 50. Callers map this exception
    /// to that exit code at the process boundary.
    /// </summary>
    public const int ManifestMalformedExitCode = 50;

    public ManifestException(string message) : base(message) { }
    public ManifestException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Verifier limits enforced on every FlatBuffers manifest deserialize, mirroring
/// the JSON-side limits in <c>/Documents/XBT.html</c> Section 8.4. These are
/// hard ceilings; both XBT (writer) and XHT/XIL2CPP (readers) reject buffers
/// outside the budget.
/// </summary>
/// <remarks>
/// Toolchain Contract Rev 13 Section 10.2 + XBT.html Section 8.4 require:
/// MaxDepth = 64, MaxBytes = 100 MB, MaxStringLength = 16 KB,
/// MaxVectorLength = 65536.
/// </remarks>
public sealed record FbsVerifierLimits
{
    /// <summary>Maximum FlatBuffers object nesting depth. Default: 64.</summary>
    public int MaxDepth { get; init; } = 64;

    /// <summary>Maximum buffer size in bytes. Default: 100 MiB.</summary>
    public long MaxBytes { get; init; } = 100L * 1024L * 1024L;

    /// <summary>Maximum length of any single string field, in UTF-8 bytes. Default: 16 KiB.</summary>
    public int MaxStringLength { get; init; } = 16 * 1024;

    /// <summary>Maximum element count of any vector field. Default: 65536.</summary>
    public int MaxVectorLength { get; init; } = 65536;

    /// <summary>The defaults required by the Toolchain Contract Rev 13.</summary>
    public static FbsVerifierLimits ContractDefaults { get; } = new();
}

/// <summary>
/// Binary FlatBuffers serialization for the XPact manifest.
/// </summary>
/// <remarks>
/// <para>
/// The binary sidecar is the canonical machine-read form per Toolchain
/// Contract Rev 13 Section 10.2. JSON remains the human-debuggable form;
/// both come from the same POCO and have structurally equivalent content.
/// </para>
/// <para>
/// The Manifest POCO declared in <see cref="ManifestSchema"/> flattens
/// the target-info fields directly onto <see cref="Manifest"/>; the FBS
/// schema groups them under a nested <c>table TargetInfo</c>. This
/// converter packs / unpacks the nested grouping transparently.
/// </para>
/// <para>
/// The four-byte FlatBuffers <c>file_identifier</c> at offset +4 of the buffer
/// is <c>"XMFT"</c> (per <c>Manifest.fbs</c>). Readers verify this prefix
/// before trusting any other field; a corrupted or mis-pointed file fails
/// fast.
/// </para>
/// </remarks>
public static class ManifestFbs
{
    /// <summary>The FlatBuffers file_identifier defined in Manifest.fbs.</summary>
    public const string FileIdentifier = "XMFT";

    /// <summary>The four file_identifier bytes in ASCII, for direct buffer inspection.</summary>
    public static ReadOnlySpan<byte> FileIdentifierBytes
        => "XMFT"u8;

    /// <summary>
    /// Architecture string written into the FBS <c>TargetInfo.architecture</c>
    /// field when the source POCO does not carry an architecture choice.
    /// The Phase 1 default is x86_64 per Toolchain Contract Rev 13 Section 4.
    /// </summary>
    public const string DefaultArchitecture = "x86_64";

    /// <summary>
    /// Serialize a manifest POCO to a FlatBuffers byte array. The buffer is
    /// self-contained and ready to write to disk.
    /// </summary>
    /// <param name="manifest">The manifest to encode. Must not be null.</param>
    /// <param name="architecture">
    /// Optional CPU architecture for the FBS <c>TargetInfo.architecture</c>
    /// field. Defaults to <see cref="DefaultArchitecture"/> because the POCO
    /// does not carry architecture in Phase 1.
    /// </param>
    /// <param name="dynamicModuleNames">
    /// Optional set of module names that the writer should mark
    /// <c>is_dynamic = true</c> on in the FBS sidecar (per Toolchain
    /// Contract Section 13.1: dynamically loaded modules are recorded
    /// separately). Defaults to empty -- XBT's <c>WriteManifestAction</c>
    /// populates this from the source rules at emit time.
    /// </param>
    /// <returns>A newly allocated array sized to the actual encoded length.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="manifest"/> is null.</exception>
    public static byte[] SerializeToFbs(
        Manifest manifest,
        string architecture = DefaultArchitecture,
        IReadOnlySet<string>? dynamicModuleNames = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        FbsManifest root = ToFbs(manifest, architecture, dynamicModuleNames ?? new HashSet<string>(StringComparer.Ordinal));

        // FlatSharp emits a pre-generated Serializer per (fs_serializer)-tagged
        // table; for Manifest that property is FbsManifest.Serializer. The
        // Write extension method (FlatSharp.ISerializerExtensions) takes a
        // byte[] target plus the object; it returns the number of bytes
        // actually written, which is always <= GetMaxSize(...).
        int maxSize = FbsManifest.Serializer.GetMaxSize(root);
        byte[] buffer = new byte[maxSize];
        int written = FbsManifest.Serializer.Write(buffer, root);

        if (written < buffer.Length)
        {
            // Trim to actual encoded size; consumers of the byte array expect
            // a tight buffer (file write, hashing for content-addressable
            // filename, etc.).
            Array.Resize(ref buffer, written);
        }
        return buffer;
    }

    /// <summary>
    /// Deserialize a manifest from a FlatBuffers buffer. Verifies the
    /// file_identifier, applies the size / depth / string / vector limits
    /// from <paramref name="limits"/>, and returns the reconstructed POCO.
    /// </summary>
    /// <param name="bytes">The encoded buffer. May not be empty.</param>
    /// <param name="limits">
    /// Verifier limits. Pass <see cref="FbsVerifierLimits.ContractDefaults"/>
    /// for the standard contract-mandated limits.
    /// </param>
    /// <returns>The reconstructed manifest POCO.</returns>
    /// <exception cref="ManifestException">
    /// Thrown if the buffer is too short, the file_identifier does not match,
    /// any limit is exceeded, or FlatSharp itself rejects the buffer.
    /// </exception>
    public static Manifest DeserializeFromFbs(ReadOnlyMemory<byte> bytes, FbsVerifierLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        if (bytes.Length > limits.MaxBytes)
        {
            throw new ManifestException(
                $"Manifest binary exceeds MaxBytes limit ({bytes.Length} > {limits.MaxBytes}).");
        }
        // FlatBuffers root offset is 4 bytes; file_identifier follows at offset 4.
        // Minimum sensible buffer: 4 (root_offset) + 4 (identifier) + at least
        // a table vtable. Anything less is malformed.
        if (bytes.Length < 8)
        {
            throw new ManifestException(
                $"Manifest binary too small to be a FlatBuffer ({bytes.Length} bytes; minimum 8).");
        }

        VerifyFileIdentifier(bytes.Span);

        FbsManifest root;
        try
        {
            // FlatSharp 7 exposes WithSettings for tuning depth limit per parse;
            // we configure a Greedy deserializer (full materialization) with the
            // contract-mandated depth limit so structural cycles and deep nesting
            // are rejected. WithObjectDepthLimit accepts Int16; the contract
            // value of 64 fits trivially. Anything larger than short.MaxValue
            // (32767) is clamped here.
            short depthLimit = (short)Math.Min(limits.MaxDepth, short.MaxValue);
            var serializer = FbsManifest.Serializer
                .WithSettings(s => s.UseGreedyDeserialization()
                                     .WithObjectDepthLimit(depthLimit));
            root = serializer.Parse(bytes);
        }
        catch (Exception ex) when (ex is not ManifestException)
        {
            throw new ManifestException(
                "FlatBuffers verifier rejected the manifest binary.", ex);
        }

        // Post-parse limit checks. FlatSharp's verifier does not natively expose
        // MaxStringLength / MaxVectorLength tuning; we enforce by inspecting the
        // greedy-materialized object graph.
        EnforcePostParseLimits(root, limits);

        return FromFbs(root);
    }

    /// <summary>
    /// Verify the FlatBuffers <c>file_identifier</c> at offset 4 in the buffer.
    /// The first four bytes are the uoffset to the root table; the next four
    /// are the identifier per the FlatBuffers wire format.
    /// </summary>
    private static void VerifyFileIdentifier(ReadOnlySpan<byte> buffer)
    {
        ReadOnlySpan<byte> expected = FileIdentifierBytes;
        ReadOnlySpan<byte> actual = buffer.Slice(4, 4);
        if (!actual.SequenceEqual(expected))
        {
            throw new ManifestException(
                $"Manifest file_identifier mismatch: expected \"{FileIdentifier}\", " +
                $"got \"{System.Text.Encoding.ASCII.GetString(actual)}\".");
        }
    }

    private static void EnforcePostParseLimits(FbsManifest root, FbsVerifierLimits limits)
    {
        CheckString(root.ContractVersion,          "Manifest.contract_version",          limits);
        CheckString(root.EngineVersion,            "Manifest.engine_version",            limits);
        CheckString(root.RootLocalPath,            "Manifest.root_local_path",           limits);
        CheckString(root.ExternalDependenciesFile, "Manifest.external_dependencies_file", limits);

        if (root.Target is { } target)
        {
            CheckString(target.Name,         "TargetInfo.name",         limits);
            CheckString(target.Architecture, "TargetInfo.architecture", limits);
        }

        IList<FbsModule>? modules = root.Modules;
        if (modules is not null)
        {
            CheckVector(modules.Count, "Manifest.modules", limits);
            for (int i = 0; i < modules.Count; i++)
            {
                FbsModule m = modules[i];
                CheckString(m.Name,                     $"Module[{i}].name",                    limits);
                CheckString(m.BaseDirectory,            $"Module[{i}].base_directory",          limits);
                CheckString(m.GeneratedCppFilenameBase, $"Module[{i}].generated_cpp_filename_base", limits);
                CheckString(m.DeprecationMessage,       $"Module[{i}].deprecation_message",     limits);
                CheckString(m.MinimumToolchainVersion,  $"Module[{i}].minimum_toolchain_version", limits);
                CheckString(m.EngineVersionCompat,      $"Module[{i}].engine_version_compat",   limits);

                CheckStringVector(m.PublicHeaders,    $"Module[{i}].public_headers",    limits);
                CheckStringVector(m.PrivateHeaders,   $"Module[{i}].private_headers",   limits);
                CheckStringVector(m.InternalHeaders,  $"Module[{i}].internal_headers",  limits);
                CheckStringVector(m.CsharpSources,    $"Module[{i}].csharp_sources",    limits);
                CheckStringVector(m.IncludePaths,     $"Module[{i}].include_paths",     limits);
                CheckStringVector(m.PublicDefines,    $"Module[{i}].public_defines",    limits);

                if (m.SourceFiles is { } srcs)
                {
                    CheckVector(srcs.Count, $"Module[{i}].source_files", limits);
                    for (int j = 0; j < srcs.Count; j++)
                    {
                        CheckString(srcs[j].RelativePath, $"Module[{i}].source_files[{j}].relative_path", limits);
                    }
                }

                if (m.ModuleDependencies is { } deps)
                {
                    CheckVector(deps.Count, $"Module[{i}].module_dependencies", limits);
                    for (int j = 0; j < deps.Count; j++)
                    {
                        CheckString(deps[j].Name, $"Module[{i}].module_dependencies[{j}].name", limits);
                    }
                }
            }
        }
    }

    private static void CheckString(string? s, string field, FbsVerifierLimits limits)
    {
        if (s is null)
        {
            return;
        }
        int utf8Length = System.Text.Encoding.UTF8.GetByteCount(s);
        if (utf8Length > limits.MaxStringLength)
        {
            throw new ManifestException(
                $"String field {field} exceeds MaxStringLength limit ({utf8Length} > {limits.MaxStringLength} bytes).");
        }
    }

    private static void CheckStringVector(IList<string>? v, string field, FbsVerifierLimits limits)
    {
        if (v is null)
        {
            return;
        }
        CheckVector(v.Count, field, limits);
        for (int i = 0; i < v.Count; i++)
        {
            CheckString(v[i], $"{field}[{i}]", limits);
        }
    }

    private static void CheckVector(int count, string field, FbsVerifierLimits limits)
    {
        if (count > limits.MaxVectorLength)
        {
            throw new ManifestException(
                $"Vector field {field} exceeds MaxVectorLength limit ({count} > {limits.MaxVectorLength}).");
        }
    }

    // ----- POCO -> FBS conversion --------------------------------------------

    private static FbsManifest ToFbs(
        Manifest m,
        string architecture,
        IReadOnlySet<string> dynamicModuleNames)
    {
        return new FbsManifest
        {
            ContractVersion          = m.ContractVersion,
            EngineVersion            = m.EngineVersion,
            RootLocalPath            = m.RootLocalPath,
            ExternalDependenciesFile = m.ExternalDependenciesFile ?? string.Empty,
            Target                   = ToFbsTarget(m, architecture),
            Modules                  = m.Modules.Select(mod => ToFbs(mod, dynamicModuleNames)).ToList(),
        };
    }

    private static FbsTargetInfo ToFbsTarget(Manifest m, string architecture)
    {
        return new FbsTargetInfo
        {
            Name                            = m.TargetName,
            TargetType                      = (FbsTargetType)(int)m.TargetType,
            Configuration                   = (FbsConfiguration)(int)m.Configuration,
            Platform                        = (FbsPlatform)(int)m.Platform,
            Architecture                    = architecture,
            StationRole                     = (FbsStationRole)(int)m.StationRole,
            SimdLevelDefault                = (FbsSimdLevel)(int)m.SimdLevelDefault,
            FipsMode                        = m.FipsMode,
            SimPathConservativeRootsAllowed = m.SimPathConservativeRootsAllowed,
        };
    }

    private static FbsModule ToFbs(Module m, IReadOnlySet<string> dynamicModuleNames)
    {
        return new FbsModule
        {
            Name                     = m.Name,
            Tier                     = (FbsModuleTier)(int)m.Tier,
            ModuleType               = (FbsModuleType)(int)m.ModuleType,
            Languages                = (FbsLanguages)(int)m.Languages,
            BaseDirectory            = m.BaseDirectory,
            SourceFiles              = m.SourceFiles.Select(ToFbs).ToList(),
            PublicHeaders            = new List<string>(m.PublicHeaders),
            PrivateHeaders           = new List<string>(m.PrivateHeaders),
            InternalHeaders          = new List<string>(m.InternalHeaders),
            CsharpSources            = new List<string>(m.CSharpSources),
            IncludePaths             = new List<string>(m.IncludePaths),
            PublicDefines            = new List<string>(m.PublicDefines),
            ModuleDependencies       = m.ModuleDependencies
                                            .Select(d => ToFbs(d, dynamicModuleNames.Contains(d.Name)))
                                            .ToList(),
            GeneratedCppFilenameBase = m.GeneratedCPPFilenameBase,
            SimPath                  = m.SimPath,
            SimdLevel                = (FbsSimdLevel)(int)m.SimdLevel,
            PchUsage                 = (FbsPCHUsageMode)(int)m.PCHUsage,
            BExcludeFromSharedPch    = m.ExcludeFromSharedPCH,
            BAllowHotReload          = m.AllowHotReload,
            BIsTestModule            = m.IsTestModule,
            DeprecationMessage       = m.DeprecationMessage ?? string.Empty,
            MinimumToolchainVersion  = m.MinimumToolchainVersion ?? string.Empty,
            EngineVersionCompat      = m.EngineVersionCompat,
        };
    }

    private static FbsModuleDep ToFbs(ModuleDep d, bool isDynamic)
    {
        return new FbsModuleDep
        {
            Name            = d.Name,
            InterfaceModule = d.InterfaceModule,
            IsDynamic       = isDynamic,
        };
    }

    private static FbsSourceFile ToFbs(SourceFile s)
    {
        return new FbsSourceFile
        {
            RelativePath = s.RelativePath,
            IsCsharp     = s.IsCSharp,
            IsHeader     = s.IsHeader,
            IsTestOnly   = s.IsTestOnly,
        };
    }

    // ----- FBS -> POCO conversion --------------------------------------------

    private static Manifest FromFbs(FbsManifest m)
    {
        FbsTargetInfo? target = m.Target;
        return new Manifest(
            ContractVersion:                 m.ContractVersion ?? string.Empty,
            EngineVersion:                   m.EngineVersion   ?? string.Empty,
            TargetName:                      target?.Name      ?? string.Empty,
            TargetType:                      target is null ? default : (BuildTargetType)(int)target.TargetType,
            Configuration:                   target is null ? default : (BuildConfiguration)(int)target.Configuration,
            Platform:                        target is null ? default : (Platform)(int)target.Platform,
            RootLocalPath:                   m.RootLocalPath ?? string.Empty,
            ExternalDependenciesFile:        string.IsNullOrEmpty(m.ExternalDependenciesFile) ? null : m.ExternalDependenciesFile,
            FipsMode:                        target?.FipsMode ?? false,
            SimPathConservativeRootsAllowed: target?.SimPathConservativeRootsAllowed ?? false,
            StationRole:                     target is null ? default : (StationRole)(int)target.StationRole,
            SimdLevelDefault:                target is null ? SimdLevel.SSE42 : (SimdLevel)(int)target.SimdLevelDefault,
            Modules:                         (m.Modules ?? new List<FbsModule>()).Select(FromFbs).ToList());
    }

    private static Module FromFbs(FbsModule m)
    {
        return new Module(
            Name:                     m.Name ?? string.Empty,
            Tier:                     (ModuleTier)(int)m.Tier,
            ModuleType:               (ModuleType)(int)m.ModuleType,
            Languages:                (Languages)(int)m.Languages,
            BaseDirectory:            m.BaseDirectory ?? string.Empty,
            SourceFiles:              (m.SourceFiles ?? new List<FbsSourceFile>()).Select(FromFbs).ToList(),
            PublicHeaders:            (m.PublicHeaders   ?? new List<string>()).ToList(),
            PrivateHeaders:           (m.PrivateHeaders  ?? new List<string>()).ToList(),
            InternalHeaders:          (m.InternalHeaders ?? new List<string>()).ToList(),
            CSharpSources:            (m.CsharpSources   ?? new List<string>()).ToList(),
            IncludePaths:             (m.IncludePaths    ?? new List<string>()).ToList(),
            PublicDefines:            (m.PublicDefines   ?? new List<string>()).ToList(),
            ModuleDependencies:       (m.ModuleDependencies ?? new List<FbsModuleDep>()).Select(FromFbs).ToList(),
            GeneratedCPPFilenameBase: m.GeneratedCppFilenameBase ?? string.Empty,
            SimPath:                  m.SimPath,
            EngineVersionCompat:      m.EngineVersionCompat ?? string.Empty,
            SimdLevel:                (SimdLevel)(int)m.SimdLevel,
            PCHUsage:                 (PCHUsageMode)(int)m.PchUsage,
            ExcludeFromSharedPCH:     m.BExcludeFromSharedPch,
            AllowHotReload:           m.BAllowHotReload,
            IsTestModule:             m.BIsTestModule,
            DeprecationMessage:       string.IsNullOrEmpty(m.DeprecationMessage) ? null : m.DeprecationMessage,
            MinimumToolchainVersion:  string.IsNullOrEmpty(m.MinimumToolchainVersion) ? null : m.MinimumToolchainVersion);
    }

    private static ModuleDep FromFbs(FbsModuleDep d)
    {
        return new ModuleDep(d.Name ?? string.Empty, d.InterfaceModule);
    }

    private static SourceFile FromFbs(FbsSourceFile s)
    {
        return new SourceFile(
            RelativePath: s.RelativePath ?? string.Empty,
            IsCSharp:     s.IsCsharp,
            IsHeader:     s.IsHeader,
            IsTestOnly:   s.IsTestOnly);
    }
}
