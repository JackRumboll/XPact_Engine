// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XIL2CPP.Entry;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Entry;

/// <summary>
/// Tests for <see cref="ToolModeRegistry"/>. The registry discovers
/// <see cref="XIL2CPPModeAttribute"/>-marked mode classes via reflection
/// per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 15.
/// </summary>
/// <remarks>
/// Phase 6.a ships only the <c>version</c> and <c>help</c> modes; the
/// transpile / parse-module modes land in a later sub-phase once the
/// front-end exists, so this test pins the current minimal set rather
/// than the full XHT seven-mode roster.
/// </remarks>
public class ToolModeRegistryTests
{
    [Fact]
    public void Build_DiscoversPhase6aModes()
    {
        IReadOnlyDictionary<string, IToolMode> modes = ToolModeRegistry.Build();

        // Phase 6.a CLI scaffold registers exactly the version + help modes.
        string[] expected = new[]
        {
            "help",
            "version",
        };

        foreach (string name in expected)
        {
            Assert.True(
                modes.ContainsKey(name),
                $"Expected XIL2CPP mode '{name}' to be registered.");
        }
    }

    [Fact]
    public void Build_HasAtLeastTwoModes()
    {
        IReadOnlyDictionary<string, IToolMode> modes = ToolModeRegistry.Build();
        // We add additional modes in later sub-phases; the Phase 6.a
        // minimum is 2 (version + help).
        Assert.True(modes.Count >= 2);
    }

    [Fact]
    public void ModeNames_AreKebabCase()
    {
        IReadOnlyDictionary<string, IToolMode> modes = ToolModeRegistry.Build();
        foreach (string name in modes.Keys)
        {
            // Kebab-case: lowercase letters, digits, hyphens only.
            foreach (char c in name)
            {
                Assert.True(
                    char.IsLower(c) || char.IsDigit(c) || c == '-',
                    $"Mode name '{name}' is not kebab-case (offending char '{c}').");
            }
        }
    }

    [Fact]
    public void Resolve_CaseInsensitive()
    {
        Assert.NotNull(ToolModeRegistry.Resolve("help"));
        Assert.NotNull(ToolModeRegistry.Resolve("HELP"));
        Assert.NotNull(ToolModeRegistry.Resolve("Help"));
        Assert.NotNull(ToolModeRegistry.Resolve("version"));
        Assert.NotNull(ToolModeRegistry.Resolve("VERSION"));
    }

    [Fact]
    public void Resolve_UnknownName_ReturnsNull()
    {
        Assert.Null(ToolModeRegistry.Resolve("no-such-mode"));
    }

    [Fact]
    public void Resolve_EmptyOrNull_ReturnsNull()
    {
        Assert.Null(ToolModeRegistry.Resolve(string.Empty));
        Assert.Null(ToolModeRegistry.Resolve(null!));
    }

    [Fact]
    public void EachMode_HasNonEmptyDescription()
    {
        IReadOnlyDictionary<string, IToolMode> modes = ToolModeRegistry.Build();
        foreach (IToolMode m in modes.Values)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Description),
                $"Mode '{m.Name}' has an empty Description.");
        }
    }

    [Fact]
    public void NameProperty_MatchesAttributeName()
    {
        IReadOnlyDictionary<string, IToolMode> modes = ToolModeRegistry.Build();
        foreach (KeyValuePair<string, IToolMode> kv in modes)
        {
            // The IToolMode.Name property must agree with the dictionary
            // key (which is the attribute name).
            Assert.Equal(kv.Key, kv.Value.Name, StringComparer.OrdinalIgnoreCase);
        }
    }
}
