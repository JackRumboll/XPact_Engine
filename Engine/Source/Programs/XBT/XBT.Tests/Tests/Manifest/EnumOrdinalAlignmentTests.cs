// Copyright Simgenics. All Rights Reserved.

using System;
using System.Linq;
using PocoEnums   = Simgenics.XPact.XBT.Manifest;
using FbsEnums    = global::XPact.Build.Manifest;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Manifest;

/// <summary>
/// Canary against the Rev 13 ordinal-mismatch class of bug. Each named value
/// in each C# POCO enum (declared in <c>XBT.Manifest/Enums.cs</c>) MUST share
/// its <c>(int)Value</c> with the FlatSharp-generated FBS enum value of the
/// same name. If this test fails, the FBS schema and the JSON DTOs have
/// drifted out of alignment and the binary sidecar will mis-decode every
/// manifest XBT writes.
/// </summary>
/// <remarks>
/// <para>
/// The C# POCO enums and the FlatSharp-generated FBS enums are deliberately
/// declared in distinct namespaces -- <c>Simgenics.XPact.XBT.Manifest</c>
/// for the POCOs, <c>XPact.Build.Manifest</c> for the FBS generated types.
/// They are different CLR types with identical names and identical (we hope)
/// member ordinals; the test reflects on both to compare.
/// </para>
/// <para>
/// Two enums use renamed POCOs to avoid colliding with .NET's
/// <c>System.Configuration</c> and <c>System.Buffers</c> types:
/// </para>
/// <list type="bullet">
///   <item><c>PocoEnums.BuildConfiguration</c> &lt;-&gt; <c>FbsEnums.Configuration</c></item>
///   <item><c>PocoEnums.BuildTargetType</c>   &lt;-&gt; <c>FbsEnums.TargetType</c></item>
/// </list>
/// <para>
/// The <c>Languages</c> bit_flags enum's POCO declares the convenience value
/// <c>Both = Cpp | CSharp = 3</c> that is not in the FBS schema (FBS bit_flags
/// enums only list the bit positions). The test allows POCO-only superset
/// members but fails on any FBS member missing from the POCO and any
/// shared-name ordinal mismatch.
/// </para>
/// </remarks>
public sealed class EnumOrdinalAlignmentTests
{
    /// <summary>
    /// <c>(int)ModuleType.Programs == 4</c> -- the Rev 13 append. Hard-coded
    /// because this is the most footgun-y append: any reorder breaks every
    /// previously-written manifest.
    /// </summary>
    [Fact]
    public void ModuleType_Programs_Ordinal_Is_Four()
    {
        Assert.Equal(4, (int)PocoEnums.ModuleType.Programs);
        Assert.Equal(4, (int)FbsEnums.ModuleType.Programs);
    }

    /// <summary>
    /// <c>(int)ModuleType.ThirdParty == 3</c> -- the immediate predecessor.
    /// If anyone slides this above 3 to insert a value between Developer and
    /// ThirdParty, this is what catches it.
    /// </summary>
    [Fact]
    public void ModuleType_ThirdParty_Ordinal_Is_Three()
    {
        Assert.Equal(3, (int)PocoEnums.ModuleType.ThirdParty);
        Assert.Equal(3, (int)FbsEnums.ModuleType.ThirdParty);
    }

    [Fact]
    public void ModuleTier_OrdinalsAligned()
    {
        AssertEnumOrdinalsAligned<PocoEnums.ModuleTier, FbsEnums.ModuleTier>();
    }

    [Fact]
    public void ModuleType_OrdinalsAligned()
    {
        AssertEnumOrdinalsAligned<PocoEnums.ModuleType, FbsEnums.ModuleType>();
    }

    [Fact]
    public void Languages_BitPositions_Aligned()
    {
        // bit_flags enums: FBS-generated values are flag bits (1, 2). The POCO
        // declares Cpp = 1, CSharp = 2 plus a Both = 3 convenience that the
        // FBS schema has no analogue for. We compare only the shared names.
        AssertEnumOrdinalsAlignedAllowingPocoSuperset<PocoEnums.Languages, FbsEnums.Languages>();
    }

