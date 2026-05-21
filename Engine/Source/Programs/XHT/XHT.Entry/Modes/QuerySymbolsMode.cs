// Copyright Simgenics. All Rights Reserved.

using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XHT.Core;

namespace Simgenics.XPact.XHT.Entry.Modes;

/// <summary>
/// Phase 2 stub of the <c>query-symbols</c> mode per
/// <c>/Documents/XHT.html</c> Rev 5 Section 1.1.
/// </summary>
/// <remarks>
/// <para>
/// Phase 2 implements an in-process AST + line-oriented JSON
/// request/response surface for IDE plugins; Phase 1 emits a "not-yet-
/// implemented" diagnostic and exits non-zero. The exit code is
/// hard-coded to <c>24</c> matching XBT's <c>RunXHTMode</c> Phase 2 stub
/// pattern: 24 is the <c>PluginNotFound</c> mnemonic per
/// <c>ContractSurface.ExitCodes</c>, the closest-fit mnemonic until a
/// dedicated <c>ToolNotFound</c> code ships (XBT.html Round-7 framing).
/// 24 is intentionally <em>not</em> in <see cref="ExitCodes"/> because
/// XHT does not own that band (it is XBT's discovery / plugin band);
/// this stub is the one site that returns it.
/// </para>
/// </remarks>
[XhtMode("query-symbols")]
public sealed class QuerySymbolsMode : IToolMode
{
    /// <summary>
    /// Hard-coded Phase 2 stub exit code: 24 mirrors XBT's <c>RunXHTMode</c>
    /// Phase 2 stub pattern. PluginNotFound mnemonic per
    /// <c>ContractSurface.ExitCodes</c>.
    /// </summary>
    public const int Phase2StubExitCode = 24;

    /// <inheritdoc />
    public string Name => "query-symbols";

    /// <inheritdoc />
    public string Description =>
        "Open an in-process AST and answer JSON symbol-lookup queries on stdin. Phase 2; not yet implemented.";

    /// <inheritdoc />
    public Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        _ = args;
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(ExitCodes.Cancelled);
        }

        Logger.Error("XHT query-symbols: Phase 2 - not yet implemented; see /Documents/XHT.html §1.1 + §26.");
        return Task.FromResult(Phase2StubExitCode);
    }
}
