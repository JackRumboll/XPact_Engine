// Copyright Simgenics. All Rights Reserved.

using System;

namespace Simgenics.XPact.XHT.Entry;

/// <summary>
/// Marks a class as an XHT command mode. The string is the case-insensitive
/// kebab-case CLI name -- e.g., <c>[XhtMode("parse-module")]</c> registers
/// the class as the handler for <c>xht parse-module ...</c>.
/// </summary>
/// <remarks>
/// <para>
/// XHT scans for this attribute in every loaded assembly at startup
/// (<see cref="ToolModeRegistry"/>). Duplicate names emit a warning and
/// the first-registered class wins (mirrors XBT.Entry's discipline).
/// Per <c>/Documents/XHT.html</c> Rev 7 Section 1 + Section 2.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class XhtModeAttribute : Attribute
{
    /// <summary>Kebab-case CLI mode name.</summary>
    public string Name { get; }

    /// <summary>
    /// Construct the attribute.
    /// </summary>
    /// <param name="name">Kebab-case mode name. Must not be null / empty / whitespace.</param>
    /// <exception cref="ArgumentException">If <paramref name="name"/> is null / empty / whitespace.</exception>
    public XhtModeAttribute(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Mode name cannot be empty.", nameof(name));
        }
        Name = name;
    }
}
