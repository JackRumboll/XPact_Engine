// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Manifest;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Configuration;

/// <summary>
/// Exercises <see cref="TierValidator.ValidateModuleDeps"/> against
/// constructed <see cref="ModuleRules"/> instances. The validator
/// walks the three dep lists and returns one
/// <see cref="TierViolation"/> per disallowed edge.
/// </summary>
public sealed class TierValidatorTests
{
    /// <summary>
    /// Build a resolver function from a flat dictionary of name -> tier.
    /// </summary>
    private static System.Func<string, ModuleTier?> Resolver(params (string Name, ModuleTier Tier)[] entries)
    {
        Dictionary<string, ModuleTier> dict = entries.ToDictionary(e => e.Name, e => e.Tier);
        return name => dict.TryGetValue(name, out ModuleTier t) ? t : null;
    }

    [Fact]
    public void Valid_Downward_Link_Returns_NoViolations()
    {
        ModuleRules studio = new()
        {
            Name = "SomeStudioModule",
            Tier = ModuleTier.Studio,
            ModuleType = ModuleType.Runtime,
            PublicDependencyModuleNames = { "XCore" },
        };

        var violations = TierValidator.ValidateModuleDeps(
            studio,
            ModuleTier.Studio,
            Resolver(("XCore", ModuleTier.Engine)));

        Assert.Empty(violations);
    }

    [Fact]
    public void Engine_Linking_To_Studio_Reports_Violation()
    {
        // The classic "engine depends on a studio plugin at link time"
        // mistake. Banned per Section 9.3.
        ModuleRules engine = new()
        {
            Name = "XCore",
            Tier = ModuleTier.Engine,
            ModuleType = ModuleType.Runtime,
            PublicDependencyModuleNames = { "IndustrialEquipment" },
        };

        var violations = TierValidator.ValidateModuleDeps(
            engine,
            ModuleTier.Engine,
            Resolver(("IndustrialEquipment", ModuleTier.Studio)));

        Assert.Single(violations);
        TierViolation v = violations[0];
        Assert.Equal("XCore", v.ConsumerName);
        Assert.Equal(ModuleTier.Engine, v.ConsumerTier);
        Assert.Equal("IndustrialEquipment", v.ProducerName);
        Assert.Equal(ModuleTier.Studio, v.ProducerTier);
        Assert.Equal(DependencyKind.Link, v.Kind);
        Assert.Contains("link", v.FormatMessage());
    }

    [Fact]
    public void Engine_Dynamic_Load_Of_Studio_Module_NoViolation()
    {
        // The audit-finding fix: Engine -> Studio dynamic is the
        // hot-reload pattern. No violation.
        ModuleRules engine = new()
        {
            Name = "XCore",
            Tier = ModuleTier.Engine,
            ModuleType = ModuleType.Runtime,
            DynamicallyLoadedModuleNames = { "IndustrialEquipment" },
        };

        var violations = TierValidator.ValidateModuleDeps(
            engine,
            ModuleTier.Engine,
            Resolver(("IndustrialEquipment", ModuleTier.Studio)));

        Assert.Empty(violations);
    }

    [Fact]
    public void Studio_Private_Linking_Up_To_Project_Reports_Violation()
    {
        // Private dep lists are validated the same way as Public.
        ModuleRules studio = new()
        {
            Name = "Studio.Helpers",
            Tier = ModuleTier.Studio,
            ModuleType = ModuleType.Runtime,
            PrivateDependencyModuleNames = { "MyProductFeature" },
        };

        var violations = TierValidator.ValidateModuleDeps(
            studio,
            ModuleTier.Studio,
            Resolver(("MyProductFeature", ModuleTier.Project)));

        Assert.Single(violations);
        Assert.Equal(DependencyKind.Link, violations[0].Kind);
    }

    [Fact]
    public void Multiple_Violations_All_Returned()
    {
        ModuleRules engine = new()
        {
            Name = "XCore",
            Tier = ModuleTier.Engine,
            ModuleType = ModuleType.Runtime,
            PublicDependencyModuleNames = { "BadStudio", "BadProject" },
        };

        var violations = TierValidator.ValidateModuleDeps(
            engine,
            ModuleTier.Engine,
            Resolver(("BadStudio", ModuleTier.Studio), ("BadProject", ModuleTier.Project)));

        Assert.Equal(2, violations.Count);
    }

    [Fact]
    public void Unknown_Dependency_Name_Skipped()
    {
        // When a dependency name does not resolve, the validator
        // skips that edge: the missing-plugin diagnostic comes from a
        // separate code path. Per Contract Section 9.3.
        ModuleRules engine = new()
        {
            Name = "XCore",
            Tier = ModuleTier.Engine,
            ModuleType = ModuleType.Runtime,
            PublicDependencyModuleNames = { "DoesNotExist" },
        };

        var violations = TierValidator.ValidateModuleDeps(
            engine,
            ModuleTier.Engine,
            Resolver());

        Assert.Empty(violations);
    }
}
