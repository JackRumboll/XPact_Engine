// Copyright Simgenics. All Rights Reserved.

using System.Threading;
using System.Threading.Tasks;

namespace Simgenics.XPact.XHT.Entry;

/// <summary>
/// One XHT command mode. Mirrors XBT's <c>IToolMode&lt;T&gt;</c> shape
/// (XBT.html Section 1.1) but flattened -- XHT modes use an instance-
/// based <c>Name</c> / <c>Description</c> surface so
/// <see cref="ToolModeRegistry"/> can list modes without resolving the
/// static-abstract members on each implementation type. Per
/// <c>/Documents/XHT.html</c> Rev 5 Section 1 + Section 2 (module layout).
/// </summary>
/// <remarks>
/// <para>
/// Implementations carry the <see cref="XhtModeAttribute"/> so reflection
/// discovery in <see cref="ToolModeRegistry"/> can enumerate them at
/// process startup without an explicit registration list.
/// </para>
/// </remarks>
public interface IToolMode
{
    /// <summary>Kebab-case CLI mode name (mirrors the <see cref="XhtModeAttribute"/>).</summary>
    string Name { get; }

    /// <summary>One-line human-readable description shown in <c>help</c>.</summary>
    string Description { get; }

    /// <summary>
    /// Execute the mode with the remaining CLI arguments (the mode name
    /// has been stripped). Honors <paramref name="ct"/> per XHT.html
    /// Section 11.5 + Section 1.5 cancellation discipline. Returns the
    /// process exit code per the surface in XHT.html Section 1.3.
    /// </summary>
    /// <param name="args">CLI arguments after the mode name (positional + flag args).</param>
    /// <param name="ct">Cancellation token wired to SIGINT / Ctrl-C at the process level.</param>
    /// <returns>Process exit code per XHT.html Section 1.3.</returns>
    Task<int> ExecuteAsync(string[] args, CancellationToken ct);
}
