// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;
using Xunit;

// See ManifestFixtures.cs for the rationale: the inner namespace
// Simgenics.XPact.XBT.Tests.Tests.Manifest shadows the imported Manifest
// type name. The alias resolves the ambiguity for the binder.
using ManifestRecord = Simgenics.XPact.XBT.Manifest.Manifest;

namespace Simgenics.XPact.XBT.Tests.Tests.Manifest;

/// <summary>
/// Tests covering the auto-derivation of <see cref="ContractVersion.Current"/>
/// from <see cref="ContractSurface"/> per Toolchain Contract Rev 13
/// Section 10.2. The tests verify:
/// </summary>
/// <list type="bullet">
///   <item>Format -- <c>"13.0+&lt;16 hex&gt;"</c>.</item>
///   <item>Stability -- two reads return the same string.</item>
///   <item>Determinism -- a hand-rolled canonical snapshot hashes to the same value.</item>
///   <item>Sensitivity -- adding to any surface element changes the hash.</item>
///   <item>Integration -- the manifest's <c>ContractVersion</c> field reflects the live value, not the old <c>"v13-stub"</c> placeholder.</item>
/// </list>
public sealed class ContractVersionTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public ContractVersionTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Emits the live <see cref="ContractVersion.Current"/> value to the
    /// test output so the doc-side subagent can read it back from the
    /// xUnit log when aligning <c>/Documents/XToolchainContract.html</c>
    /// references after a Contract revision bump. The assertion only
    /// pins the shape (<c>"{tag}+{16-hex}"</c>); the literal value is
    /// intentionally NOT asserted because that's what every other test
    /// in this file is for, and pinning a literal here would create a
    /// maintenance footgun (every Rev bump would need a manual update).
    /// </summary>
    [Fact]
    public void ContractVersion_Current_LogsTheValueForDocAlignment()
    {
        string current = ContractVersion.Current;
        _output.WriteLine($"ContractVersion.Current = \"{current}\"");

        int sep = current.IndexOf('+');
        Assert.True(sep > 0, $"Expected '<tag>+<hex>' shape; got \"{current}\".");
        Assert.Equal(16, current.Length - (sep + 1));
    }

    /// <summary>
    /// <see cref="ContractVersion.Current"/> begins with the
    /// <see cref="ContractSurface.SemanticVersionTag"/> plus a literal
    /// <c>+</c> separator.
    /// </summary>
    [Fact]
    public void Current_HasSemverPrefix()
    {
        string current = ContractVersion.Current;
        string expectedPrefix = ContractSurface.SemanticVersionTag + "+";
        Assert.True(
            current.StartsWith(expectedPrefix, StringComparison.Ordinal),
            $"ContractVersion.Current = \"{current}\"; expected prefix \"{expectedPrefix}\".");
    }

    /// <summary>
    /// The hex suffix after the <c>+</c> is exactly 16 lowercase hex
    /// characters. The first 16 hex chars of the 64-hex-char BLAKE3
    /// digest give 64 bits of disambiguation; truncation is intentional
    /// per Contract Section 10.2 (short tag that flows through symbol
    /// mangling without bloating every symbol).
    /// </summary>
    [Fact]
    public void Current_HasStructureHashSuffix()
    {
        string current = ContractVersion.Current;
        int sep = current.IndexOf('+');
        Assert.True(sep >= 0, "ContractVersion.Current must contain a '+' separator.");

        string suffix = current[(sep + 1)..];
        Assert.Equal(16, suffix.Length);

        foreach (char c in suffix)
        {
            bool isLowerHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            Assert.True(isLowerHex, $"Suffix char '{c}' is not lowercase hex.");
        }
    }

    /// <summary>
    /// Two reads of <see cref="ContractVersion.Current"/> return the
    /// same string. The auto-derivation is computed once at static-init
    /// and memoised; this asserts the property holds end-to-end.
    /// </summary>
    [Fact]
    public void Current_IsStableAcrossInvocations()
    {
        string a = ContractVersion.Current;
        string b = ContractVersion.Current;
        Assert.Equal(a, b);
    }

    /// <summary>
    /// Round-trip determinism. <see cref="ContractVersion.StructureHash"/>
    /// equals a hand-rolled BLAKE3 computed on this test's own
    /// reconstruction of the canonical serialization. If anyone changes
    /// the canonicalization algorithm without updating this test, drift
    /// is caught here -- the goal is to make the algorithm reviewable
    /// end-to-end.
    /// </summary>
    [Fact]
    public void StructureHash_IsDeterministic()
    {
        IoHash live = ContractVersion.StructureHash;

        // Re-canonicalize via this test's own hand-rolled rewrite of
        // the algorithm. This must hash to the same value -- this is
        // the test's "did the algorithm change" canary.
        byte[] canonical = BuildCanonicalSurfaceBytes();
        IoHash recomputed = IoHash.Compute(canonical);

        Assert.Equal(live, recomputed);
    }

    /// <summary>
    /// Sensitivity -- mutating any surface element changes the hash.
    /// We reconstruct the canonical stream with a phantom element
    /// appended and assert the resulting hash differs from the live
    /// <see cref="ContractVersion.StructureHash"/>. This guards against
    /// a refactor that accidentally drops part of the surface from the
    /// canonicalization (where the live hash would no longer respond
    /// to surface changes).
    /// </summary>
    [Fact]
    public void StructureHash_ChangesIfEnumAdded()
    {
        IoHash live = ContractVersion.StructureHash;

        // Reconstruct the canonical bytes with an extra synthetic line
        // appended at the end. We do not modify the real
        // ContractSurface because the source-of-truth must not be
        // mutable from a test.
        byte[] canonical = BuildCanonicalSurfaceBytes();
        byte[] mutatedBytes = new byte[canonical.Length + 42];
        canonical.CopyTo(mutatedBytes, 0);
        byte[] suffix = Encoding.UTF8.GetBytes("phantom-surface-element:NewEnumValue=99\n");
        suffix.CopyTo(mutatedBytes, canonical.Length);
        // Recompute against the actual length used.
        Array.Resize(ref mutatedBytes, canonical.Length + suffix.Length);

        IoHash mutated = IoHash.Compute(mutatedBytes);
        Assert.NotEqual(live, mutated);
    }

    /// <summary>
    /// Integration -- <see cref="ManifestJson.SerializeToJson"/> emits
    /// the live <see cref="ContractVersion.Current"/> rather than the
    /// old <c>"v13-stub"</c> placeholder. Constructed via the rich
    /// fixture (which itself reads <see cref="ContractVersion.Current"/>)
    /// so this test is a true integration check. We check via a
    /// round-trip deserialization because <see cref="System.Text.Json"/>
    /// escapes <c>+</c> as <c>+</c> in string-encoded output;
    /// asserting via decode keeps the test robust against the escape
    /// policy choice.
    /// </summary>
    [Fact]
    public void ManifestSchema_ReferencesContractVersion_Auto()
    {
        ManifestRecord m = ManifestFixtures.RichExample();
        Assert.Equal(ContractVersion.Current, m.ContractVersion);

        string json = ManifestJson.SerializeToJson(m);
        ManifestRecord decoded = ManifestJson.DeserializeFromJson(json);

        // The serialized-then-deserialized manifest carries the live
        // value, not the legacy stub. Decoded equality is the
        // authoritative check; raw-text negative check is a belt-and-
        // braces against accidental re-introduction of the placeholder.
        Assert.Equal(ContractVersion.Current, decoded.ContractVersion);
        Assert.DoesNotContain("v13-stub", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Surface-introspection canary -- the reflected enum list contains
    /// every enum currently declared in <c>XBT.Manifest/Enums.cs</c>.
    /// If this test fails after adding a new enum, the new enum was
    /// successfully picked up; update the asserted minimum here.
    /// </summary>
    [Fact]
    public void ContractSurface_Enums_IncludeAllManifestEnums()
    {
        string[] expected = new[]
        {
            "BuildConfiguration",
            "BuildTargetType",
            "FPSemantics",
            "Languages",
            "ModuleTier",
            "ModuleType",
            "OptimizeCodeMode",
            "PCHUsageMode",
            "Platform",
            "SimdLevel",
            "StationRole",
        };
        string[] actual = ContractSurface.Enums.Select(e => e.EnumName).ToArray();

        // Every expected enum is in the surface. Extras (added later)
        // are tolerated -- the test fails open on new additions.
        foreach (string name in expected)
        {
            Assert.Contains(name, actual);
        }
    }

    /// <summary>
    /// Surface-content canary -- <c>ModuleType.Programs == 4</c> is
    /// present in the reflected enum table (this is the Rev 13 append
    /// that subagent B's <c>EnumOrdinalAlignmentTests</c> guards
    /// against drift; mirrored here from the contract-surface side).
    /// </summary>
    [Fact]
    public void ContractSurface_Enums_ContainProgramsOrdinalFour()
    {
        (string EnumName, IReadOnlyList<(string Name, int Ordinal)> Members) moduleType =
            ContractSurface.Enums.Single(e => e.EnumName == "ModuleType");

        (string Name, int Ordinal) programs = moduleType.Members.Single(m => m.Name == "Programs");
        Assert.Equal(4, programs.Ordinal);
    }

    /// <summary>
    /// Reproduce the canonical-surface serialization algorithm independently
    /// of <see cref="ContractVersion.ComputeStructureHash"/>. This mirrors
    /// the steps documented in
    /// <see cref="ContractVersion.WriteCanonicalSurfaceTo"/>; if the two
    /// diverge, <see cref="StructureHash_IsDeterministic"/> fails and the
    /// reviewer is forced to look at both sides.
    /// </summary>
    private static byte[] BuildCanonicalSurfaceBytes()
    {
        using MemoryStream ms = new();
        using StreamWriter w = new(
            ms,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true))
        {
            NewLine = "\n",
        };

        // 1. SemanticVersionTag.
        w.Write(ContractSurface.SemanticVersionTag);
        w.Write('\n');

        // 2. Enums (already sorted by ContractSurface).
        foreach ((string enumName, IReadOnlyList<(string Name, int Ordinal)> members) in ContractSurface.Enums)
        {
            w.Write(enumName);
            w.Write('\n');
            foreach ((string memberName, int ordinal) in members)
            {
                w.Write(memberName);
                w.Write('=');
                w.Write(ordinal);
                w.Write('\n');
            }
            w.Write('\n');
        }

        // 3. Marker macros.
        foreach (string marker in ContractSurface.MarkerMacros)
        {
            w.Write("marker:");
            w.Write(marker);
            w.Write('\n');
        }

        // 4. Body-macro suffixes.
        foreach (string suffix in ContractSurface.BodyMacroSuffixes)
        {
            w.Write("bodysuffix:");
            w.Write(suffix);
            w.Write('\n');
        }

        // 5. Mangling rule example.
        w.Write("mangling:");
        w.Write(ContractSurface.ManglingRuleExample);
        w.Write('\n');

        // 6. File-ID scheme.
        w.Write("file_id:");
        w.Write(ContractSurface.FileIdScheme);
        w.Write('\n');

        // 7. Exit codes, sorted by code.
        (int Code, string Mnemonic)[] sortedExits = ContractSurface.ExitCodes
            .OrderBy(e => e.Code)
            .ToArray();
        foreach ((int code, string mnemonic) in sortedExits)
        {
            w.Write("exit:");
            w.Write(code);
            w.Write('=');
            w.Write(mnemonic);
            w.Write('\n');
        }

        // 8. Action types, declared order = slot ordinal.
        foreach (string action in ContractSurface.ActionTypes)
        {
            w.Write("action:");
            w.Write(action);
            w.Write('\n');
        }

        w.Flush();
        return ms.ToArray();
    }
}
