// Copyright Simgenics. All Rights Reserved.

using Xunit;

namespace Simgenics.XPact.XBT.Tests;

/// <summary>
/// xUnit collection that serialises every test which reads or writes
/// <see cref="Simgenics.XPact.XBT.Core.ToolchainSelfHash"/>'s process-wide
/// override slot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this collection exists.</b>
/// <c>ToolchainSelfHash.XbtBinaryHash</c> is a process-wide global that
/// every C++ toolchain emit site folds into its action cache key (audit
/// fix R8-M4). Tests that exercise the override
/// (<c>ToolchainSelfHash.__SetForTesting("...")</c>) and tests that
/// assert cache-key determinism (two back-to-back
/// <c>CompileSource</c>/<c>GenerateSharedPCH</c> calls must produce the
/// same <c>CommandVersion</c>) cannot run in parallel: if the
/// override-set lands between the two reads, the determinism assertion
/// observes a mismatch and the test fails non-deterministically. xUnit's
/// default per-class parallelism makes this the runner's default
/// behaviour, so the only correct fix is explicit serialisation.
/// </para>
/// <para>
/// <b>Why a shared collection, not per-class
/// <c>[CollectionDefinition]</c>.</b> The LoggerTests-style pattern (one
/// class decorated with both <c>[Collection]</c> and
/// <c>[CollectionDefinition]</c>) only serialises a single class against
/// itself. Multiple unrelated test classes touch <c>ToolchainSelfHash</c>
/// (the override-writers <c>XMSVCToolChainTests</c> /
/// <c>XClangToolChainTests</c> / <c>AuditRound3Tests</c>; the
/// determinism-asserters <c>SharedPchTests</c> / <c>PCHGenerationTests</c>;
/// and any future test that calls toolchain methods which fold the hash
/// into a cache key); they must share one named collection so xUnit
/// serialises across class boundaries, not just within.
/// </para>
/// <para>
/// <b>Why not finer-grained locking inside <c>ToolchainSelfHash</c>.</b>
/// The override is test-only — production code never calls
/// <c>__SetForTesting</c>. A per-call lock or copy-on-read snapshot in
/// <c>ToolchainSelfHash</c> would solve the symptom but add runtime cost
/// to every cache-key read in production builds (where the override is
/// permanently null). xUnit-level isolation pays the cost only in the
/// test harness, which is the architecturally correct trade.
/// </para>
/// <para>
/// <b>Coding standard (audit fix R5-M3):</b> ANY test class that
/// constructs <see cref="Simgenics.XPact.XBT.Toolchain.XMSVCToolChain"/>
/// or <see cref="Simgenics.XPact.XBT.Toolchain.XClangToolChain"/>, OR
/// calls anything that does (notably
/// <see cref="Simgenics.XPact.XBT.Entry.BuildMode.Run"/>,
/// <see cref="Simgenics.XPact.XBT.ProjectFiles.RiderProjectGenerator"/>,
/// <see cref="Simgenics.XPact.XBT.ProjectFiles.ClangdCompileCommandsGenerator"/>),
/// MUST decorate itself with
/// <c>[Collection(nameof(ToolchainSelfHashCollection))]</c>. The
/// invariant is enforced by
/// <see cref="Simgenics.XPact.XBT.Tests.Tests.ToolchainSelfHashCollectionMembershipTests"/>
/// at runtime: that test scans the assembly's test types for direct
/// XMSVCToolChain / XClangToolChain construction and asserts every
/// matching class carries the collection attribute. A new test that
/// touches the toolchain without joining the collection fails the
/// membership check before it can introduce a parallel-test flake.
/// </para>
/// </remarks>
[CollectionDefinition(nameof(ToolchainSelfHashCollection), DisableParallelization = true)]
public sealed class ToolchainSelfHashCollection
{
    // Marker type. Intentionally empty: xUnit discovers the
    // CollectionDefinition attribute on the type, and the type name is
    // the collection's identifier referenced from [Collection(...)] on
    // each participating test class.
}
