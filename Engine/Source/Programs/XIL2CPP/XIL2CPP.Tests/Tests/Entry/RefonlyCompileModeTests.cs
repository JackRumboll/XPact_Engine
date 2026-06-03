// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Entry;
using Simgenics.XPact.XIL2CPP.Entry.Modes;
using Xunit;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Entry;

/// <summary>
/// End-to-end tests for <see cref="RefonlyCompileMode"/> -- the
/// <c>refonly-compile</c> CLI mode that emits a module's
/// <c>&lt;Module&gt;.refonly.dll</c> (metadata-only reference assembly) per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 9.8.
/// </summary>
/// <remarks>
/// The mode loads its BCL from the Phase-6.a staging directory
/// <c>&lt;intermediateRoot&gt;/Bcl/</c>; these tests stage
/// <c>Basic.Reference.Assemblies.Net80</c> there so a type declaration binds
/// and the metadata-only emit succeeds. The produced DLL is then re-loaded
/// as a <see cref="MetadataReference"/> (proving the declared types +
/// signatures are present) and inspected with
/// <see cref="System.Reflection.Metadata.MetadataReader"/> (proving every
/// method body was stripped). Logger state is process-global, so this
/// collection serialises against the other Logger-touching collections.
/// </remarks>
[Collection(nameof(RefonlyCompileModeTests))]
[CollectionDefinition(nameof(RefonlyCompileModeTests), DisableParallelization = true)]
public sealed class RefonlyCompileModeTests : IDisposable
{
    private readonly string _root;

