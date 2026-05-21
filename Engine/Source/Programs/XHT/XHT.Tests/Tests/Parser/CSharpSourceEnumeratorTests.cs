// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Simgenics.XPact.XHT.Manifest;
using Simgenics.XPact.XHT.Parser.CSharp;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Parser;

/// <summary>
/// Tests for <see cref="CSharpSourceEnumerator"/>. Determinism gate for
/// C# source-file ordering per <c>/Documents/XHT.html</c> Rev 7
/// Section 11.4 (byte-identical output under parallelism).
/// </summary>
public class CSharpSourceEnumeratorTests
{
    [Fact]
    public void EnumerateOrdered_RawPaths_SortsOrdinal()
    {
        IReadOnlyList<string> input = new[] { "Zebra.cs", "Apple.cs", "Mango.cs" };
        IReadOnlyList<string> sorted = CSharpSourceEnumerator.EnumerateOrdered(input);
        Assert.Equal(new[] { "Apple.cs", "Mango.cs", "Zebra.cs" }, sorted);
    }

    [Fact]
    public void EnumerateOrdered_EmptyInput_ReturnsEmpty()
    {
        IReadOnlyList<string> sorted = CSharpSourceEnumerator.EnumerateOrdered(
            System.Array.Empty<string>());
        Assert.Empty(sorted);
    }

    [Fact]
    public void EnumerateOrdered_IsCaseSensitive()
    {
        // 'A' (0x41) < 'a' (0x61) under StringComparer.Ordinal. The
        // sort must NOT collapse case differences.
        IReadOnlyList<string> input = new[] { "alpha.cs", "Beta.cs", "Charlie.cs" };
        IReadOnlyList<string> sorted = CSharpSourceEnumerator.EnumerateOrdered(input);
        // Ordinal: 'B' (0x42) < 'C' (0x43) < 'a' (0x61) -> Beta, Charlie, alpha.
        Assert.Equal(new[] { "Beta.cs", "Charlie.cs", "alpha.cs" }, sorted);
    }

    [Fact]
    public void EnumerateOrdered_DeterministicAcrossCalls()
    {
        string[] input = { "Z.cs", "A.cs", "M.cs", "B.cs", "L.cs" };
        IReadOnlyList<string> first = CSharpSourceEnumerator.EnumerateOrdered(input);
        IReadOnlyList<string> second = CSharpSourceEnumerator.EnumerateOrdered(input);
        IReadOnlyList<string> third = CSharpSourceEnumerator.EnumerateOrdered(input);
        Assert.Equal(first, second);
        Assert.Equal(second, third);
    }

    [Fact]
    public void EnumerateOrdered_XbtModule_FiltersToCSharpSources()
    {
        XbtModule module = new(
            Name: "XScoring",
            Tier: ModuleTier.Engine,
            ModuleType: ModuleType.Runtime,
            Languages: Languages.Both,
            BaseDirectory: "Engine/Source/Runtime/XScoring",
            SourceFiles: new List<XbtSourceFile>
            {
                new("Public/XValve.h", false, true, false),
                new("Public/Valve.cs", true, false, false),
                new("Private/XValve.cpp", false, false, false),
                new("Private/Helpers.cs", true, false, false),
                new("Public/Actor.cs", true, false, false),
            },
            PublicHeaders: System.Array.Empty<string>(),
            PrivateHeaders: System.Array.Empty<string>(),
            InternalHeaders: System.Array.Empty<string>(),
            CSharpSources: System.Array.Empty<string>(),
            IncludePaths: System.Array.Empty<string>(),
            PublicDefines: System.Array.Empty<string>(),
            ModuleDependencies: System.Array.Empty<XbtModuleDep>(),
            GeneratedCPPFilenameBase: "XScoring",
            SimPath: false,
            EngineVersionCompat: "1.0.0",
            SimdLevel: SimdLevel.Default,
            PCHUsage: PCHUsageMode.Default,
            ExcludeFromSharedPCH: false,
            AllowHotReload: false,
            IsTestModule: false,
            DeprecationMessage: null,
            MinimumToolchainVersion: null);

        IReadOnlyList<string> sorted = CSharpSourceEnumerator.EnumerateOrdered(module);
        // Filters to 3 C# files; sorted ordinal.
        Assert.Equal(new[] { "Private/Helpers.cs", "Public/Actor.cs", "Public/Valve.cs" }, sorted);
    }

    [Fact]
    public void EnumerateOrdered_NullModule_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(
            () => CSharpSourceEnumerator.EnumerateOrdered((XbtModule)null!));
    }

    [Fact]
    public void EnumerateOrdered_NullPaths_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(
            () => CSharpSourceEnumerator.EnumerateOrdered((IEnumerable<string>)null!));
    }
}
