// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Discovery;
using Simgenics.XPact.XBT.Toolchain;

namespace Simgenics.XPact.XBT.ProjectFiles;

/// <summary>
/// One generator emits project files for one IDE per
/// <c>/Documents/XBT.html</c> Rev 4 Section 14. MVP implementations:
/// <see cref="ClangdCompileCommandsGenerator"/>, <see cref="RiderProjectGenerator"/>.
/// Visual Studio and Xcode are Phase 2.
/// </summary>
public interface IProjectFileGenerator
{
    /// <summary>
    /// Generator name for CLI <c>-Generator=</c> matching and for
    /// <see cref="GenerationResult"/> reporting. Lower-case canonical
    /// form: <c>"clangd"</c>, <c>"Rider"</c>, future
    /// <c>"VisualStudio"</c>, etc.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Generate the project files into the engine root (or wherever
    /// appropriate for the IDE).
    /// </summary>
    /// <param name="context">Per-call context: target, module + plugin
    /// catalogs, toolchain.</param>
    /// <param name="token">Cancellation token honored per
    /// <c>/Documents/XBT.html</c> Section 20.3.</param>
    Task<GenerationResult> GenerateAsync(GenerationContext context, CancellationToken token);
}

/// <summary>
/// Per-call context passed to every <see cref="IProjectFileGenerator"/>.
/// Carries every input the generator needs to compose its output --
/// engine + studio + project roots, the active target, the discovered
/// catalogs, and the toolchain (to obtain the compile command line for
/// clangd's <c>compile_commands.json</c>).
/// </summary>
/// <param name="EngineRoot">Absolute path to <c>/Engine/</c>. Always set.</param>
/// <param name="StudioRoot">Optional absolute path to <c>/Studio/</c>;
/// null when the repo has no Studio tier.</param>
/// <param name="ProjectRoot">Optional absolute path to the project
/// directory the user invoked the build from; null for engine-only builds.</param>
/// <param name="Target">The active target driving the build.</param>
/// <param name="Modules">Every module enabled by the target, in the
/// discovery-time order.</param>
/// <param name="Plugins">Every plugin entry in the resolved catalog.
/// Some entries may have no enabled modules; the generator filters as
/// appropriate.</param>
/// <param name="ToolChain">The toolchain providing
/// <c>CompileSource</c> for clangd flag emission.</param>
/// <param name="LogChannel">Free-form channel tag the generator uses
/// when emitting diagnostics (e.g. <c>"ProjectFiles"</c>).</param>
public sealed record GenerationContext(
    string EngineRoot,
    string? StudioRoot,
    string? ProjectRoot,
    TargetRules Target,
    IReadOnlyList<ModuleRecord> Modules,
    IReadOnlyList<PluginEntry> Plugins,
    XToolChain ToolChain,
    string LogChannel);

/// <summary>
/// Result of an <see cref="IProjectFileGenerator.GenerateAsync"/> call.
/// On success <see cref="Succeeded"/> is true and <see cref="WrittenFiles"/>
/// lists every absolute path written. On failure <see cref="Succeeded"/>
/// is false and <see cref="ErrorMessage"/> carries the diagnostic.
/// </summary>
public sealed record GenerationResult(
    bool Succeeded,
    IReadOnlyList<string> WrittenFiles,
    string? ErrorMessage = null);
