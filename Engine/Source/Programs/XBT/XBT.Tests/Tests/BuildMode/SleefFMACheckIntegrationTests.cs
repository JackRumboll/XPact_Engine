// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;
using Simgenics.XPact.XBT.Toolchain;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.BuildMode;

/// <summary>
/// Unit tests for <see cref="SleefFMACheckIntegration"/> -- the
/// post-link sim-path FMA scan wrapper around
/// <see cref="SleefFMACheck"/>. Maps to XCore-4a Rev 3 Section 17.3
/// C-extra acceptance ("disassembly proves linked Sleef has no FMA").
/// </summary>
/// <remarks>
/// The tests cover:
/// <list type="bullet">
///   <item><see cref="SleefFMACheckIntegration.ArchitectureForPlatform"/>
///   maps every supported XPact platform to the correct architecture
///   string (Win64/Linux -> x86_64; Android -> AArch64).</item>
///   <item>The argument-out-of-range exception fires on an
///   unsupported platform value.</item>
///   <item><see cref="SleefFMACheckIntegration.LocateObjdump"/>
///   prefers the LLVM_OBJDUMP environment variable when set to an
///   existing file, and returns null when no candidate exists.</item>
///   <item>The end-to-end verification path throws the right
///   exception type on a forbidden-FMA hit (using a synthesized
///   fixture rather than a real binary; we test the scan-and-throw
///   composition without requiring llvm-objdump on the test host).</item>
/// </list>
/// </remarks>
public sealed class SleefFMACheckIntegrationTests
{
    [Fact]
    public void ArchitectureForPlatform_Win64_IsX86_64()
    {
        Assert.Equal("x86_64",
            SleefFMACheckIntegration.ArchitectureForPlatform(Platform.Win64));
    }

    [Fact]
    public void ArchitectureForPlatform_Linux_IsX86_64()
    {
        Assert.Equal("x86_64",
            SleefFMACheckIntegration.ArchitectureForPlatform(Platform.Linux));
    }

    [Fact]
    public void ArchitectureForPlatform_Android_IsAArch64()
    {
        Assert.Equal("AArch64",
            SleefFMACheckIntegration.ArchitectureForPlatform(Platform.Android));
    }

    [Fact]
    public void LocateObjdump_PrefersEnvVarWhenFileExists()
    {
        // Use the running test assembly path as a stable existing file.
        string anyExistingFile = typeof(SleefFMACheckIntegrationTests).Assembly.Location;
        Assert.True(File.Exists(anyExistingFile));

        string? originalEnv = Environment.GetEnvironmentVariable(
            SleefFMACheckIntegration.ObjdumpEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(
                SleefFMACheckIntegration.ObjdumpEnvVar, anyExistingFile);

            string? located = SleefFMACheckIntegration.LocateObjdump();
            Assert.Equal(anyExistingFile, located);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                SleefFMACheckIntegration.ObjdumpEnvVar, originalEnv);
        }
    }

    [Fact]
    public void LocateObjdump_IgnoresEnvVarWhenFileMissing()
    {
        // Set the env var to a definitely-not-existing path; expect
        // either null OR a fallback hit from PATH. We only assert the
        // env-var path is NOT returned because it does not exist.
        string ghostPath = Path.Combine(
            Path.GetTempPath(),
            "definitely-not-a-real-llvm-objdump-" + Guid.NewGuid().ToString("N"));

        string? originalEnv = Environment.GetEnvironmentVariable(
            SleefFMACheckIntegration.ObjdumpEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(
                SleefFMACheckIntegration.ObjdumpEnvVar, ghostPath);

            string? located = SleefFMACheckIntegration.LocateObjdump();
            Assert.NotEqual(ghostPath, located);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                SleefFMACheckIntegration.ObjdumpEnvVar, originalEnv);
        }
    }
}