    public RefonlyCompileModeTests()
    {
        Logger.DisableJsonChannel();
        Logger.ResetCounters();
        Logger.__SetStderrForTesting(null);

        _root = Path.Combine(Path.GetTempPath(), "XIL2CPP-RefonlyMode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        // The intermediate-root resolver walks up looking for an ancestor with
        // an Engine/ sibling; create it so the resolver anchors here.
        Directory.CreateDirectory(Path.Combine(_root, "Engine"));
    }

    public void Dispose()
    {
        Logger.DisableJsonChannel();
        Logger.ResetCounters();
        Logger.__SetStderrForTesting(null);
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>The intermediate root the mode resolves under <c>_root</c>.</summary>
    private string IntermediateRoot => Path.Combine(_root, "Intermediate", "Build", "XIL2CPP");

    /// <summary>The produced reference DLL path for <paramref name="moduleName"/>.</summary>
    private string RefOnlyDllPath(string moduleName) =>
        Path.Combine(IntermediateRoot, moduleName, "Reference", moduleName + ".refonly.dll");

    private void WriteSource(string relativeUnderRoot, string content)
    {
        string full = Path.Combine(_root, relativeUnderRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// Stage the pinned .NET 8 BCL reference assemblies into the mode's
    /// Phase-6.a BCL staging directory (<c>&lt;intermediateRoot&gt;/Bcl/</c>)
    /// so a type declaration binds and the metadata-only emit succeeds.
    /// </summary>
    private void StageBcl()
    {
        string bclDir = Path.Combine(IntermediateRoot, RefonlyCompileMode.BclSubdirectory);
        Directory.CreateDirectory(bclDir);
        foreach (Basic.Reference.Assemblies.Net80.ReferenceInfo info
                 in Basic.Reference.Assemblies.Net80.ReferenceInfos.All)
        {
            File.WriteAllBytes(Path.Combine(bclDir, info.FileName), info.ImageBytes.ToArray());
        }
    }

    private string WriteManifest(string moduleName, string baseDir, string sourceRelative)
    {
        string manifestPath = Path.Combine(_root, "Manifest.json");
        string rootForwardSlash = _root.Replace('\\', '/');
        string json = $$"""
            {
              "ContractVersion": "{{Xil2CppVersion.ContractVersion}}",
              "EngineVersion": "0.1.0",
              "Target": {
                "Name": "MiningTrainingEditor",
                "Type": "Editor",
                "Platform": "Win64",
                "Configuration": "Development",
                "Architecture": "x86_64",
                "GCRootABI": "Span-based v1",
                "ExceptionABI": "Tier1-Shim/Tier2-Direct",
                "ManglingScheme": "Itanium-LengthPrefixed-v1",
                "FipsMode": false,
                "SimPathConservativeRootsAllowed": false,
                "SimdLevelDefault": "SSE42",
                "StationRole": "None"
              },
              "RootLocalPath": "{{rootForwardSlash}}",
              "ExternalDependenciesFile": null,
              "Modules": [
                {
                  "Name": "{{moduleName}}",
                  "Tier": "Engine",
                  "ModuleType": "Runtime",
                  "Languages": "CSharp",
                  "BaseDirectory": "{{baseDir}}",
                  "SourceFiles": [],
                  "PublicHeaders": [],
                  "PrivateHeaders": [],
                  "InternalHeaders": [],
                  "CSharpSources": [ "{{sourceRelative}}" ],
                  "IncludePaths": [],
                  "PublicDefines": [],
                  "ModuleDependencies": [],
                  "GeneratedCPPFilenameBase": "{{moduleName}}",
                  "SimPath": false,
                  "EngineVersionCompat": "0.1.0",
                  "SimdLevel": "Default",
                  "PCHUsage": "Default",
                  "ExcludeFromSharedPCH": false,
                  "AllowHotReload": false,
                  "IsTestModule": false,
                  "DeprecationMessage": null,
                  "MinimumToolchainVersion": null
                }
              ]
            }
            """;
        File.WriteAllText(manifestPath, json, new UTF8Encoding(false));
        return manifestPath;
    }

    [Fact]
    public void Mode_IsRegistered_UnderRefonlyCompileName()
    {
        IToolMode? mode = ToolModeRegistry.Resolve("refonly-compile");
        Assert.NotNull(mode);
        Assert.IsType<RefonlyCompileMode>(mode);
    }

    [Fact]
    public void Mode_Description_DescribesMetadataOnlyReferenceAssembly()
    {
        IToolMode mode = ToolModeRegistry.Resolve("refonly-compile")!;
        Assert.Contains("refonly.dll", mode.Description, StringComparison.Ordinal);
        Assert.Contains("metadata-only", mode.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no method bodies", mode.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_MissingOut_ReturnsCliArgumentError()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        WriteSource("Mod/A.cs", "namespace Mod;");
        string manifest = WriteManifest("Mod", "Mod", "A.cs");

        IToolMode mode = new RefonlyCompileMode();
        // -Out= is required for refonly-compile.
        int exit = await mode.ExecuteAsync(
            new[] { $"-Manifest={manifest}", "-Module=Mod" },
            CancellationToken.None);

        Assert.Equal(ExitCodes.CliArgumentError, exit);
    }

    [Fact]
    public async Task Main_RefonlyCompile_ManifestNotFound_ReturnsManifestMalformed()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        string missing = Path.Combine(_root, "does-not-exist.json");
        int exit = await Program.Main(new[]
        {
            "refonly-compile",
            $"-Manifest={missing}",
            "-Module=Whatever",
            $"-Out={RefOnlyDllPath("Whatever")}",
        });

        Assert.Equal(ExitCodes.ManifestMalformed, exit);
    }

    [Fact]
    public async Task Main_RefonlyCompile_ModuleNotInManifest_ReturnsManifestMalformed()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        WriteSource("Mod/A.cs", "namespace Mod;");
        string manifest = WriteManifest("Present", "Mod", "A.cs");

        int exit = await Program.Main(new[]
        {
            "refonly-compile",
            $"-Manifest={manifest}",
            "-Module=Absent",
            $"-Out={RefOnlyDllPath("Absent")}",
        });

        Assert.Equal(ExitCodes.ManifestMalformed, exit);
    }

    [Fact]
    public async Task Main_RefonlyCompile_TypeWithoutBcl_ReturnsInternalFailure()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        // No BCL staged: a type declaration cannot bind System.Object, so the
        // metadata-only emit fails -> exit 63. (Honest failure: the produced
        // DLL is NOT written.)
        WriteSource("Mod/C.cs", "namespace Mod; public class C { public int X; }");
        string manifest = WriteManifest("Mod", "Mod", "C.cs");
        string outPath = RefOnlyDllPath("Mod");

        int exit = await Program.Main(new[]
        {
            "refonly-compile",
            $"-Manifest={manifest}",
            "-Module=Mod",
            $"-Out={outPath}",
        });

        Assert.Equal(ExitCodes.Xil2CppInternalFailure, exit);
        Assert.False(File.Exists(outPath), "A failed emit must not leave a reference DLL on disk.");
    }

    [Fact]
    public async Task Main_RefonlyCompile_WithStagedBcl_ProducesRefonlyDll_ExposingTypesWithNoBodies()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        StageBcl();

        // A type with a method whose body would normally carry IL; the
        // metadata-only emit must keep the type + method signature but strip
        // the body.
        const string source = """
            namespace XScoring;
            public class Widget
            {
                public int Spin(int seed) { return seed + 7; }
                public string Name { get; set; } = "w";
            }
            """;
        WriteSource("XScoring/Widget.cs", source);
        string manifest = WriteManifest("XScoring", "XScoring", "Widget.cs");
        string outPath = RefOnlyDllPath("XScoring");

        int exit = await Program.Main(new[]
        {
            "refonly-compile",
            $"-Manifest={manifest}",
            "-Module=XScoring",
            $"-Out={outPath}",
        });

        Assert.Equal(ExitCodes.Success, exit);
        Assert.True(File.Exists(outPath), "Expected the reference DLL to be produced at -Out=.");

        // (1) The DLL loads as a MetadataReference and exposes the declared
        //     type + method signature (a consumer binds against it cleanly).
        AssertTypeAndMethodResolvable(outPath);

        // (2) The produced PE is a true reference assembly (/refonly): it is
        //     marked with ReferenceAssemblyAttribute and every method body is
        //     reduced to the trivial `throw null;` stub (no real IL logic).
        AssertReferenceAssemblyWithStubBodies(outPath);
    }

    [Fact]
    public async Task Main_RefonlyCompile_DeclarationFreeSource_ProducesDll_WithoutBcl()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        // A declaration-free compilation unit needs no BCL: the metadata-only
        // emit succeeds even with an empty BCL set, producing an (effectively
        // empty) reference assembly.
        WriteSource("Mod/Empty.cs", "// only a comment; no type declarations\n");
        string manifest = WriteManifest("Mod", "Mod", "Empty.cs");
        string outPath = RefOnlyDllPath("Mod");

        int exit = await Program.Main(new[]
        {
            "refonly-compile",
            $"-Manifest={manifest}",
            "-Module=Mod",
            $"-Out={outPath}",
        });

        Assert.Equal(ExitCodes.Success, exit);
        Assert.True(File.Exists(outPath));
    }

