// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Entry;
using Simgenics.XPact.XHT.Manifest;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Entry;

/// <summary>
/// Tests for <see cref="Program"/>'s mode dispatch + exit-code path per
/// <c>/Documents/XHT.html</c> Rev 7 Section 1.3.
/// </summary>
/// <remarks>
/// <para>
/// Tests invoke <see cref="Program.Main"/> directly; the Logger's stderr
/// override captures the diagnostic text so we can assert message
/// content. Logger state is process-global so the collection serialises
/// these tests against the parallel LoggerTests collection.
/// </para>
/// <para>
/// Round 5 R4-CR2: the end-to-end <c>Main_</c>...<c>_EmitsXHT</c><i>NNN</i>
/// tests near the bottom of this class exercise the entry-point catch
/// surface that maps a <see cref="Simgenics.XPact.XHT.Manifest.ManifestMalformedException"/>
/// to an "error XHT<i>NNN</i>: ..." stderr line. Each test asserts both
/// the exit code AND that the catalog-anchored diagnostic code (per
/// <c>/Documents/XHT.html</c> Section 23.2) lands on stderr.
/// </para>
/// </remarks>
[Collection(nameof(ProgramTests))]
[CollectionDefinition(nameof(ProgramTests), DisableParallelization = true)]
public sealed class ProgramTests : IDisposable
{
    private readonly string _tempDir;

    public ProgramTests()
    {
        Logger.DisableJsonChannel();
        Logger.ResetCounters();
        Logger.__SetStderrForTesting(null);
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "XHT.Tests-Program-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Logger.DisableJsonChannel();
        Logger.ResetCounters();
        Logger.__SetStderrForTesting(null);
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task Main_NoArgs_RoutesToHelp_ReturnsZero()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        int exit = await Program.Main(Array.Empty<string>());

        Assert.Equal(ExitCodes.Success, exit);
        // Help should mention the version banner.
        Assert.Contains("XHT (XPact Header Tool)", sw.ToString(), StringComparison.Ordinal);
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
        // The version banner mentions XHT + the semver string.
        Assert.Contains("XHT ", sw.ToString(), StringComparison.Ordinal);
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
    public async Task Main_HelpMode_ListsAllRegisteredModes()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        int exit = await Program.Main(new[] { "help" });

        Assert.Equal(ExitCodes.Success, exit);
        string output = sw.ToString();
        Assert.Contains("parse-module", output, StringComparison.Ordinal);
        Assert.Contains("emit-module", output, StringComparison.Ordinal);
        Assert.Contains("validate-only", output, StringComparison.Ordinal);
        Assert.Contains("dump-ast", output, StringComparison.Ordinal);
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
    public async Task Main_ParseModuleMissingManifest_ReturnsCliArgumentError()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        int exit = await Program.Main(new[] { "parse-module", "-Module=X", "-Out=." });

        Assert.Equal(ExitCodes.CliArgumentError, exit);
    }

    [Fact]
    public async Task Main_ParseModuleNonexistentManifest_ReturnsManifestMalformed()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        string fakePath = Path.Combine(Path.GetTempPath(), $"no-such-manifest-{Guid.NewGuid():N}.json");
        int exit = await Program.Main(new[]
        {
            "parse-module",
            $"-Manifest={fakePath}",
            "-Module=X",
            "-Out=.",
        });

