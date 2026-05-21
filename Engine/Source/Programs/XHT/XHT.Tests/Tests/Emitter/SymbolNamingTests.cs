// Copyright Simgenics. All Rights Reserved.

using System;
using Simgenics.XPact.XHT.Emitter;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Emitter;

/// <summary>
/// Tests for <see cref="SymbolNaming"/>. Verifies the encoder matches
/// the locked example string from Contract Section 1.4 + XHT.html
/// Section 10.4 byte-for-byte.
/// </summary>
public class SymbolNamingTests
{
    /// <summary>The locked worked example from Contract Section 1.4 + XHT.html Section 10.4.</summary>
    private const string LockedExample
        = "_XID_N6EngineN4N14XGameFrameworkN6PublicN6ValvesN6XValve_L14_GENERATED_BODY";

    [Fact]
    public void FileId_LockedExample_MatchesContractRev134_ByteForByte()
    {
        string id = SymbolNaming.FileId(
            pluginName: "Engine",
            logicalPathSegments: new[] { "XGameFramework", "Public", "Valves", "XValve" },
            lineNumber: 14,
            suffix: "_GENERATED_BODY");

        Assert.Equal(LockedExample, id);
    }

    [Fact]
    public void FileId_OtherSuffixes_AtSameSite_MatchExample()
    {
        string[] suffixes = { "_INCLASS", "_RPC_WRAPPERS", "_ACCESSORS", "_FIELDNOTIFY", "_VINTERFACES", "_STANDARD_CONSTRUCTORS" };

        foreach (string s in suffixes)
        {
            string id = SymbolNaming.FileId(
                pluginName: "Engine",
                logicalPathSegments: new[] { "XGameFramework", "Public", "Valves", "XValve" },
                lineNumber: 14,
                suffix: s);
            string expected = "_XID_N6EngineN4N14XGameFrameworkN6PublicN6ValvesN6XValve_L14" + s;
            Assert.Equal(expected, id);
        }
    }

    [Fact]
    public void FileId_DifferentLineNumber_ChangesSymbolDeterministically()
    {
        string a = SymbolNaming.FileId("Engine", new[] { "Foo" }, lineNumber: 1, "_GENERATED_BODY");
        string b = SymbolNaming.FileId("Engine", new[] { "Foo" }, lineNumber: 2, "_GENERATED_BODY");
        Assert.NotEqual(a, b);
        Assert.Contains("_L1_", a);
        Assert.Contains("_L2_", b);
    }

    [Fact]
    public void FileId_TwoIdenticalInputs_ProduceByteIdenticalOutput()
    {
        string a = SymbolNaming.FileId("X", new[] { "S" }, 3, "_FOO");
        string b = SymbolNaming.FileId("X", new[] { "S" }, 3, "_FOO");
        Assert.Equal(a, b);
    }

    [Fact]
    public void FileId_SegmentLength_IsLengthPrefixedInDecimal()
    {
        // 14-character segment encodes as "N14<14chars>" per the Itanium-
        // ABI grammar. A 6-character segment encodes as "N6<6chars>".
        string id = SymbolNaming.FileId(
            pluginName: "Engine",
            logicalPathSegments: new[] { "ABCDEFGHIJKLMN" }, // 14 chars
            lineNumber: 1,
            suffix: "_X");
        Assert.Contains("N1N14ABCDEFGHIJKLMN_L1_X", id);
    }

    [Fact]
    public void FileId_NonAlnumSegmentByte_IsHexEscaped()
    {
        // A dash in a segment encodes as "X2d" (2-digit hex of 0x2d).
        string id = SymbolNaming.FileId(
            pluginName: "Engine",
            logicalPathSegments: new[] { "A-B" },
            lineNumber: 1,
            suffix: "_X");
        // "A-B" -> "AX2dB" (5 chars), so segment is "N5AX2dB".
        Assert.Contains("N5AX2dB", id);
    }

    [Fact]
    public void FileId_RejectsNullArgs()
    {
        Assert.Throws<ArgumentNullException>(() => SymbolNaming.FileId(null!, new[] { "S" }, 1, "_X"));
        Assert.Throws<ArgumentNullException>(() => SymbolNaming.FileId("E", null!, 1, "_X"));
        Assert.Throws<ArgumentNullException>(() => SymbolNaming.FileId("E", new[] { "S" }, 1, null!));
    }

