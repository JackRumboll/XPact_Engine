// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Linq;
using System.Text;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Manifest;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Configuration;

/// <summary>
/// Exercises the Phase 1 Roslyn escape hatch for module descriptors per
/// Toolchain Contract Rev 13 Section 9.6 and <c>/Documents/XBT.html</c>
/// Rev 4 Section 3.6.
/// </summary>
/// <remarks>
/// <para>
/// All tests scaffold .Build.cs source files into an isolated scratch
/// directory and pass an explicit <c>cacheDirectory</c> to
/// <see cref="BuildCsCompiler.Compile"/> so the test suite never reads
/// or writes the repo's actual <c>Intermediate/Build/XBT/BuildCsCache/</c>
/// directory and so concurrent test runs do not collide.
/// </para>
/// <para>
/// The descriptor source has the Simgenics copyright header on the first
/// line per master plan Section 2 Copyright / licensing row. The header
/// flows verbatim into the Roslyn compile input; Roslyn parses it as a
/// trailing-comment line and ignores it.
/// </para>
/// </remarks>
public sealed class BuildCsCompilerTests : IDisposable
{
    private readonly string _scratchDir;
    private readonly string _cacheDir;

    public BuildCsCompilerTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.BuildCsCompiler",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchDir);
        _cacheDir = Path.Combine(_scratchDir, "Intermediate", "Build", "XBT", "BuildCsCache");
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratchDir))
            {
                Directory.Delete(_scratchDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup. Roslyn-loaded DLLs hold a runtime
            // reference for the process lifetime but we read bytes
            // rather than file-mapping so the source DLL on disk is
            // typically removable; if not, the OS reaps the scratch
            // tree eventually.
        }
    }

    // ---------------------------------------------------------------------
    // 1. Simple compile -- happy path.
    // ---------------------------------------------------------------------

    [Fact]
    public void SimpleCompile_PopulatesNameAndTier()
    {
        string source = """
            // Copyright Simgenics. All Rights Reserved.
            using Simgenics.XPact.XBT.Configuration;
            using Simgenics.XPact.XBT.Manifest;

            public sealed class XCoreBuild : ModuleRules
            {
                public XCoreBuild(TargetRules target)
                {
                    Name = "XCore";
                    Tier = ModuleTier.Engine;
                    ModuleType = ModuleType.Runtime;
                }
            }
            """;
        string path = WriteBuildCs("XCore", source);
        TargetRules target = MakeTarget();

        ModuleRules rules = BuildCsCompiler.Compile(path, target, _cacheDir);

        Assert.Equal("XCore", rules.Name);
        Assert.Equal(ModuleTier.Engine, rules.Tier);
        Assert.Equal(ModuleType.Runtime, rules.ModuleType);
    }

    // ---------------------------------------------------------------------
    // 2. Conditional on target.Platform -- both branches.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(Platform.Win64, true)]
    [InlineData(Platform.Linux, false)]
    public void PlatformConditional_BothBranches(Platform platform, bool shouldDefineWin)
    {
        string source = """
            // Copyright Simgenics. All Rights Reserved.
            using Simgenics.XPact.XBT.Configuration;
            using Simgenics.XPact.XBT.Manifest;

            public sealed class PlatformGate : ModuleRules
            {
                public PlatformGate(TargetRules target)
                {
                    Name = "PlatformGate";
                    Tier = ModuleTier.Engine;
                    ModuleType = ModuleType.Runtime;
                    if (target.Platform == Platform.Win64)
                    {
                        PublicDefinitions.Add("X_PLATFORM_WIN=1");
                    }
                }
            }
            """;
        string path = WriteBuildCs("PlatformGate_" + platform, source);
        TargetRules target = MakeTarget(platform: platform);

        ModuleRules rules = BuildCsCompiler.Compile(path, target, _cacheDir);

        if (shouldDefineWin)
        {
            Assert.Contains("X_PLATFORM_WIN=1", rules.PublicDefinitions);
        }
        else
        {
            Assert.DoesNotContain("X_PLATFORM_WIN=1", rules.PublicDefinitions);
        }
    }

    // ---------------------------------------------------------------------
    // 3. Conditional on target.FipsMode.
    // ---------------------------------------------------------------------

    [Fact]
    public void FipsModeConditional_AddsExtraDependency()
    {
        string source = """
            // Copyright Simgenics. All Rights Reserved.
            using Simgenics.XPact.XBT.Configuration;
            using Simgenics.XPact.XBT.Manifest;

            public sealed class XCryptoBuild : ModuleRules
            {
                public XCryptoBuild(TargetRules target)
                {
                    Name = "XCrypto";
                    Tier = ModuleTier.Engine;
                    ModuleType = ModuleType.Runtime;
                    PublicDependencyModuleNames.Add(new ModuleDep("XCore", false));
                    if (target.FipsMode)
                    {
                        PublicDependencyModuleNames.Add(new ModuleDep("XSecurity", false));
                    }
                }
            }
            """;
        string path = WriteBuildCs("XCrypto", source);

        ModuleRules withoutFips = BuildCsCompiler.Compile(path, MakeTarget(fipsMode: false), _cacheDir);
        Assert.DoesNotContain(withoutFips.PublicDependencyModuleNames, d => d.Name == "XSecurity");
        Assert.Contains(withoutFips.PublicDependencyModuleNames, d => d.Name == "XCore");

        ModuleRules withFips = BuildCsCompiler.Compile(path, MakeTarget(fipsMode: true), _cacheDir);
        Assert.Contains(withFips.PublicDependencyModuleNames, d => d.Name == "XSecurity");
        Assert.Contains(withFips.PublicDependencyModuleNames, d => d.Name == "XCore");
    }

    // ---------------------------------------------------------------------
    // 4. Syntax error -- missing semicolon.
    // ---------------------------------------------------------------------

    [Fact]
    public void SyntaxError_ThrowsWithLineInformation()
    {
        string source = """
            // Copyright Simgenics. All Rights Reserved.
            using Simgenics.XPact.XBT.Configuration;

            public sealed class BadSyntax : ModuleRules
            {
                public BadSyntax(TargetRules target)
                {
                    Name = "BadSyntax"
                }
            }
            """;
        string path = WriteBuildCs("BadSyntax", source);

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildCsCompiler.Compile(path, MakeTarget(), _cacheDir));

        Assert.Equal(30, ex.ExitCode);
        // Roslyn anchors the "; expected" diagnostic at the missing-semi
        // span (line 7) or at the start of the next token on line 8;
        // either is acceptable. Both lines surface in the formatted
        // diagnostic via the "path:line:col" MSBuild-style prefix.
        Assert.True(
            ex.Message.Contains(":7:", StringComparison.Ordinal)
                || ex.Message.Contains(":8:", StringComparison.Ordinal),
            $"Expected a diagnostic line number 7 or 8; got: {ex.Message}");
        Assert.Matches(@"error CS\d{4}", ex.Message);
    }

    // ---------------------------------------------------------------------
    // 5. Unresolved reference -- undefined symbol.
    // ---------------------------------------------------------------------

    [Fact]
    public void UnresolvedReference_NamesTheSymbol()
    {
        string source = """
            // Copyright Simgenics. All Rights Reserved.
            using Simgenics.XPact.XBT.Configuration;
            using Simgenics.XPact.XBT.Manifest;

            public sealed class Unresolved : ModuleRules
            {
                public Unresolved(TargetRules target)
                {
                    Name = "Unresolved";
                    Tier = ModuleTier.Engine;
                    SomeBogusIdentifier();
                }
            }
            """;
        string path = WriteBuildCs("Unresolved", source);

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildCsCompiler.Compile(path, MakeTarget(), _cacheDir));

        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("SomeBogusIdentifier", ex.Message);
    }

    // ---------------------------------------------------------------------
    // 6. No ModuleRules subclass.
    // ---------------------------------------------------------------------

    [Fact]
    public void NoModuleRulesSubclass_ProducesDescriptorParseException()
    {
        // A file with only an unrelated public class -- compiles but
        // discovery finds zero ModuleRules subclasses.
        string source = """
            // Copyright Simgenics. All Rights Reserved.
            public sealed class NotADescriptor
            {
                public int Answer => 42;
            }
            """;
        string path = WriteBuildCs("NoSubclass", source);

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildCsCompiler.Compile(path, MakeTarget(), _cacheDir));

        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("No ModuleRules subclass", ex.Message);
    }

    // ---------------------------------------------------------------------
    // 7. Multiple ModuleRules subclasses.
    // ---------------------------------------------------------------------

    [Fact]
    public void MultipleModuleRulesSubclasses_ProducesDescriptorParseException()
    {
        string source = """
            // Copyright Simgenics. All Rights Reserved.
            using Simgenics.XPact.XBT.Configuration;
            using Simgenics.XPact.XBT.Manifest;

            public sealed class FirstRules : ModuleRules
            {
                public FirstRules(TargetRules target)
                {
                    Name = "First";
                    Tier = ModuleTier.Engine;
                }
            }

            public sealed class SecondRules : ModuleRules
            {
                public SecondRules(TargetRules target)
                {
                    Name = "Second";
                    Tier = ModuleTier.Engine;
                }
            }
            """;
        string path = WriteBuildCs("MultipleSubclasses", source);

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildCsCompiler.Compile(path, MakeTarget(), _cacheDir));

        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("Multiple ModuleRules subclasses", ex.Message);
        Assert.Contains("FirstRules", ex.Message);
        Assert.Contains("SecondRules", ex.Message);
    }

    // ---------------------------------------------------------------------
    // 8. Constructor throws -- surfaces line info when available.
    // ---------------------------------------------------------------------

    [Fact]
    public void ConstructorThrows_DescriptorParseExceptionCarriesContext()
    {
        string source = """
            // Copyright Simgenics. All Rights Reserved.
            using System;
            using Simgenics.XPact.XBT.Configuration;
            using Simgenics.XPact.XBT.Manifest;

            public sealed class ThrowingRules : ModuleRules
            {
                public ThrowingRules(TargetRules target)
                {
                    Name = "Throwing";
                    Tier = ModuleTier.Engine;
                    throw new InvalidOperationException("nope");
                }
            }
            """;
        string path = WriteBuildCs("Throwing", source);

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildCsCompiler.Compile(path, MakeTarget(), _cacheDir));

        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("InvalidOperationException", ex.Message);
        Assert.Contains("nope", ex.Message);
        // The throw lives on a known line in the .Build.cs source. We
        // accept either an "at line N" annotation (PDB walker resolved
        // the frame) or the "during constructor execution" fallback
        // (PDB lookup unavailable on this runtime); both are contract-
        // legal. The exact line is documented to be near the body of
        // the constructor, which is past line 7 -- assert positivity
        // on the line value so we know the walker picked the user
        // frame, not a TPA frame at line 0.
        if (ex.Line is not null)
        {
            Assert.True(
                ex.Line >= 5,
                $"PDB line lookup should land on a user-source line; got {ex.Line}.");
            Assert.Contains("at line", ex.Message);
        }
        else
        {
            Assert.Contains("during constructor execution", ex.Message);
        }
    }

    // ---------------------------------------------------------------------
    // 9. Cache hit -- second invocation does not recompile.
    // ---------------------------------------------------------------------

    [Fact]
    public void CacheHit_SecondCompileReusesCachedDll()
    {
        string source = """
            // Copyright Simgenics. All Rights Reserved.
            using Simgenics.XPact.XBT.Configuration;
            using Simgenics.XPact.XBT.Manifest;

            public sealed class CachedRules : ModuleRules
            {
                public CachedRules(TargetRules target)
                {
                    Name = "Cached";
                    Tier = ModuleTier.Engine;
                }
            }
            """;
        string path = WriteBuildCs("Cached", source);
        TargetRules target = MakeTarget();

        // First call: cache miss; compile + write.
        ModuleRules first = BuildCsCompiler.Compile(path, target, _cacheDir);
        Assert.Equal("Cached", first.Name);

        string[] dllsAfterFirst = Directory.GetFiles(_cacheDir, "*.dll");
        Assert.Single(dllsAfterFirst);
        // Compare content-addressable identity: a cache hit means the
        // on-disk DLL bytes are unchanged. mtime is banned for
        // invalidation engine-wide per Toolchain Contract Rev 13
        // Section 2.1 (footgun #1); the test must not lean on it
        // either or it implicitly endorses the very signal the
        // contract forbids. Content equality is the only true
        // invariant the cache promises.
        byte[] bytesAfterFirst = File.ReadAllBytes(dllsAfterFirst[0]);

        // Second call with identical source: cache hit.
        ModuleRules second = BuildCsCompiler.Compile(path, target, _cacheDir);
        Assert.Equal("Cached", second.Name);

        string[] dllsAfterSecond = Directory.GetFiles(_cacheDir, "*.dll");
        Assert.Single(dllsAfterSecond);
        Assert.Equal(dllsAfterFirst[0], dllsAfterSecond[0]);
        byte[] bytesAfterSecond = File.ReadAllBytes(dllsAfterSecond[0]);
        Assert.Equal(bytesAfterFirst, bytesAfterSecond);
    }

    // ---------------------------------------------------------------------
    // 10. Cache invalidation -- modified source produces a different DLL.
    // ---------------------------------------------------------------------

    [Fact]
    public void CacheInvalidation_ModifiedSourceTriggersRecompile()
    {
        string sourceV1 = """
            // Copyright Simgenics. All Rights Reserved.
            using Simgenics.XPact.XBT.Configuration;
            using Simgenics.XPact.XBT.Manifest;

            public sealed class Mutable : ModuleRules
            {
                public Mutable(TargetRules target)
                {
                    Name = "MutableA";
                    Tier = ModuleTier.Engine;
                }
            }
            """;
        string sourceV2 = """
            // Copyright Simgenics. All Rights Reserved.
            using Simgenics.XPact.XBT.Configuration;
            using Simgenics.XPact.XBT.Manifest;

            public sealed class Mutable : ModuleRules
            {
                public Mutable(TargetRules target)
                {
                    Name = "MutableB";
                    Tier = ModuleTier.Engine;
                }
            }
            """;
        string path = WriteBuildCs("Mutable", sourceV1);
        TargetRules target = MakeTarget();

        ModuleRules first = BuildCsCompiler.Compile(path, target, _cacheDir);
        Assert.Equal("MutableA", first.Name);
        string[] dllsV1 = Directory.GetFiles(_cacheDir, "*.dll");
        Assert.Single(dllsV1);
        string hashV1 = Path.GetFileNameWithoutExtension(dllsV1[0]);
        byte[] bytesV1 = File.ReadAllBytes(dllsV1[0]);

        File.WriteAllText(path, sourceV2, new UTF8Encoding(false));

        ModuleRules second = BuildCsCompiler.Compile(path, target, _cacheDir);
        Assert.Equal("MutableB", second.Name);

        string[] dllsV2 = Directory.GetFiles(_cacheDir, "*.dll");
        Assert.Equal(2, dllsV2.Length); // V1 entry still present; V2 newly written
        string[] hashesV2 = dllsV2.Select(Path.GetFileNameWithoutExtension).ToArray()!;
        Assert.Contains(hashV1, hashesV2);
        Assert.Contains(hashesV2, h => h != hashV1);

        // The newly-cached DLL must have different bytes from the
        // original. Pure content comparison -- never mtime -- per
        // Toolchain Contract Rev 13 Section 2.1.
        string newDllPath = dllsV2.Single(d => Path.GetFileNameWithoutExtension(d) != hashV1);
        byte[] bytesV2 = File.ReadAllBytes(newDllPath);
        Assert.NotEqual(bytesV1, bytesV2);
    }

    // ---------------------------------------------------------------------
    // 11. Constructor signature mismatch (no public ctor with TargetRules).
    // ---------------------------------------------------------------------

    [Fact]
    public void MissingConstructor_DescriptorParseException()
    {
        // ModuleRules subclass present but with a different ctor signature.
        string source = """
            // Copyright Simgenics. All Rights Reserved.
            using Simgenics.XPact.XBT.Configuration;
            using Simgenics.XPact.XBT.Manifest;

            public sealed class WrongCtor : ModuleRules
            {
                public WrongCtor()
                {
                    Name = "WrongCtor";
                    Tier = ModuleTier.Engine;
                }
            }
            """;
        string path = WriteBuildCs("WrongCtor", source);

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildCsCompiler.Compile(path, MakeTarget(), _cacheDir));
        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("WrongCtor", ex.Message);
        Assert.Contains("TargetRules target", ex.Message);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private string WriteBuildCs(string moduleName, string source)
    {
        string moduleDir = Path.Combine(_scratchDir, moduleName);
        Directory.CreateDirectory(moduleDir);
        string path = Path.Combine(moduleDir, moduleName + ".Build.cs");
        File.WriteAllText(path, source, new UTF8Encoding(false));
        return path;
    }

    private static TargetRules MakeTarget(
        Platform platform = Platform.Win64,
        bool fipsMode = false,
        BuildConfiguration configuration = BuildConfiguration.Development,
        StationRole stationRole = StationRole.Engineer)
        => new()
        {
            Name = "TestTarget",
            TargetType = BuildTargetType.Editor,
            Configuration = configuration,
            Platform = platform,
            Architecture = "x86_64",
            StationRole = stationRole,
            FipsMode = fipsMode,
        };
}
