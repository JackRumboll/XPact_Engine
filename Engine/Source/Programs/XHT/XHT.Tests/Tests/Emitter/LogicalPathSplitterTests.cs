// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XHT.Emitter;
using Simgenics.XPact.XHT.Manifest;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Emitter;

/// <summary>
/// Tests for <see cref="LogicalPathSplitter"/>. Verifies that a source-
/// relative path splits into the right segments after the module's
/// BaseDirectory is stripped per Contract Section 1.4 logical-path rule.
/// </summary>
public class LogicalPathSplitterTests
{
    [Fact]
    public void Split_EngineModule_StripsBaseDirAndSplitsRemaining()
    {
        XbtModule m = EmitterTestHarness.MakeModule(
            name: "XGameFramework",
            baseDir: "Engine/Source/Runtime/XGameFramework");

        IReadOnlyList<string> segments = LogicalPathSplitter.Split(
            "Engine/Source/Runtime/XGameFramework/Public/Valves/XValve.h", m);

        Assert.Equal(new[] { "Public", "Valves", "XValve" }, segments);
    }

    [Fact]
    public void Split_ModuleRelativePath_SplitsWithoutStripping()
    {
        XbtModule m = EmitterTestHarness.MakeModule(
            name: "XGameFramework",
            baseDir: "Engine/Source/Runtime/XGameFramework");

        IReadOnlyList<string> segments = LogicalPathSplitter.Split(
            "Public/Valves/XValve.h", m);

        Assert.Equal(new[] { "Public", "Valves", "XValve" }, segments);
    }

    [Fact]
    public void Split_StudioModule_StripsCorrectPrefix()
    {
        XbtModule m = EmitterTestHarness.MakeModule(
            name: "IndustrialEquipment",
            baseDir: "Studio/Plugins/IndustrialEquipment/Source/IndustrialEquipment",
            tier: ModuleTier.Studio);

        IReadOnlyList<string> segments = LogicalPathSplitter.Split(
            "Studio/Plugins/IndustrialEquipment/Source/IndustrialEquipment/Private/Pump.cs", m);

        Assert.Equal(new[] { "Private", "Pump" }, segments);
    }

    [Fact]
    public void Split_ProjectModule_StripsCorrectPrefix()
    {
        XbtModule m = EmitterTestHarness.MakeModule(
            name: "MiningTraining",
            baseDir: "Project/Source/MiningTraining",
            tier: ModuleTier.Project);

        IReadOnlyList<string> segments = LogicalPathSplitter.Split(
            "Project/Source/MiningTraining/Public/Mine.h", m);

        Assert.Equal(new[] { "Public", "Mine" }, segments);
    }

    [Fact]
    public void Split_HeaderAtModuleRoot_ProducesOneSegment()
    {
        XbtModule m = EmitterTestHarness.MakeModule(
            name: "XValve",
            baseDir: "Source/XValve");

        IReadOnlyList<string> segments = LogicalPathSplitter.Split("XValve.h", m);
        Assert.Single(segments);
        Assert.Equal("XValve", segments[0]);
    }

    [Fact]
    public void Split_BackslashesNormalised_ToForwardSlash()
    {
        XbtModule m = EmitterTestHarness.MakeModule(
            name: "XGameFramework",
            baseDir: "Engine/Source/Runtime/XGameFramework");

        // Mixed slashes: emitter must produce identical output regardless
        // of host OS.
        IReadOnlyList<string> segments = LogicalPathSplitter.Split(
            @"Engine\Source\Runtime\XGameFramework\Public\Valves\XValve.h", m);

        Assert.Equal(new[] { "Public", "Valves", "XValve" }, segments);
    }

    [Fact]
    public void Split_HppAndHhAndInlExtensions_StrippedFromStem()
    {
        XbtModule m = EmitterTestHarness.MakeModule("M", "");
        Assert.Equal(new[] { "Foo" }, LogicalPathSplitter.Split("Foo.hpp", m));
        Assert.Equal(new[] { "Foo" }, LogicalPathSplitter.Split("Foo.hh", m));
        Assert.Equal(new[] { "Foo" }, LogicalPathSplitter.Split("Foo.inl", m));
    }

    [Fact]
    public void Split_RejectsEmptyOrNullPath()
    {
        XbtModule m = EmitterTestHarness.MakeModule();
        Assert.Throws<ArgumentException>(() => LogicalPathSplitter.Split("", m));
        Assert.Throws<ArgumentException>(() => LogicalPathSplitter.Split("   ", m));
        Assert.Throws<ArgumentNullException>(() => LogicalPathSplitter.Split(null!, m));
    }

    [Fact]
    public void Split_RejectsNullModule()
    {
        Assert.Throws<ArgumentNullException>(() => LogicalPathSplitter.Split("X.h", null!));
    }

    [Fact]
    public void Split_RejectsGenH_InputAsXhtOutput()
    {
        // Per M10 audit: .gen.h / .gen.cpp / .gen.hpp / .gen.inl are
        // XHT OUTPUTS; they must never appear as XHT INPUTS. Reject
        // them with a clear diagnostic at the LogicalPathSplitter
        // level so the caller surfaces a cycle.
        XbtModule m = EmitterTestHarness.MakeModule();
        Assert.Throws<ArgumentException>(() => LogicalPathSplitter.Split("Foo.gen.h", m));
        Assert.Throws<ArgumentException>(() => LogicalPathSplitter.Split("Foo.gen.cpp", m));
        Assert.Throws<ArgumentException>(() => LogicalPathSplitter.Split("Foo.GEN.H", m));
        Assert.Throws<ArgumentException>(() => LogicalPathSplitter.Split("Public/Foo.gen.h", m));
    }
}
