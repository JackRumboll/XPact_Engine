// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Emit.Output;
using Simgenics.XPact.XIL2CPP.Tiering;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit.Output;

/// <summary>
/// Tests for <see cref="Pass7Writer"/>: it writes <c>&lt;stem&gt;.cs.h</c> +
/// <c>&lt;stem&gt;.cs.cpp</c> at the expected <c>Transpiled/</c> paths and the
/// enriched <c>TierTable.partial.&lt;Module&gt;.json</c>, atomically (no leftover
/// temp files), returning the written paths.
/// </summary>
public sealed class Pass7WriterTests : IDisposable
{
    private readonly string _root;

    public Pass7WriterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "XIL2CPP-Pass7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private static TierTable Tier() => new("MyModule", new[]
    {
        new TierClassification(new StableId("Game.Widget.Compute(int):int"), "Game.Widget.Compute(int):int", FunctionTier.Tier2, string.Empty),
    });

    private static ManglingTable Mangle() => new("MyModule", "1ab12cd34", new[]
    {
        new ManglingRecord(
            new StableId("Game.Widget.Compute(int):int"),
            "_v1ab12cd34__Game::Widget::Compute_P_(R Game::Widget, V int)",
            "_v1ab12cd34__Game__Widget__Compute_P_R_Game__Widget_V_int",
            false, false, false, false, false, false),
    });

    [Fact]
    public void WriteOutputs_WritesHeaderAndSourceAtTranspiledPaths()
    {
        EmitResult result = new("Game/Widget.cs", "// header\n", "// source\n");
        IReadOnlyList<string> written = Pass7Writer.WriteOutputs(
            new[] { result }, Mangle(), Tier(), _root);

        string expectedHeader = Path.Combine(_root, "Transpiled", "Game", "Widget.cs.h");
        string expectedSource = Path.Combine(_root, "Transpiled", "Game", "Widget.cs.cpp");

        Assert.True(File.Exists(expectedHeader));
        Assert.True(File.Exists(expectedSource));
        Assert.Equal("// header\n", File.ReadAllText(expectedHeader));
        Assert.Equal("// source\n", File.ReadAllText(expectedSource));

        Assert.Contains(Path.GetFullPath(expectedHeader), written);
        Assert.Contains(Path.GetFullPath(expectedSource), written);
    }

    [Fact]
    public void WriteOutputs_WritesEnrichedTierTableAtModulePath()
    {
        IReadOnlyList<string> written = Pass7Writer.WriteOutputs(
            Array.Empty<EmitResult>(), Mangle(), Tier(), _root);

        string expectedTierTable = Path.Combine(_root, "TierTable.partial.MyModule.json");
        Assert.True(File.Exists(expectedTierTable));
        Assert.Contains(Path.GetFullPath(expectedTierTable), written);

        string json = File.ReadAllText(expectedTierTable);
        // The enrichment adds contractVersion + manglingV1.
        Assert.Contains("\"contractVersion\": \"1ab12cd34\"", json);
        Assert.Contains("\"manglingV1\": \"_v1ab12cd34__Game__Widget__Compute_P_R_Game__Widget_V_int\"", json);
    }

    [Fact]
    public void WriteOutputs_LeavesNoTempFiles()
    {
        Pass7Writer.WriteOutputs(
            new[] { new EmitResult("A.cs", "h", "s") }, Mangle(), Tier(), _root);

        string[] temps = Directory.GetFiles(_root, "*.tmp-*", SearchOption.AllDirectories);
        Assert.Empty(temps);
    }

    [Fact]
    public void WriteOutputs_NoBom()
    {
        Pass7Writer.WriteOutputs(
            new[] { new EmitResult("A.cs", "header", "source") }, Mangle(), Tier(), _root);

        byte[] headerBytes = File.ReadAllBytes(Path.Combine(_root, "Transpiled", "A.cs.h"));
        // UTF-8 BOM is EF BB BF; assert it is absent.
        Assert.False(headerBytes.Length >= 3 && headerBytes[0] == 0xEF && headerBytes[1] == 0xBB && headerBytes[2] == 0xBF);
    }

    [Fact]
    public void DeriveStem_DropsCsExtensionAndNormalisesSlashes()
    {
        Assert.Equal("Game/HealthPickup", Pass7Writer.DeriveStem("Game\\HealthPickup.cs"));
        Assert.Equal("Widget", Pass7Writer.DeriveStem("Widget.cs"));
    }

    [Fact]
    public void WriteOutputs_ReturnedPathsAreDeterministicForGivenOrder()
    {
        EmitResult[] results =
        {
            new("B.cs", "h", "s"),
            new("A.cs", "h", "s"),
        };

        IReadOnlyList<string> first = Pass7Writer.WriteOutputs(results, Mangle(), Tier(), _root);
        IReadOnlyList<string> second = Pass7Writer.WriteOutputs(results, Mangle(), Tier(), _root);

        Assert.Equal(first.ToArray(), second.ToArray());
    }
}
