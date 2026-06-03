// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Entry;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Entry;

/// <summary>
/// Tests for <see cref="Program"/>'s mode dispatch + exit-code path per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 15 + the Toolchain
/// Contract Section 13.1 exit-code surface.
/// </summary>
/// <remarks>
/// <para>
/// Tests invoke <see cref="Program.Main"/> directly; the Logger's stderr
/// override captures the diagnostic text so we can assert message
/// content. Logger state is process-global so the collection serialises
/// these tests against the parallel Logger-touching collections.
/// </para>
/// <para>
/// The <c>Main_Unhandled...</c> test exercises the Phase 6.a catch
/// surface that maps an arbitrary unhandled exception to exit 63
/// (Xil2CppInternalFailure) with an "error XIL2CPP900: ..." stderr line.
/// The manifest-malformed (exit 50) catch branch lands in a later
/// sub-phase once the XIL2CPP.Manifest types exist.
/// </para>
/// </remarks>
[Collection(nameof(ProgramTests))]
[CollectionDefinition(nameof(ProgramTests), DisableParallelization = true)]
public sealed class ProgramTests : IDisposable
{
    public ProgramTests()
    {
        Logger.DisableJsonChannel();
        Logger.ResetCounters();
        Logger.__SetStderrForTesting(null);
    }

    public void Dispose()
    {
        Logger.DisableJsonChannel();
        Logger.ResetCounters();
        Logger.__SetStderrForTesting(null);
    }

    [Fact]
    public async Task Main_NoArgs_RoutesToHelp_ReturnsZero()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        int exit = await Program.Main(Array.Empty<string>());

        Assert.Equal(ExitCodes.Success, exit);
        // Help should mention the version banner.
        Assert.Contains("XIL2CPP (XPact IL-to-C++ transpiler)", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_HelpShortcut_RoutesToHelp_ReturnsZero()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        int exit = await Program.Main(new[] { "--help" });

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Modes:", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_DashHShortcut_RoutesToHelp_ReturnsZero()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        int exit = await Program.Main(new[] { "-h" });

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Modes:", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_VersionShortcut_RoutesToVersion_ReturnsZero()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        int exit = await Program.Main(new[] { "--version" });

        Assert.Equal(ExitCodes.Success, exit);
        // The version banner mentions XIL2CPP + the semver string.
        Assert.Contains("XIL2CPP ", sw.ToString(), StringComparison.Ordinal);
        Assert.Contains(Xil2CppVersion.Semver, sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_UnknownMode_ReturnsCliArgumentError()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        int exit = await Program.Main(new[] { "no-such-mode" });

        Assert.Equal(ExitCodes.CliArgumentError, exit);
        Assert.Contains("Unknown mode", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_HelpMode_ListsRegisteredModes()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        int exit = await Program.Main(new[] { "help" });

        Assert.Equal(ExitCodes.Success, exit);
        string output = sw.ToString();
        Assert.Contains("help", output, StringComparison.Ordinal);
        Assert.Contains("version", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_InvalidJsonFdFlag_ReturnsCliArgumentError()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        int exit = await Program.Main(new[] { "-JsonFd=not-a-number", "help" });

        Assert.Equal(ExitCodes.CliArgumentError, exit);
        Assert.Contains("JsonFd", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_ValidJsonFdFlag_StrippedBeforeDispatch_ReturnsZero()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        // A well-formed -JsonFd= flag is consumed by the pre-pass and the
        // residual ("help") dispatches normally. The pre-pass does not
        // open a writer in Phase 6.a; it only validates the value.
        int exit = await Program.Main(new[] { "-JsonFd=0", "help" });

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Modes:", sw.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Positive coverage for the XIL2CPP900 ICE branch in
    /// <see cref="Program.Main"/>. We inject a test-only mode via
    /// <see cref="ToolModeRegistry.__RegisterForTesting"/> that throws an
    /// arbitrary unhandled exception and assert the catch site:
    /// <list type="bullet">
    /// <item><description>exits with <see cref="ExitCodes.Xil2CppInternalFailure"/> (63)</description></item>
    /// <item><description>emits an "error XIL2CPP900: ..." stderr line</description></item>
    /// <item><description>names the unhandled path as an XIL2CPP-side bug</description></item>
    /// <item><description>surfaces the inner exception's <c>ToString()</c> output for the stack trace</description></item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task Main_UnhandledException_EmitsXIL2CPP900OnStderr_AndReturns63()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        const string injectedMessage = "synthetic unhandled error (XIL2CPP900 positive coverage)";
        ToolModeRegistry.__RegisterForTesting(
            "test-xil2cpp900-ice-injector",
            new TestUnhandledThrowMode(injectedMessage));

        try
        {
            int exit = await Program.Main(new[] { "test-xil2cpp900-ice-injector" });

            Assert.Equal(ExitCodes.Xil2CppInternalFailure, exit);
            string stderr = sw.ToString();
            // The catalog-anchored XIL2CPP900 line MUST land on stderr.
            Assert.Contains("error XIL2CPP900:", stderr, StringComparison.Ordinal);
            // The branch must call out the unhandled path as an XIL2CPP-side bug.
            Assert.Contains("XIL2CPP-side bug", stderr, StringComparison.Ordinal);
            // The branch must surface the inner message so the operator has
            // a starting point for triage.
            Assert.Contains(injectedMessage, stderr, StringComparison.Ordinal);
            // The branch must emit ex.ToString() so the stack trace is
            // captured for issue-tracker triage; the exception type's
            // full name is the canary that ToString() ran.
            Assert.Contains(
                typeof(InvalidOperationException).FullName!,
                stderr,
                StringComparison.Ordinal);
        }
        finally
        {
            // Restore the registry so the injected mode does not leak
            // to other tests in this collection.
            ToolModeRegistry.__ResetForTesting();
        }
    }

    /// <summary>
    /// Test-only mode that throws an arbitrary unhandled exception so the
    /// XIL2CPP900 ICE branch in <see cref="Program.Main"/> can be
    /// exercised end-to-end. Registered via
    /// <see cref="ToolModeRegistry.__RegisterForTesting"/> for the
    /// duration of the test, then dropped on teardown.
    /// </summary>
    private sealed class TestUnhandledThrowMode : IToolMode
    {
        private readonly string _message;

        public TestUnhandledThrowMode(string message)
        {
            _message = message;
        }

        public string Name => "test-xil2cpp900-ice-injector";

        public string Description => "Test-only XIL2CPP900 ICE-branch injector.";

        public Task<int> ExecuteAsync(string[] args, CancellationToken ct)
        {
            throw new InvalidOperationException(_message);
        }
    }
}
