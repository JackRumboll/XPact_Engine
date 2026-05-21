// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Manifest;

namespace Simgenics.XPact.XHT.Tests.Tests.Resolver;

/// <summary>
/// Shared helpers for resolver tests. Constructs minimal valid AST
/// nodes + manifest fixtures so the per-phase tests can express their
/// assertions concisely.
/// </summary>
internal static class ResolverTestHarness
{
    /// <summary>The default module name for resolver tests.</summary>
    public const string TestModule = "XScoring";

    /// <summary>A neutral fixed-line SourceSpan for test AST nodes.</summary>
    public static SourceSpan Span(int line = 1, int column = 1, int length = 5)
        => new("Test.h", line, column, length);

    /// <summary>Construct an empty manifest with only the test module declared.</summary>
    public static XbtManifest MakeManifest(
        string moduleName = TestModule,
        IReadOnlyList<XbtModuleDep>? deps = null,
        IReadOnlyList<XbtModule>? extraModules = null)
    {
        XbtModule consumer = MakeModule(moduleName, deps);

        List<XbtModule> modules = new() { consumer };
        if (extraModules is not null)
        {
            modules.AddRange(extraModules);
        }

        XbtTargetInfo target = new(
            Name: "TestTarget",
            Type: BuildTargetType.Editor,
            Platform: Platform.Win64,
            Configuration: BuildConfiguration.Development,
            Architecture: "x86_64",
            GCRootABI: "GCRoot-v1",
            ExceptionABI: "Exc-v1",
            ManglingScheme: "MS-v1",
            FipsMode: false,
            SimPathConservativeRootsAllowed: false,
            SimdLevelDefault: SimdLevel.SSE42,
            StationRole: StationRole.None);

        return new XbtManifest(
            ContractVersion: "13.2+test",
            EngineVersion: "0.0.0",
            Target: target,
            RootLocalPath: "C:/test",
            ExternalDependenciesFile: null,
            Modules: modules);
    }

    public static XbtModule MakeModule(string name, IReadOnlyList<XbtModuleDep>? deps = null)
    {
        return new XbtModule(
            Name: name,
            Tier: ModuleTier.Engine,
            ModuleType: global::Simgenics.XPact.XHT.Manifest.ModuleType.Runtime,
            Languages: Languages.Both,
            BaseDirectory: $"Source/{name}",
            SourceFiles: Array.Empty<XbtSourceFile>(),
            PublicHeaders: Array.Empty<string>(),
            PrivateHeaders: Array.Empty<string>(),
            InternalHeaders: Array.Empty<string>(),
            CSharpSources: Array.Empty<string>(),
            IncludePaths: Array.Empty<string>(),
            PublicDefines: Array.Empty<string>(),
            ModuleDependencies: deps ?? Array.Empty<XbtModuleDep>(),
            GeneratedCPPFilenameBase: name,
            SimPath: false,
            EngineVersionCompat: "0.0.0",
            SimdLevel: SimdLevel.Default,
            PCHUsage: PCHUsageMode.Default,
            ExcludeFromSharedPCH: false,
            AllowHotReload: false,
            IsTestModule: false,
            DeprecationMessage: null,
            MinimumToolchainVersion: null);
    }

    /// <summary>Build a minimal valid <see cref="XhtClass"/> with sensible defaults.</summary>
    public static XhtClass MakeClass(
        string name,
        string? superIdentifier = null,
        Language lang = Language.Cpp,
        string module = TestModule,
        IReadOnlyList<Specifier>? specifiers = null,
        IReadOnlyList<string>? interfaceIdentifiers = null,
        string? withinIdentifier = null,
        string? requiredApiMacroName = null,
        bool hasGeneratedBody = false,
        IReadOnlyList<XhtFunction>? functions = null,
        IReadOnlyList<XhtProperty>? properties = null)
    {
        return new XhtClass(
            Name: name,
            FullyQualifiedName: name,
            OuterName: null,
            ModuleName: module,
            Language: lang,
            Span: Span(),
            Specifiers: specifiers ?? Array.Empty<Specifier>(),
            SuperIdentifier: superIdentifier,
            Super: null,
            Functions: functions ?? Array.Empty<XhtFunction>(),
            Properties: properties ?? Array.Empty<XhtProperty>(),
            InterfaceIdentifiers: interfaceIdentifiers ?? Array.Empty<string>(),
            Interfaces: Array.Empty<XhtInterface>(),
            WithinIdentifier: withinIdentifier,
            WithinClass: null,
            RequiredAPIMacroName: requiredApiMacroName,
            HasGeneratedBody: hasGeneratedBody);
    }

