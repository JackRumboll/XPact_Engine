// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Emitter;
using Simgenics.XPact.XHT.Manifest;
using Simgenics.XPact.XHT.Resolver;
using Simgenics.XPact.XHT.Tables;

namespace Simgenics.XPact.XHT.Tests.Tests.Emitter;

/// <summary>
/// Shared helpers for emitter tests. Constructs minimal-valid emit
/// contexts with synthetic AST nodes the per-emitter tests can drive
/// directly.
/// </summary>
internal static class EmitterTestHarness
{
    public const string DefaultModule = "XGameFramework";

    public static SourceSpan Span(string sourcePath = "Public/XValve.h", int line = 14, int col = 1, int len = 5)
        => new(sourcePath, line, col, len);

    public static XbtModule MakeModule(
        string name = DefaultModule,
        string baseDir = "Engine/Source/Runtime/XGameFramework",
        IReadOnlyList<XbtSourceFile>? sourceFiles = null,
        ModuleTier tier = ModuleTier.Engine)
    {
        return new XbtModule(
            Name: name,
            Tier: tier,
            ModuleType: ModuleType.Runtime,
            Languages: Languages.Both,
            BaseDirectory: baseDir,
            SourceFiles: sourceFiles ?? Array.Empty<XbtSourceFile>(),
            PublicHeaders: Array.Empty<string>(),
            PrivateHeaders: Array.Empty<string>(),
            InternalHeaders: Array.Empty<string>(),
            CSharpSources: Array.Empty<string>(),
            IncludePaths: Array.Empty<string>(),
            PublicDefines: Array.Empty<string>(),
            ModuleDependencies: Array.Empty<XbtModuleDep>(),
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

    public static XbtManifest MakeManifest(XbtModule? module = null, string rootLocalPath = "C:/test")
    {
        XbtTargetInfo target = new(
            Name: "TestTarget",
            Type: BuildTargetType.Editor,
            Platform: Platform.Win64,
            Configuration: BuildConfiguration.Development,
            Architecture: "x86_64",
            GCRootABI: "Span-based v1",
            ExceptionABI: "Tier1-Shim/Tier2-Direct",
            ManglingScheme: "Itanium-LengthPrefixed-v1",
            FipsMode: false,
            SimPathConservativeRootsAllowed: false,
            SimdLevelDefault: SimdLevel.SSE42,
            StationRole: StationRole.None);

        XbtModule m = module ?? MakeModule();
        return new XbtManifest(
            ContractVersion: "13.2+test",
            EngineVersion: "0.0.0",
            Target: target,
            RootLocalPath: rootLocalPath,
            ExternalDependenciesFile: null,
            Modules: new[] { m });
    }

    public static EmitterContext MakeContext(
        string outputDirectory,
        XbtModule? module = null,
        IReadOnlyList<XhtTypeBase>? typesToRegister = null,
        XbtManifest? manifestOverride = null)
    {
        XbtModule mod = module ?? MakeModule();
        XbtManifest manifest = manifestOverride ?? MakeManifest(mod);

        SymbolTable symbols = new();
        if (typesToRegister is not null)
        {
            foreach (XhtTypeBase t in typesToRegister)
            {
                symbols.Register(t);
            }
        }

        SpecifierRegistry registry = new(registerBuiltIns: true);
        List<DiagnosticRecord> diagnostics = new();
        ResolverContext resolverContext = new(symbols, registry, manifest, mod.Name, diagnostics);

        return new EmitterContext(
            ResolverContext: resolverContext,
            XbtManifest: manifest,
            Module: mod,
            OutputDirectory: outputDirectory,
            Diagnostics: diagnostics);
    }

    public static XhtClass MakeClass(
        string name,
        string moduleName = DefaultModule,
        Language lang = Language.Cpp,
        string sourcePath = "Public/XValve.h",
        int line = 14,
        IReadOnlyList<XhtProperty>? properties = null,
        IReadOnlyList<XhtFunction>? functions = null,
        string? superIdentifier = null,
        string? super = null)
    {
        return new XhtClass(
            Name: name,
            FullyQualifiedName: name,
            OuterName: null,
            ModuleName: moduleName,
            Language: lang,
            Span: new SourceSpan(sourcePath, line, 1, name.Length),
            Specifiers: Array.Empty<Specifier>(),
            SuperIdentifier: superIdentifier ?? super,
            Super: null,
            Functions: functions ?? Array.Empty<XhtFunction>(),
            Properties: properties ?? Array.Empty<XhtProperty>(),
            InterfaceIdentifiers: Array.Empty<string>(),
            Interfaces: Array.Empty<XhtInterface>(),
            WithinIdentifier: null,
            WithinClass: null,
            RequiredAPIMacroName: null,
            HasGeneratedBody: true);
    }

    public static XhtInterface MakeInterface(
        string name,
        string moduleName = DefaultModule,
        Language lang = Language.Cpp,
        string sourcePath = "Public/XValve.h",
        int line = 14,
        IReadOnlyList<XhtFunction>? functions = null)
    {
        return new XhtInterface(
            Name: name,
            FullyQualifiedName: name,
            OuterName: null,
            ModuleName: moduleName,
            Language: lang,
            Span: new SourceSpan(sourcePath, line, 1, name.Length),
            Specifiers: Array.Empty<Specifier>(),
            SuperIdentifier: null,
            Super: null,
            Functions: functions ?? Array.Empty<XhtFunction>());
    }

    public static XhtParam MakeParam(string name, string typeIdentifier)
    {
        return new XhtParam(
            Name: name,
            TypeIdentifier: typeIdentifier,
            Specifiers: Array.Empty<Specifier>(),
            IsOut: false,
            IsRef: false,
            Span: new SourceSpan("Public/XValve.h", 14, 1, name.Length));
    }

    public static XhtStruct MakeStruct(
        string name,
        string moduleName = DefaultModule,
        Language lang = Language.Cpp,
        string sourcePath = "Public/XValve.h",
        int line = 14,
        IReadOnlyList<XhtProperty>? properties = null)
    {
        return new XhtStruct(
            Name: name,
            FullyQualifiedName: name,
            OuterName: null,
            ModuleName: moduleName,
            Language: lang,
            Span: new SourceSpan(sourcePath, line, 1, name.Length),
            Specifiers: Array.Empty<Specifier>(),
            SuperIdentifier: null,
            Super: null,
            Properties: properties ?? Array.Empty<XhtProperty>(),
            IsFastArraySerializer: false);
    }

    public static XhtEnum MakeEnum(
        string name,
        string moduleName = DefaultModule,
        Language lang = Language.Cpp,
        string sourcePath = "Public/XValve.h",
        int line = 14,
        IReadOnlyList<XhtEnumValue>? values = null)
    {
        return new XhtEnum(
            Name: name,
            FullyQualifiedName: name,
            OuterName: null,
            ModuleName: moduleName,
            Language: lang,
            Span: new SourceSpan(sourcePath, line, 1, name.Length),
            Specifiers: Array.Empty<Specifier>(),
            UnderlyingType: null,
            IsFlags: false,
            Values: values ?? Array.Empty<XhtEnumValue>());
    }

    public static XhtProperty MakeProperty(
        string name,
        string typeIdentifier = "int32",
        string sourcePath = "Public/XValve.h",
        int line = 14)
    {
        return new XhtProperty(
            Name: name,
            TypeIdentifier: typeIdentifier,
            Specifiers: Array.Empty<Specifier>(),
            IsContainer: false,
            RepNotifyFunctionName: null,
            Category: null,
            Span: new SourceSpan(sourcePath, line, 1, name.Length));
    }

    public static XhtFunction MakeFunction(
        string name,
        string returnType = "void",
        string sourcePath = "Public/XValve.h",
        int line = 14,
        IReadOnlyList<XhtParam>? parameters = null)
    {
        return new XhtFunction(
            Name: name,
            ReturnType: returnType,
            Parameters: parameters ?? Array.Empty<XhtParam>(),
            Specifiers: Array.Empty<Specifier>(),
            IsStatic: false,
            IsVirtual: false,
            IsConst: false,
            Span: new SourceSpan(sourcePath, line, 1, name.Length));
    }
}