    /// <summary>
    /// Build a tiny consumer compilation referencing the produced reference
    /// DLL (+ the staged BCL) and assert the declared type + method bind --
    /// proving the reference assembly exposes the public surface.
    /// </summary>
    private void AssertTypeAndMethodResolvable(string refOnlyDllPath)
    {
        List<MetadataReference> references = Basic.Reference.Assemblies.Net80.References.All
            .Cast<MetadataReference>()
            .ToList();
        references.Add(MetadataReference.CreateFromFile(refOnlyDllPath));

        const string consumerSource = """
            namespace Consumer;
            public class User
            {
                public int Use(XScoring.Widget w) => w.Spin(1) + w.Name.Length;
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(consumerSource);
        CSharpCompilation consumer = CSharpCompilation.Create(
            "Consumer",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Diagnostic[] errors = consumer.GetDiagnostics()
            .Where(d => d.Severity == RoslynSeverity.Error)
            .ToArray();
        Assert.True(
            errors.Length == 0,
            "Consumer failed to bind against the reference DLL: "
            + string.Join(" | ", errors.Select(e => e.ToString())));
    }

    /// <summary>
    /// Inspect the produced PE with a <see cref="MetadataReader"/> and assert
    /// it is a true reference assembly (the <c>/refonly</c> shape Section 9.8
    /// mandates): (a) the assembly carries
    /// <c>System.Runtime.CompilerServices.ReferenceAssemblyAttribute</c>, and
    /// (b) every method body present is the trivial <c>throw null;</c> stub
    /// (IL <c>ldnull; throw</c> -- 2 bytes), proving no real method-body IL
    /// was emitted. The declared type + a declared method signature must
    /// still be present.
    /// </summary>
    private static void AssertReferenceAssemblyWithStubBodies(string refOnlyDllPath)
    {
        using FileStream fs = File.OpenRead(refOnlyDllPath);
        using PEReader peReader = new(fs);

        Assert.True(peReader.HasMetadata, "Produced file is not a managed PE with metadata.");
        MetadataReader reader = peReader.GetMetadataReader();

        // (a) ReferenceAssemblyAttribute on the assembly -- the defining mark
        //     of a /refonly reference assembly.
        Assert.True(
            HasReferenceAssemblyAttribute(reader),
            "Produced DLL is not marked with ReferenceAssemblyAttribute "
            + "(emit was not a /refonly reference-only emit).");

        // (b) Every method body is the trivial throw-null stub: no real IL
        //     logic survived. The Widget.Spin source body (return seed + 7)
        //     would compile to ldarg/ldc/add/ret (> 2 bytes) if real bodies
        //     were emitted.
        int methodsInspected = 0;
        bool sawSpin = false;
        foreach (MethodDefinitionHandle handle in reader.MethodDefinitions)
        {
            MethodDefinition method = reader.GetMethodDefinition(handle);
            methodsInspected++;

            if (string.Equals(reader.GetString(method.Name), "Spin", StringComparison.Ordinal))
            {
                sawSpin = true;
            }

            int rva = method.RelativeVirtualAddress;
            if (rva == 0)
            {
                // Body genuinely stripped -- also acceptable for "no bodies".
                continue;
            }

            byte[]? il = peReader.GetMethodBody(rva).GetILBytes();
            Assert.NotNull(il);
            // throw null; == ldnull (0x14) ; throw (0x7A) -> exactly 2 bytes.
            Assert.True(
                il!.Length <= 2,
                $"Method '{reader.GetString(method.Name)}' has a non-stub body "
                + $"({il.Length} IL bytes); a reference-only emit must not carry real bodies.");
        }

        Assert.True(methodsInspected > 0, "Expected at least one method definition in the reference DLL.");
        Assert.True(sawSpin, "Expected the declared method 'Spin' (signature retained) in the reference DLL.");

        // The declared type itself must be present (signatures are retained).
        bool sawWidget = false;
        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            if (string.Equals(reader.GetString(type.Name), "Widget", StringComparison.Ordinal))
            {
                sawWidget = true;
                break;
            }
        }
        Assert.True(sawWidget, "Expected the declared type 'Widget' in the reference DLL metadata.");
    }

    /// <summary>
    /// True iff the assembly definition carries
    /// <c>System.Runtime.CompilerServices.ReferenceAssemblyAttribute</c>.
    /// </summary>
    private static bool HasReferenceAssemblyAttribute(MetadataReader reader)
    {
        AssemblyDefinition assembly = reader.GetAssemblyDefinition();
        foreach (CustomAttributeHandle handle in assembly.GetCustomAttributes())
        {
            CustomAttribute attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
            {
                continue;
            }

            MemberReference ctor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (ctor.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            TypeReference typeRef = reader.GetTypeReference((TypeReferenceHandle)ctor.Parent);
            if (string.Equals(reader.GetString(typeRef.Name), "ReferenceAssemblyAttribute", StringComparison.Ordinal)
                && string.Equals(
                    reader.GetString(typeRef.Namespace),
                    "System.Runtime.CompilerServices",
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
