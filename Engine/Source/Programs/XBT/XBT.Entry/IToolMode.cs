// Copyright Simgenics. All Rights Reserved.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Simgenics.XPact.XBT.Entry;

/// <summary>
/// Marks a class as an XBT command mode. The string is the case-insensitive
/// CLI name -- e.g., <c>[XBTMode("build")]</c> registers <c>BuildMode</c>
/// as the handler for <c>XBT.exe build ...</c>.
/// </summary>
/// <remarks>
/// XBT scans for this attribute in every loaded assembly at startup
/// (<see cref="ToolModeRegistry"/>). Duplicate names fail loudly so
/// out-of-tree mode registrations cannot silently shadow built-ins.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class XBTModeAttribute : Attribute
{
    public string Name { get; }

    public XBTModeAttribute(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Mode name cannot be empty.", nameof(name));
        }
        Name = name;
    }
}

/// <summary>
/// The XBT mode interface. Mirrors UBT's <c>IToolMode&lt;T&gt;</c>
/// pattern (see <c>UnrealBuildTool/Modes/IToolMode.cs</c> for the
/// reference shape) with C# 12 static-abstract members so the registry
/// can enumerate mode names and descriptions without instantiating
/// every mode at discovery time.
/// </summary>
/// <typeparam name="TMode">The implementing mode type itself.</typeparam>
public interface IToolMode<TMode>
    where TMode : IToolMode<TMode>
{
    /// <summary>Case-insensitive CLI name -- mirrors the <see cref="XBTModeAttribute"/>.</summary>
    static abstract string Name { get; }

    /// <summary>One-line human-readable description shown in <c>help</c>.</summary>
    static abstract string Description { get; }

    /// <summary>
    /// Execute the mode with the remaining CLI arguments (the mode name
    /// has been stripped). Honors <paramref name="cancellationToken"/>
    /// per XBT.html Section 20.3. Returns the process exit code per
    /// the surface in Toolchain Contract Section 13.
    /// </summary>
    Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken);
}
