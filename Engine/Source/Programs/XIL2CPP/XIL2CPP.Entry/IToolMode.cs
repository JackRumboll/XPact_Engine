// Copyright Simgenics. All Rights Reserved.

using System.Threading;
using System.Threading.Tasks;

namespace Simgenics.XPact.XIL2CPP.Entry;

/// <summary>
/// One XIL2CPP command mode. Mirrors XHT's <c>IToolMode</c> shape
/// (XHT.html Section 1) -- XIL2CPP modes use an instance-based
/// <c>Name</c> / <c>Description</c> surface so
/// <see cref="ToolModeRegistry"/> can list modes without resolving any
/// static-abstract members on each implementation type. Per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 15.
/// </summary>
/// <remarks>
/// <para>
/// Implementations carry the <see cref="XIL2CPPModeAttribute"/> so reflection
/// discovery in <see cref="ToolModeRegistry"/> can enumerate them at
/// process startup without an explicit registration list.
/// </para>
/// </remarks>
public interface IToolMode
{
    /// <summary>Kebab-case CLI mode name (mirrors the <see cref="XIL2CPPModeAttribute"/>).</summary>
    string Name { get; }

    /// <summary>One-line human-readable description shown in <c>help</c>.</summary>
    string Description { get; }

    /// <summary>
    /// Execute the mode with the remaining CLI arguments (the mode name
    /// has been stripped). Honors <paramref name="ct"/> per the
    /// cancellation discipline in <c>/Documents/XIL2CPP.html</c> Rev 4
    /// Section 15. Returns the process exit code per the exit-code surface
    /// (Toolchain Contract Section 13.1).
    /// </summary>
    /// <param name="args">CLI arguments after the mode name (positional + flag args).</param>
    /// <param name="ct">Cancellation token wired to SIGINT / Ctrl-C at the process level.</param>
    /// <returns>Process exit code per the Toolchain Contract exit-code surface.</returns>
    Task<int> ExecuteAsync(string[] args, CancellationToken ct);
}