    [Fact]
    public void SimdLevel_OrdinalsAligned()
    {
        AssertEnumOrdinalsAligned<PocoEnums.SimdLevel, FbsEnums.SimdLevel>();
    }

    [Fact]
    public void Configuration_OrdinalsAligned()
    {
        // POCO renames "Configuration" -> "BuildConfiguration" to avoid the
        // System.Configuration collision. The named values must still align.
        AssertEnumOrdinalsAligned<PocoEnums.BuildConfiguration, FbsEnums.Configuration>();
    }

    [Fact]
    public void TargetType_OrdinalsAligned()
    {
        // POCO renames "TargetType" -> "BuildTargetType" so it does not collide
        // with the action-graph-side TargetType (Phase 1.1 future addition).
        AssertEnumOrdinalsAligned<PocoEnums.BuildTargetType, FbsEnums.TargetType>();
    }

    [Fact]
    public void Platform_OrdinalsAligned()
    {
        AssertEnumOrdinalsAligned<PocoEnums.Platform, FbsEnums.Platform>();
    }

    [Fact]
    public void StationRole_OrdinalsAligned()
    {
        AssertEnumOrdinalsAligned<PocoEnums.StationRole, FbsEnums.StationRole>();
    }

    [Fact]
    public void PCHUsageMode_OrdinalsAligned()
    {
        AssertEnumOrdinalsAligned<PocoEnums.PCHUsageMode, FbsEnums.PCHUsageMode>();
    }

    /// <summary>
    /// Generic: assert every named value in <typeparamref name="TPoco"/> has
    /// the same integer value as the value of the same name in
    /// <typeparamref name="TFbs"/>, and vice versa. Both enums must have the
    /// same member set.
    /// </summary>
    private static void AssertEnumOrdinalsAligned<TPoco, TFbs>()
        where TPoco : struct, Enum
        where TFbs  : struct, Enum
    {
        // Ordinal sort: the default culture-sensitive Compare can rank
        // characters differently on Turkish-locale runners (the famous
        // i/I dotting case) and on .NET version transitions.
        // StringComparer.Ordinal is locked across runtimes.
        string[] pocoNames = Enum.GetNames<TPoco>().OrderBy(n => n, StringComparer.Ordinal).ToArray();
        string[] fbsNames  = Enum.GetNames<TFbs>().OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(pocoNames, fbsNames);

        foreach (string name in pocoNames)
        {
            int pocoOrdinal = Convert.ToInt32(Enum.Parse<TPoco>(name));
            int fbsOrdinal  = Convert.ToInt32(Enum.Parse<TFbs>(name));
            Assert.True(
                pocoOrdinal == fbsOrdinal,
                $"Enum value drift: POCO {typeof(TPoco).FullName}.{name}={pocoOrdinal} but FBS {typeof(TFbs).FullName}.{name}={fbsOrdinal}.");
        }
    }

    /// <summary>
    /// Variant of <see cref="AssertEnumOrdinalsAligned"/> that allows the
    /// POCO to declare more members than the FBS enum (e.g. the
    /// <c>Languages.Both</c> convenience flag). Every member present in
    /// FBS must be present in POCO with matching value; POCO-only members
    /// are allowed.
    /// </summary>
    private static void AssertEnumOrdinalsAlignedAllowingPocoSuperset<TPoco, TFbs>()
        where TPoco : struct, Enum
        where TFbs  : struct, Enum
    {
        foreach (string fbsName in Enum.GetNames<TFbs>())
        {
            Assert.True(
                Enum.IsDefined(typeof(TPoco), fbsName),
                $"FBS enum {typeof(TFbs).FullName}.{fbsName} has no corresponding POCO member.");

            int pocoOrdinal = Convert.ToInt32(Enum.Parse<TPoco>(fbsName));
            int fbsOrdinal  = Convert.ToInt32(Enum.Parse<TFbs>(fbsName));
            Assert.True(
                pocoOrdinal == fbsOrdinal,
                $"Enum value drift: POCO {typeof(TPoco).FullName}.{fbsName}={pocoOrdinal} but FBS {typeof(TFbs).FullName}.{fbsName}={fbsOrdinal}.");
        }
    }
}
