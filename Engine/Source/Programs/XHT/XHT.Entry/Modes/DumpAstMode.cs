// Copyright Simgenics. All Rights Reserved.

using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XHT.Core;

namespace Simgenics.XPact.XHT.Entry.Modes;

/// <summary>
/// Phase 1b stub of the <c>dump-ast</c> mode per
/// <c>/Documents/XHT.html</c> Rev 8 Section 1.1.
/// </summary>
/// <remarks>
/// <para>
/// Phase 1d+ implements full AST JSON dump to stdout or to the file
/// named by <c>-Out=</c>. Phase 1b is a smoke stub that returns 0 after
/// emitting an informational log line.
/// </para>
/// </remarks>
[XhtMode("dump-ast")]
public sealed class DumpAstMode : IToolMode
{
    /// <inheritdoc />
    public string Name => "dump-ast";

    /// <inheritdoc />
    public string Description =>
        "Parse + resolve, dump the AST as JSON to stdout / -Out=<file>. Phase 1d+; Phase 1b stub.";

    /// <inheritdoc />
    public Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        _ = args;
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(ExitCodes.Cancelled);
        }

        Logger.Info("Phase 1b stub: AST dump not yet implemented");
        return Task.FromResult(ExitCodes.Success);
    }
}
