// Copyright Simgenics. All Rights Reserved.

using System.IO;
using Simgenics.XPact.XBT.ActionGraph;
using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.Toolchain;

/// <summary>
/// Per-module precompiled-header binding produced by
/// <see cref="XToolChain.GeneratePCH"/>. Threads the generated PCH
/// action and the PCH header through to the consumer's
/// <see cref="XToolChain.CompileSource"/> calls so every TU in the
/// same module depends on the PCH action and consumes the resulting
/// <c>.pch</c>/<c>.pchi</c> via the platform's PCH-include mechanism.
/// </summary>
/// <param name="Action">
/// The <see cref="XActionType.PCHGenerationAction"/> instance. Threaded
/// into the action graph as a prerequisite of every consumer compile.
/// </param>
/// <param name="PchHeaderFile">
/// The source PCH header file. Surfaced through
/// <see cref="IExternalAction.PrerequisiteItems"/> on every consumer
/// compile so a header content change invalidates the consumer's
/// cached output too.
/// </param>
/// <param name="PchHeaderName">
/// The PCH header's filename component (e.g. <c>"XScoringPCH.h"</c>)
/// the platform's <c>/FI</c> (MSVC) or <c>-include</c> (Clang) flag
/// references. Matches what the module declared as
/// <c>PrivatePCHHeaderFile</c>.
/// </param>
/// <param name="PchOutputFile">
/// The generated PCH artefact: <c>.pch</c> on MSVC, <c>.pchi</c>
/// on Clang. Surfaced through the consumer compile's
/// <see cref="IExternalAction.PrerequisiteItems"/> so the
/// dependency edge is explicit in the graph.
/// </param>
public sealed record PCHBinding(
    IExternalAction Action,
    FileItem PchHeaderFile,
    string PchHeaderName,
    FileItem PchOutputFile)
{
    /// <summary>
    /// Absolute path to the directory containing the PCH header file.
    /// Threaded into the consumer's <c>/I</c> (MSVC) or <c>-I</c>
    /// (Clang) list so the platform's <c>/FI</c>/<c>-include</c>
    /// resolves regardless of the consumer module's own include-path
    /// declarations.
    /// </summary>
    /// <remarks>
    /// Audit fix C4: in the shared-PCH case the header may live under
    /// a participant's tree that other participants do not include.
    /// Storing the directory on the binding (rather than re-deriving it
    /// from <see cref="PchHeaderFile"/> at every consume site) keeps
    /// the consumer-side flag emission cheap and unambiguous.
    /// </remarks>
    public string PchHeaderDirectory { get; } =
        Path.GetDirectoryName(PchHeaderFile.FullPath) ?? string.Empty;
}
