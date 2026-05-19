// Copyright Simgenics. All Rights Reserved.

using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.Discovery;

/// <summary>
/// Sink for discovery-time diagnostics: plugin / module parse
/// failures, EngineVersion mismatches, filesystem access errors, and
/// the Phase 1.2.1 Roslyn-fallback-pending marker.
/// </summary>
/// <remarks>
/// The interface separates discovery's diagnostic surface from the
/// concrete <see cref="Logger"/> so unit tests can inject a recording
/// stub and assert exact diagnostic emissions without touching the
/// process-wide logger state.
/// </remarks>
public interface IDiscoveryDiagnostics
{
    /// <summary>
    /// A filesystem walk failed (typically: permission denied, missing
    /// directory). Discovery continues with whatever was reachable.
    /// </summary>
    void ReportDiscoveryFailure(string root, string message);

    /// <summary>
    /// A <c>.xplugin</c> failed to parse. The plugin is omitted from
    /// the catalog.
    /// </summary>
    void ReportPluginParseFailure(string descriptorPath, string message);

    /// <summary>
    /// A <c>.Build.toml</c> failed to parse. The module is omitted
    /// from the catalog.
    /// </summary>
    void ReportModuleParseFailure(string descriptorPath, string message);

    /// <summary>
    /// A <c>.Build.cs</c> file was observed but the Phase 1 Roslyn
    /// escape hatch is not yet implemented (Phase 1.2.1). The module
    /// is omitted; the caller decides whether to fail the build.
    /// </summary>
    void ReportRoslynFallbackPending(string descriptorPath);
}

/// <summary>
/// Default <see cref="IDiscoveryDiagnostics"/> implementation that
/// routes every diagnostic through XBT's process-wide
/// <see cref="Logger"/>. Use this in production; pass a stub in unit
/// tests.
/// </summary>
public sealed class DiscoveryDiagnostics : IDiscoveryDiagnostics
{
    /// <summary>
    /// Singleton instance. <see cref="DiscoveryDiagnostics"/> is
    /// stateless so a single instance suffices.
    /// </summary>
    public static readonly DiscoveryDiagnostics Default = new();

    private DiscoveryDiagnostics() { }

    /// <inheritdoc/>
    public void ReportDiscoveryFailure(string root, string message)
    {
        Logger.Warning(
            $"Plugin/module discovery failed under '{root}': {message}. " +
            "Continuing with the rest of the walk.",
            new DiagnosticContext { Action = "discover" });
    }

    /// <inheritdoc/>
    public void ReportPluginParseFailure(string descriptorPath, string message)
    {
        Logger.Error(
            $".xplugin parse failed at {descriptorPath}: {message}. The plugin is omitted from the catalog.",
            exitCode: 50,
            context: new DiagnosticContext
            {
                Action = "discover",
                File = descriptorPath,
            });
    }

    /// <inheritdoc/>
    public void ReportModuleParseFailure(string descriptorPath, string message)
    {
        Logger.Error(
            $".Build.toml parse failed at {descriptorPath}: {message}. The module is omitted from the catalog.",
            exitCode: 30,
            context: new DiagnosticContext
            {
                Action = "discover",
                File = descriptorPath,
            });
    }

    /// <inheritdoc/>
    public void ReportRoslynFallbackPending(string descriptorPath)
    {
        Logger.Warning(
            $".Build.cs file observed at {descriptorPath} but the Phase 1 Roslyn fallback is not yet implemented. " +
            "The module is omitted. Phase 1.2.1 will land the implementation; for now, " +
            "supply a .Build.toml descriptor for this module.",
            new DiagnosticContext
            {
                Action = "discover",
                File = descriptorPath,
            });
    }
}
