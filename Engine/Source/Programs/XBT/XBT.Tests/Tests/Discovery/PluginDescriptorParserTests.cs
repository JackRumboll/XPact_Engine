// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Simgenics.XPact.XBT.Configuration;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Discovery;

/// <summary>
/// Verifies <see cref="PluginDescriptorParser"/> against the canonical
/// <c>.xplugin</c> JSON shape per Toolchain Contract Rev 13 Section
/// 9.4 and <c>/Documents/XBT.html</c> Rev 4 Section 17.
/// </summary>
public sealed class PluginDescriptorParserTests
{
    [Fact]
    public void Minimal_ValidJson_Parses()
    {
        const string json = """
            {
              "Name": "HMIPanels",
              "Version": "1.0.0"
            }
            """;

        PluginDescriptor descriptor = PluginDescriptorParser.Parse(json);

        Assert.Equal("HMIPanels", descriptor.Name);
        Assert.Equal("1.0.0", descriptor.Version);
        Assert.Null(descriptor.MinEngineVersion);
        Assert.Null(descriptor.MaxEngineVersion);
        Assert.True(descriptor.EnabledByDefault);
        Assert.Empty(descriptor.Modules);
        Assert.Empty(descriptor.Dependencies);
    }

    [Fact]
    public void FullyPopulated_Json_Parses()
    {
        const string json = """
            {
              "Name": "HMIPanels",
              "Version": "2.4.0",
              "MinEngineVersion": "1.0.0",
              "MaxEngineVersion": "1.99.99",
              "Description": "Industrial HMI panel widgets.",
              "Author": "Simgenics",
              "Modules": ["HMIPanels", "HMIPanelsRuntime"],
              "Dependencies": ["XCore", "XSlateAlt"],
              "EnabledByDefault": false,
              "Platforms": ["Win64", "Linux"],
              "Targets": ["Editor", "Game"]
            }
            """;

        PluginDescriptor descriptor = PluginDescriptorParser.Parse(json);

        Assert.Equal("HMIPanels", descriptor.Name);
        Assert.Equal("2.4.0", descriptor.Version);
        Assert.Equal("1.0.0", descriptor.MinEngineVersion);
        Assert.Equal("1.99.99", descriptor.MaxEngineVersion);
        Assert.Equal("Industrial HMI panel widgets.", descriptor.Description);
        Assert.Equal("Simgenics", descriptor.Author);
        Assert.Equal(2, descriptor.Modules.Count);
        Assert.Contains("HMIPanelsRuntime", descriptor.Modules);
        Assert.Equal(new[] { "XCore", "XSlateAlt" }, descriptor.Dependencies.ToArray());
        Assert.False(descriptor.EnabledByDefault);
        Assert.Equal(new[] { "Win64", "Linux" }, descriptor.Platforms.ToArray());
        Assert.Equal(new[] { "Editor", "Game" }, descriptor.Targets.ToArray());
    }

    [Fact]
    public void Malformed_Json_Rejects_With_Exit50()
    {
        const string json = """
            {
              "Name": "HMI"
              "Version": "1.0.0"
            }
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => PluginDescriptorParser.Parse(json));
        Assert.Equal(50, ex.ExitCode);
    }

    [Fact]
    public void Missing_Required_Name_Rejects()
    {
        const string json = """
            {
              "Version": "1.0.0"
            }
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => PluginDescriptorParser.Parse(json));
        Assert.Equal(50, ex.ExitCode);
        Assert.Contains("Name", ex.Message);
    }

    [Fact]
    public void Missing_Required_Version_Rejects()
    {
        const string json = """
            {
              "Name": "Foo"
            }
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => PluginDescriptorParser.Parse(json));
        Assert.Equal(50, ex.ExitCode);
        Assert.Contains("Version", ex.Message);
    }

    [Fact]
    public void Unknown_TopLevel_Key_Rejects()
    {
        const string json = """
            {
              "Name": "Foo",
              "Version": "1.0.0",
              "BlatantTypo": true
            }
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => PluginDescriptorParser.Parse(json));
        Assert.Equal(50, ex.ExitCode);
        Assert.Contains("BlatantTypo", ex.Message);
    }

    [Fact]
    public void Wrong_Type_For_EnabledByDefault_Rejects()
    {
        const string json = """
            {
              "Name": "Foo",
              "Version": "1.0.0",
              "EnabledByDefault": "yes"
            }
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => PluginDescriptorParser.Parse(json));
        Assert.Equal(50, ex.ExitCode);
    }

    [Fact]
    public void Wrong_Type_For_Modules_Rejects()
    {
        const string json = """
            {
              "Name": "Foo",
              "Version": "1.0.0",
              "Modules": "not-an-array"
            }
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => PluginDescriptorParser.Parse(json));
        Assert.Equal(50, ex.ExitCode);
    }
}