        Assert.Equal(ExitCodes.ManifestMalformed, exit);
    }

    // ---------------------------------------------------------------------
    // Round 5 R4-CR2: end-to-end tests that exercise Program.Main's catch
    // surface for catalog-anchored manifest-malformed diagnostic codes per
    // /Documents/XHT.html Rev 7 Section 23.2. Each test:
    //   (a) writes a synthetic manifest condition to disk (or omits the
    //       file entirely),
    //   (b) invokes Program.Main with the parse-module mode,
    //   (c) asserts exit code = 50 (ManifestMalformed), and
    //   (d) asserts the catalog-anchored "error XHT<NNN>:" string lands
    //       on stderr (so operators reading the build log see the
    //       actionable code rather than the legacy XHT050 shim).
    // ---------------------------------------------------------------------

    /// <summary>
    /// XHT001 -- Manifest not found. The XbtManifestReader's
    /// File.Exists branch fires with diagnosticCode "XHT001"; the
    /// entry-point catch surfaces "error XHT001: ...".
    /// </summary>
    [Fact]
    public async Task Main_ParseModuleNonexistentManifest_EmitsXHT001OnStderr()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        string fakePath = Path.Combine(_tempDir, $"no-such-manifest-{Guid.NewGuid():N}.json");
        Assert.False(File.Exists(fakePath), "Sanity: the synthetic manifest path must not exist.");

        int exit = await Program.Main(new[]
        {
            "parse-module",
            $"-Manifest={fakePath}",
            "-Module=X",
            $"-Out={_tempDir}",
        });

        Assert.Equal(ExitCodes.ManifestMalformed, exit);
        string stderr = sw.ToString();
        Assert.Contains("error XHT001:", stderr, StringComparison.Ordinal);
        Assert.Contains(fakePath, stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// XHT002 -- ContractVersion mismatch. The reader fires when the
    /// manifest's ContractVersion does not match XHT's compile-time pin;
    /// the entry-point catch surfaces "error XHT002: ..." with both the
    /// observed and expected values so operators can decide which side to
    /// rebuild.
    /// </summary>
    [Fact]
    public async Task Main_ParseModuleWithMismatchedContractVersion_EmitsXHT002OnStderr()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        const string mismatchedVersion = "99.99+deadbeefcafebabe";
        string manifestPath = WriteManifestWithContractVersion(mismatchedVersion);
        string outDir = Path.Combine(_tempDir, "Out");
        Directory.CreateDirectory(outDir);

        int exit = await Program.Main(new[]
        {
            "parse-module",
            $"-Manifest={manifestPath}",
            "-Module=XScoring",
            $"-Out={outDir}",
        });

        Assert.Equal(ExitCodes.ManifestMalformed, exit);
        string stderr = sw.ToString();
        Assert.Contains("error XHT002:", stderr, StringComparison.Ordinal);
        // Operator-actionable diagnostic must name BOTH versions.
        Assert.Contains(mismatchedVersion, stderr, StringComparison.Ordinal);
        Assert.Contains(XhtVersion.ContractVersion, stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// XHT003 -- Manifest verifier rejection (depth-limit violation in
    /// the hardened JSON reader). Fires when the manifest JSON nests
    /// beyond <c>XbtManifestReader.MaxJsonDepth</c> = 64.
    /// </summary>
    [Fact]
    public async Task Main_ParseModuleWithExcessiveDepth_EmitsXHT003OnStderr()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        // Construct a JSON payload that exceeds MaxJsonDepth = 64. 80
        // levels of array-nesting trips the hardened-reader pre-pass
        // before the binder runs. The contents are otherwise irrelevant
        // -- the validator never reaches them.
        StringBuilder sb = new();
        for (int i = 0; i < 80; i++) { sb.Append('['); }
        for (int i = 0; i < 80; i++) { sb.Append(']'); }
        string manifestPath = Path.Combine(_tempDir, "deep.json");
        File.WriteAllText(manifestPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        string outDir = Path.Combine(_tempDir, "Out");
        Directory.CreateDirectory(outDir);

        int exit = await Program.Main(new[]
        {
            "parse-module",
            $"-Manifest={manifestPath}",
            "-Module=X",
            $"-Out={outDir}",
        });

        Assert.Equal(ExitCodes.ManifestMalformed, exit);
        Assert.Contains("error XHT003:", sw.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// XHT004 -- Module not in manifest. The ParseModuleMode dispatch
    /// throws when <c>XbtManifestReader.FindModule</c> returns null; the
    /// entry-point catch surfaces "error XHT004: ...". Replaces the
    /// pre-Round-5 XHT050 shim per the R4-CR1 fix.
    /// </summary>
    [Fact]
    public async Task Main_ParseModuleWithMissingModule_EmitsXHT004OnStderr()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        string manifestPath = TestManifestBuilder.WriteOneModuleManifest(_tempDir, "Existing");
        string outDir = Path.Combine(_tempDir, "Out");
        Directory.CreateDirectory(outDir);

        int exit = await Program.Main(new[]
        {
            "parse-module",
            $"-Manifest={manifestPath}",
            "-Module=NotPresent",
            $"-Out={outDir}",
        });

        Assert.Equal(ExitCodes.ManifestMalformed, exit);
        string stderr = sw.ToString();
        Assert.Contains("error XHT004:", stderr, StringComparison.Ordinal);
        Assert.Contains("NotPresent", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// The R4-CR1 strict-assertion path: a ManifestMalformedException with
    /// a null DiagnosticCode now surfaces as XHT900 (internal compiler
    /// error) rather than the legacy XHT050 shim. The current production
    /// code paths all carry diagnostic codes, so this defensive surface
    /// fires only when a future throw site forgets to anchor itself. We
    /// validate the shape by inspecting the catch-flow indirectly --
    /// every existing test that exercises ManifestMalformedException
    /// must continue to surface a non-XHT900 code, which the four tests
    /// above already cover.
    /// </summary>
    [Fact]
    public async Task Main_ParseModuleWithMismatchedContractVersion_DoesNotEmitXHT900Fallback()
    {
        // Regression guard for the R4-CR1 fix: an exception that DOES
        // carry a DiagnosticCode must NOT trip the XHT900 ICE branch.
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        string manifestPath = WriteManifestWithContractVersion("99.99+deadbeefcafebabe");
        string outDir = Path.Combine(_tempDir, "Out");
        Directory.CreateDirectory(outDir);

        int exit = await Program.Main(new[]
        {
            "parse-module",
            $"-Manifest={manifestPath}",
            "-Module=XScoring",
            $"-Out={outDir}",
        });

        Assert.Equal(ExitCodes.ManifestMalformed, exit);
        string stderr = sw.ToString();
        // The XHT900 ICE branch is the un-anchored-throw-site defence;
        // a properly-anchored XHT002 throw must not trip it.
        Assert.DoesNotContain("XHT900", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("XHT050", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// Round 7 R6-XH1 positive coverage for the XHT900 ICE branch in
    /// <see cref="Program.Main"/>. Production throw sites all carry a
    /// catalog-anchored DiagnosticCode (R5-XHT-CR2 fix) and
    /// <see cref="ManifestMalformedException"/>'s no-code constructors
    /// are <c>internal</c> (R6-XH1 fix). We inject a test-only mode via
    /// <see cref="ToolModeRegistry.__RegisterForTesting"/> that throws
    /// the un-anchored exception and assert the catch site:
    /// <list type="bullet">
    /// <item><description>exits with <see cref="ExitCodes.ManifestMalformed"/> (50)</description></item>
    /// <item><description>emits an "error XHT900: ..." stderr line</description></item>
    /// <item><description>names the un-anchored throw as a code-side bug</description></item>
    /// <item><description>surfaces the inner exception's <c>ToString()</c> output for the stack trace</description></item>
    /// </list>
    /// Without this test the XHT900 branch had only the negative-guard
    /// above; a future regression that broke the XHT900 emit (a null-ref
    /// on <c>ex.Message</c>, a wording change that lost "code-side bug",
    /// etc.) would not be caught.
    /// </summary>
    [Fact]
    public async Task Main_UnanchoredManifestMalformedException_EmitsXHT900OnStderr()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        const string injectedMessage = "synthetic un-anchored manifest error (R6-XH1 positive coverage)";
        ToolModeRegistry.__RegisterForTesting(
            "test-xht900-ice-injector",
            new TestUnanchoredManifestThrowMode(injectedMessage));

        try
        {
            int exit = await Program.Main(new[] { "test-xht900-ice-injector" });

            Assert.Equal(ExitCodes.ManifestMalformed, exit);
            string stderr = sw.ToString();
            // The catalog-anchored XHT900 line MUST land on stderr.
            Assert.Contains("error XHT900:", stderr, StringComparison.Ordinal);
            // The branch must call out the un-anchored throw as a
            // code-side bug so the developer who introduced it fixes it.
            Assert.Contains("ManifestMalformedException", stderr, StringComparison.Ordinal);
            Assert.Contains("DiagnosticCode anchor", stderr, StringComparison.Ordinal);
            // The branch must surface the inner message so the operator
            // has a starting point for triage.
            Assert.Contains(injectedMessage, stderr, StringComparison.Ordinal);
            // The branch must emit ex.ToString() so the stack trace is
            // captured for issue-tracker triage; the class's fully-
            // qualified name is the canary that ToString() ran.
            Assert.Contains(
                "Simgenics.XPact.XHT.Manifest.ManifestMalformedException",
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
    /// Test-only mode that throws the (internal, R6-XH1 lockdown)
    /// un-anchored <see cref="ManifestMalformedException"/> so the
    /// XHT900 ICE branch in <see cref="Program.Main"/> can be exercised
    /// end-to-end. Registered via
    /// <see cref="ToolModeRegistry.__RegisterForTesting"/> for the
    /// duration of the test, then dropped on teardown.
    /// </summary>
    private sealed class TestUnanchoredManifestThrowMode : IToolMode
    {
        private readonly string _message;

        public TestUnanchoredManifestThrowMode(string message)
        {
            _message = message;
        }

        public string Name => "test-xht900-ice-injector";

        public string Description => "Test-only XHT900 ICE-branch injector (Round 7 R6-XH1).";

        public Task<int> ExecuteAsync(string[] args, CancellationToken ct)
        {
            // Hits the (now internal) un-anchored constructor. The
            // resulting exception has DiagnosticCode = null, which is
            // exactly the condition Program.Main's XHT900 branch
            // defends against.
            throw new ManifestMalformedException(_message);
        }
    }

    /// <summary>
    /// Helper: write a manifest with a specific <c>ContractVersion</c>
    /// value (used by the XHT002 mismatch tests). All other fields are
    /// the minimal-valid shape from <see cref="TestManifestBuilder"/>'s
    /// one-module template.
    /// </summary>
    private string WriteManifestWithContractVersion(string contractVersion)
    {
        string json = $$"""
            {
              "ContractVersion": "{{contractVersion}}",
              "EngineVersion": "0.1.0",
              "Target": {
                "Name": "MiningTrainingEditor",
                "Type": "Editor",
                "Platform": "Win64",
                "Configuration": "Development",
                "Architecture": "x86_64",
                "GCRootABI": "Span-based v1",
                "ExceptionABI": "Tier1-Shim/Tier2-Direct",
                "ManglingScheme": "Itanium-LengthPrefixed-v1",
                "FipsMode": false,
                "SimPathConservativeRootsAllowed": false,
                "SimdLevelDefault": "SSE42",
                "StationRole": "None"
              },
              "RootLocalPath": "C:/repo",
              "ExternalDependenciesFile": null,
              "Modules": [
                {
                  "Name": "XScoring",
                  "Tier": "Engine",
                  "ModuleType": "Runtime",
                  "Languages": "Both",
                  "BaseDirectory": "Engine/Source/Runtime/XScoring",
                  "SourceFiles": [],
                  "PublicHeaders": [],
                  "PrivateHeaders": [],
                  "InternalHeaders": [],
                  "CSharpSources": [],
                  "IncludePaths": [],
                  "PublicDefines": [],
                  "ModuleDependencies": [],
                  "GeneratedCPPFilenameBase": "XScoring",
                  "SimPath": false,
                  "EngineVersionCompat": "0.1.0",
                  "SimdLevel": "Default",
                  "PCHUsage": "Default",
                  "ExcludeFromSharedPCH": false,
                  "AllowHotReload": false,
                  "IsTestModule": false,
                  "DeprecationMessage": null,
                  "MinimumToolchainVersion": null
                }
              ]
            }
            """;

        string path = Path.Combine(_tempDir, "Manifest.json");
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
