// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Simgenics.XPact.XBT.Manifest;
using Xunit;
using ManifestRecord = Simgenics.XPact.XBT.Manifest.Manifest;

namespace Simgenics.XPact.XBT.Tests.Tests.Manifest;

/// <summary>
/// Deep-equality assertions for the manifest record graph. The POCO records
/// declared in <c>ManifestSchema.cs</c> use positional records, which give us
/// value-equality on the scalar fields but only reference-equality on the
/// <c>IReadOnlyList</c>-typed properties (records call
/// <c>EqualityComparer&lt;T&gt;.Default.Equals</c> on each property; for
/// collection types that means reference equality, not element-by-element).
/// Round-trip tests rely on element-wise equality, so this helper walks the
/// graph and compares contents.
/// </summary>
internal static class ManifestEquality
{
    public static void AssertEqual(ManifestRecord expected, ManifestRecord actual)
    {
        Assert.Equal(expected.ContractVersion,                 actual.ContractVersion);
        Assert.Equal(expected.EngineVersion,                   actual.EngineVersion);
        Assert.Equal(expected.RootLocalPath,                   actual.RootLocalPath);
        Assert.Equal(expected.ExternalDependenciesFile,        actual.ExternalDependenciesFile);

        // Per Toolchain Contract Rev 13 Section 10.2 reconciliation:
        // per-target fields live under the nested Target record so JSON
        // mirrors the FBS TargetInfo grouping.
        Assert.NotNull(expected.Target);
        Assert.NotNull(actual.Target);
        Assert.Equal(expected.Target.Name,                            actual.Target.Name);
        Assert.Equal(expected.Target.Type,                            actual.Target.Type);
        Assert.Equal(expected.Target.Configuration,                   actual.Target.Configuration);
        Assert.Equal(expected.Target.Platform,                        actual.Target.Platform);
        // Audit fix C1/C10: ABI envelope fields.
        Assert.Equal(expected.Target.Architecture,                    actual.Target.Architecture);
        Assert.Equal(expected.Target.FipsMode,                        actual.Target.FipsMode);
        Assert.Equal(expected.Target.SimPathConservativeRootsAllowed, actual.Target.SimPathConservativeRootsAllowed);
        Assert.Equal(expected.Target.StationRole,                     actual.Target.StationRole);
        Assert.Equal(expected.Target.SimdLevelDefault,                actual.Target.SimdLevelDefault);
        Assert.Equal(expected.Target.GCRootABI,                       actual.Target.GCRootABI);
        Assert.Equal(expected.Target.ExceptionABI,                    actual.Target.ExceptionABI);
        Assert.Equal(expected.Target.ManglingScheme,                  actual.Target.ManglingScheme);

        Assert.Equal(expected.Modules.Count, actual.Modules.Count);
        for (int i = 0; i < expected.Modules.Count; i++)
        {
            AssertModuleEqual(expected.Modules[i], actual.Modules[i], i);
        }
    }

    private static void AssertModuleEqual(Module expected, Module actual, int index)
    {
        string ctx = $"Module[{index}] '{expected.Name}'";
        Assert.Equal(expected.Name,                     actual.Name);
        Assert.Equal(expected.Tier,                     actual.Tier);
        Assert.Equal(expected.ModuleType,               actual.ModuleType);
        Assert.Equal(expected.Languages,                actual.Languages);
        Assert.Equal(expected.BaseDirectory,            actual.BaseDirectory);
        Assert.Equal(expected.GeneratedCPPFilenameBase, actual.GeneratedCPPFilenameBase);
        Assert.Equal(expected.SimPath,                  actual.SimPath);
        Assert.Equal(expected.EngineVersionCompat,      actual.EngineVersionCompat);
        Assert.Equal(expected.SimdLevel,                actual.SimdLevel);
        Assert.Equal(expected.PCHUsage,                 actual.PCHUsage);
        Assert.Equal(expected.ExcludeFromSharedPCH,     actual.ExcludeFromSharedPCH);
        Assert.Equal(expected.AllowHotReload,           actual.AllowHotReload);
        Assert.Equal(expected.IsTestModule,             actual.IsTestModule);
        Assert.Equal(expected.DeprecationMessage,       actual.DeprecationMessage);
        Assert.Equal(expected.MinimumToolchainVersion,  actual.MinimumToolchainVersion);

        AssertSourceFileListEqual(expected.SourceFiles,       actual.SourceFiles,       $"{ctx}.SourceFiles");
        AssertStringListEqual(expected.PublicHeaders,         actual.PublicHeaders,     $"{ctx}.PublicHeaders");
        AssertStringListEqual(expected.PrivateHeaders,        actual.PrivateHeaders,    $"{ctx}.PrivateHeaders");
        AssertStringListEqual(expected.InternalHeaders,       actual.InternalHeaders,   $"{ctx}.InternalHeaders");
        AssertStringListEqual(expected.CSharpSources,         actual.CSharpSources,     $"{ctx}.CSharpSources");
        AssertStringListEqual(expected.IncludePaths,          actual.IncludePaths,      $"{ctx}.IncludePaths");
        AssertStringListEqual(expected.PublicDefines,         actual.PublicDefines,     $"{ctx}.PublicDefines");
        AssertModuleDepListEqual(expected.ModuleDependencies, actual.ModuleDependencies, $"{ctx}.ModuleDependencies");
    }

    private static void AssertStringListEqual(IReadOnlyList<string> a, IReadOnlyList<string> b, string ctx)
    {
        Assert.True(a.Count == b.Count, $"{ctx} count mismatch: {a.Count} vs {b.Count}");
        for (int i = 0; i < a.Count; i++)
        {
            Assert.True(a[i] == b[i], $"{ctx}[{i}] mismatch: \"{a[i]}\" vs \"{b[i]}\"");
        }
    }

    private static void AssertSourceFileListEqual(IReadOnlyList<SourceFile> a, IReadOnlyList<SourceFile> b, string ctx)
    {
        Assert.True(a.Count == b.Count, $"{ctx} count mismatch: {a.Count} vs {b.Count}");
        for (int i = 0; i < a.Count; i++)
        {
            // SourceFile is itself a positional record over scalar primitives,
            // so the default record equality is value-based and correct.
            Assert.Equal(a[i], b[i]);
        }
    }

    private static void AssertModuleDepListEqual(IReadOnlyList<ModuleDep> a, IReadOnlyList<ModuleDep> b, string ctx)
    {
        Assert.True(a.Count == b.Count, $"{ctx} count mismatch: {a.Count} vs {b.Count}");
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i], b[i]);
        }
    }
}
