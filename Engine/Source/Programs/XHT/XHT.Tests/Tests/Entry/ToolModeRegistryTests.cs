// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XHT.Entry;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Entry;

/// <summary>
/// Tests for <see cref="ToolModeRegistry"/>. The registry discovers
/// <see cref="XhtModeAttribute"/>-marked mode classes via reflection per
/// <c>/Documents/XHT.html</c> Rev 8 Section 1 + Section 2.
/// </summary>
public class ToolModeRegistryTests
{
    [Fact]
    public void Build_DiscoversAllPhase1Modes()
    {
        IReadOnlyDictionary<string, IToolMode> modes = ToolModeRegistry.Build();

        // Per /Documents/XHT.html Rev 8 Section 1.1: 6 Phase 1 modes +
        // 1 Phase 2 stub = 7 modes total in Phase 1b.
        string[] expected = new[]
        {
            "help",
            "version",
            "parse-module",
            "emit-module",
            "validate-only",
            "dump-ast",
            "query-symbols",
        };

        foreach (string name in expected)
        {
            Assert.True(
                modes.ContainsKey(name),
                $"Expected XHT mode '{name}' to be registered.");
        }
    }

    [Fact]
    public void Build_HasSevenModes()
    {
        IReadOnlyDictionary<string, IToolMode> modes = ToolModeRegistry.Build();
        // We may include additional modes in future; the minimum is 7.
        Assert.True(modes.Count >= 7);
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
        Assert.NotNull(ToolModeRegistry.Resolve("parse-module"));
        Assert.NotNull(ToolModeRegistry.Resolve("PARSE-MODULE"));
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
