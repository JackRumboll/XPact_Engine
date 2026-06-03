// Copyright Simgenics. All Rights Reserved.

using System;

namespace Simgenics.XPact.XIL2CPP.Entry;

/// <summary>
/// Marks a class as an XIL2CPP command mode. The string is the
/// case-insensitive kebab-case CLI name -- e.g.,
/// <c>[XIL2CPPMode("transpile-module")]</c> registers the class as the
/// handler for <c>xil2cpp transpile-module ...</c>.
/// </summary>
/// <remarks>
/// <para>
/// XIL2CPP scans for this attribute in every loaded assembly at startup
/// (<see cref="ToolModeRegistry"/>). Duplicate names emit a warning and
/// the first-registered class wins (mirrors XHT.Entry's discipline).
/// Per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 15.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class XIL2CPPModeAttribute : Attribute
{
    /// <summary>Kebab-case CLI mode name.</summary>
    public string Name { get; }

    /// <summary>
    /// Construct the attribute.
    /// </summary>
    /// <param name="name">Kebab-case mode name. Must not be null / empty / whitespace.</param>
    /// <exception cref="ArgumentException">If <paramref name="name"/> is null / empty / whitespace.</exception>
    public XIL2CPPModeAttribute(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Mode name cannot be empty.", nameof(name));
        }
        Name = name;
    }
}
