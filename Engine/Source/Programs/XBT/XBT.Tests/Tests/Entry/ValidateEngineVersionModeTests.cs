// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Entry;
using Simgenics.XPact.XBT.Manifest;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Entry;

/// <summary>
/// Verifies <see cref="ValidateEngineVersionMode"/> exit-code
/// surface alignment with the Toolchain Contract Rev 13 Section 13
/// table reflected in <see cref="ContractSurface.ExitCodes"/>.
/// </summary>
/// <remarks>
/// <para>
/// The Round-1 audit fix briefly mis-mapped the mode's failure to exit
/// code 22 (<c>CycleDetected</c> in the same surface). The locked
/// policy maps every engine-version mismatch -- whether observed by the
/// in-build pipeline or by the standalone <c>validate-engine-version</c>
/// CLI -- to code 23 (<c>EngineOrToolchainVersionMismatch</c>). This
/// test guards the alignment so a future refactor cannot silently
/// re-introduce the 22 mis-mapping.
/// </para>
/// </remarks>
public sealed class ValidateEngineVersionModeTests : IDisposable
{
    private readonly string _scratchDir;

    public ValidateEngineVersionModeTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.ValidateEngineVersionMode",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratchDir))
            {
                Directory.Delete(_scratchDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// The mode's published mismatch exit code is exactly 23, matching
    /// the entry in <see cref="ContractSurface.ExitCodes"/> for the
    /// <c>EngineOrToolchainVersionMismatch</c> family.
    /// </summary>
    [Fact]
    public void ExitCode_AlignsWithContractSurface_23()
    {
        Assert.Equal(23, ValidateEngineVersionMode.EngineVersionMismatchExitCode);

        // Confirm the contract surface still defines 23 with the
        // expected mnemonic -- if either drifts, both checks fail in
        // the same test.
        (int Code, string Mnemonic) entry = default;
        foreach ((int code, string mnemonic) in ContractSurface.ExitCodes)
        {
            if (code == 23)
            {
                entry = (code, mnemonic);
                break;
            }
        }
        Assert.Equal(23, entry.Code);
        Assert.Equal("EngineOrToolchainVersionMismatch", entry.Mnemonic);
    }

    /// <summary>
    /// Contract surface bookkeeping canary: code 22 is reserved for
    /// <c>CycleDetected</c> and must not be reused for engine-version
    /// mismatches. The mode-side exit code therefore cannot equal 22.
    /// </summary>
    [Fact]
    public void ExitCode_DoesNotCollide_With_CycleDetected_22()
    {
        Assert.NotEqual(22, ValidateEngineVersionMode.EngineVersionMismatchExitCode);

        bool found22 = false;
        string mnemonic22 = string.Empty;
        foreach ((int code, string mnemonic) in ContractSurface.ExitCodes)
        {
            if (code == 22)
            {
                found22 = true;
                mnemonic22 = mnemonic;
                break;
            }
        }
        Assert.True(found22, "Exit code 22 must be defined in ContractSurface.ExitCodes.");
        Assert.Equal("CycleDetected", mnemonic22);
    }

    /// <summary>
    /// End-to-end: invoke the mode against a scratch engine whose
    /// <c>Engine.xengine</c> is intentionally missing. The mode walks
    /// up looking for the descriptor, fails to find it, and returns
    /// exit code 23 -- not 22.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_OnMissingEngineDescriptor_Returns_23()
    {
        // Point -Engine= at a directory that does NOT contain
        // Engine.xengine so DiscoverEngineVersion throws.
        string fakeEngineRoot = Path.Combine(_scratchDir, "FakeEngine");
        Directory.CreateDirectory(fakeEngineRoot);

        ValidateEngineVersionMode mode = new();
        string[] args = new[] { $"-Engine={fakeEngineRoot}" };

        int exit = await mode.ExecuteAsync(args, CancellationToken.None);
        Assert.Equal(23, exit);
    }

    /// <summary>
    /// Round-6 final-cleanup M2: the mode accepts the spec-canonical
    /// <c>-EngineRoot=</c> flag in addition to the legacy <c>-Engine=</c>
    /// alias. Both resolve to the same engine root override.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AcceptsSpecCanonical_EngineRootFlag()
    {
        string fakeEngineRoot = Path.Combine(_scratchDir, "FakeEngineRootFlag");
        Directory.CreateDirectory(fakeEngineRoot);

        ValidateEngineVersionMode mode = new();
        int exit = await mode.ExecuteAsync(
            new[] { $"-EngineRoot={fakeEngineRoot}" },
            CancellationToken.None);

        // The descriptor is missing -- but the parser accepted the new
        // flag (otherwise we'd get exit 10 for unknown argument).
        Assert.Equal(23, exit);
    }
}