    /// <summary>Build a minimal valid <see cref="XhtStruct"/> with sensible defaults.</summary>
    public static XhtStruct MakeStruct(
        string name,
        string? superIdentifier = null,
        Language lang = Language.Cpp,
        string module = TestModule,
        IReadOnlyList<Specifier>? specifiers = null,
        IReadOnlyList<XhtProperty>? properties = null)
    {
        return new XhtStruct(
            Name: name,
            FullyQualifiedName: name,
            OuterName: null,
            ModuleName: module,
            Language: lang,
            Span: Span(),
            Specifiers: specifiers ?? Array.Empty<Specifier>(),
            SuperIdentifier: superIdentifier,
            Super: null,
            Properties: properties ?? Array.Empty<XhtProperty>(),
            IsFastArraySerializer: false);
    }

    /// <summary>Build a minimal valid <see cref="XhtInterface"/>.</summary>
    public static XhtInterface MakeInterface(
        string name,
        string? superIdentifier = null,
        Language lang = Language.Cpp,
        string module = TestModule,
        IReadOnlyList<XhtFunction>? functions = null)
    {
        return new XhtInterface(
            Name: name,
            FullyQualifiedName: name,
            OuterName: null,
            ModuleName: module,
            Language: lang,
            Span: Span(),
            Specifiers: Array.Empty<Specifier>(),
            SuperIdentifier: superIdentifier,
            Super: null,
            Functions: functions ?? Array.Empty<XhtFunction>());
    }

    /// <summary>Build a minimal <see cref="XhtFunction"/>.</summary>
    public static XhtFunction MakeFunction(
        string name,
        string returnType = "void",
        IReadOnlyList<XhtParam>? parameters = null,
        IReadOnlyList<Specifier>? specifiers = null)
    {
        return new XhtFunction(
            Name: name,
            ReturnType: returnType,
            Parameters: parameters ?? Array.Empty<XhtParam>(),
            Specifiers: specifiers ?? Array.Empty<Specifier>(),
            IsStatic: false,
            IsVirtual: false,
            IsConst: false,
            Span: Span());
    }

    /// <summary>Build a minimal <see cref="XhtProperty"/>.</summary>
    public static XhtProperty MakeProperty(
        string name,
        string typeIdentifier = "int32",
        IReadOnlyList<Specifier>? specifiers = null,
        bool isContainer = false)
    {
        return new XhtProperty(
            Name: name,
            TypeIdentifier: typeIdentifier,
            Specifiers: specifiers ?? Array.Empty<Specifier>(),
            IsContainer: isContainer,
            RepNotifyFunctionName: null,
            Category: null,
            Span: Span());
    }

    /// <summary>Build a minimal <see cref="XhtParam"/>.</summary>
    public static XhtParam MakeParam(string name, string typeIdentifier)
    {
        return new XhtParam(
            Name: name,
            TypeIdentifier: typeIdentifier,
            Specifiers: Array.Empty<Specifier>(),
            IsOut: false,
            IsRef: false,
            Span: Span());
    }

    /// <summary>Build a flag-style specifier (no value).</summary>
    public static Specifier Flag(string name) => Specifier.Flag(name, Span());

    /// <summary>Build a single-value specifier.</summary>
    public static Specifier Value(string name, string value)
        => new(name, new[] { value }, Span());
}