    [Fact]
    public void FileId_RejectsEmptyPluginName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SymbolNaming.FileId("", new[] { "S" }, 1, "_X"));
    }

    [Fact]
    public void FileId_RejectsSuffixWithoutLeadingUnderscore_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => SymbolNaming.FileId("E", new[] { "S" }, 1, "GENERATED_BODY"));
    }

    [Fact]
    public void FileId_RejectsNegativeLineNumber_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SymbolNaming.FileId("E", new[] { "S" }, -1, "_X"));
    }

    [Fact]
    public void SingletonGetter_LockedExample_MatchesContractRev102()
    {
        // From XHT.html Section 10.4: "Z_Construct_XClass_XGameFramework_XValve".
        string g = SymbolNaming.SingletonGetter("XGameFramework", "XValve", EngineRole.Class);
        Assert.Equal("Z_Construct_XClass_XGameFramework_XValve", g);
    }

    [Fact]
    public void SingletonGetter_AllRoles_HaveCorrectXKindToken()
    {
        Assert.Equal("Z_Construct_XClass_M_T", SymbolNaming.SingletonGetter("M", "T", EngineRole.Class));
        Assert.Equal("Z_Construct_XStruct_M_T", SymbolNaming.SingletonGetter("M", "T", EngineRole.Struct));
        Assert.Equal("Z_Construct_XEnum_M_T", SymbolNaming.SingletonGetter("M", "T", EngineRole.Enum));
        Assert.Equal("Z_Construct_XInterface_M_T", SymbolNaming.SingletonGetter("M", "T", EngineRole.Interface));
        Assert.Equal("Z_Construct_XDelegateFunction_M_T", SymbolNaming.SingletonGetter("M", "T", EngineRole.Delegate));
        Assert.Equal("Z_Construct_XFunction_M_T", SymbolNaming.SingletonGetter("M", "T", EngineRole.Function));
        Assert.Equal("Z_Construct_XProperty_M_T", SymbolNaming.SingletonGetter("M", "T", EngineRole.Property));
    }

    [Fact]
    public void ConstInitSymbol_LockedExample_MatchesContractRev71()
    {
        // From XHT.html Section 10.4: "Z_ConstInit_XClass_XGameFramework_XValve".
        string c = SymbolNaming.ConstInitSymbol("XGameFramework", "XValve", EngineRole.Class);
        Assert.Equal("Z_ConstInit_XClass_XGameFramework_XValve", c);
    }

    [Fact]
    public void SingletonGetter_RejectsEmptyArgs()
    {
        Assert.Throws<ArgumentException>(() => SymbolNaming.SingletonGetter("", "T", EngineRole.Class));
        Assert.Throws<ArgumentException>(() => SymbolNaming.SingletonGetter("M", "", EngineRole.Class));
    }

    [Fact]
    public void ConstInitSymbol_RejectsEmptyArgs()
    {
        Assert.Throws<ArgumentException>(() => SymbolNaming.ConstInitSymbol("", "T", EngineRole.Class));
        Assert.Throws<ArgumentException>(() => SymbolNaming.ConstInitSymbol("M", "", EngineRole.Class));
    }

    [Fact]
    public void RoleToken_AllRoles_DefinedAndStable()
    {
        Assert.Equal("XClass", SymbolNaming.RoleToken(EngineRole.Class));
        Assert.Equal("XStruct", SymbolNaming.RoleToken(EngineRole.Struct));
        Assert.Equal("XEnum", SymbolNaming.RoleToken(EngineRole.Enum));
        Assert.Equal("XInterface", SymbolNaming.RoleToken(EngineRole.Interface));
        Assert.Equal("XDelegateFunction", SymbolNaming.RoleToken(EngineRole.Delegate));
        Assert.Equal("XFunction", SymbolNaming.RoleToken(EngineRole.Function));
        Assert.Equal("XProperty", SymbolNaming.RoleToken(EngineRole.Property));
    }
}
