// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Simgenics.XPact.XBT.ActionGraph;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.LiveCoding;
using Simgenics.XPact.XBT.Manifest;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.LiveCoding;

/// <summary>
/// Verifies <see cref="PatcherRegistry"/> behaviour. Phase 1 ships
/// with an empty registry; the BuildMode call to <c>ApplyAll</c> is a
/// no-op. Phase 2 XLiveCoding registers patchers that fire in
/// registration order. Per <c>/Documents/XBT.html</c> Rev 4 Section 16.5.
/// </summary>
public sealed class PatcherRegistryTests
{
    /// <summary>
    /// An empty registry's <c>ApplyAll</c> must not mutate the action
    /// list at all -- the Phase 1 no-op contract.
    /// </summary>
    [Fact]
    public void EmptyRegistry_ApplyAll_IsNoOp()
    {
        PatcherRegistry registry = new();
        Assert.Empty(registry.Patchers);

        // We pass an empty action list and a synthetic context; the
        // call must complete without touching either.
        List<LinkedAction> actions = new();
        PatcherContext ctx = MakeContext();

        registry.ApplyAll(actions, ctx);

        // No patchers registered -> no mutation.
        Assert.Empty(actions);
    }

    /// <summary>
    /// A registered patcher's <c>Patch</c> method is invoked exactly
    /// once per <c>ApplyAll</c> call, in registration order.
    /// </summary>
    [Fact]
    public void RegisteredPatcher_PatchInvokedExactlyOnce_InRegistrationOrder()
    {
        PatcherRegistry registry = new();
        RecordingPatcher first = new("first");
        RecordingPatcher second = new("second");

        registry.Register(first);
        registry.Register(second);

        Assert.Equal(2, registry.Patchers.Count);

        List<LinkedAction> actions = new();
        PatcherContext ctx = MakeContext();
        registry.ApplyAll(actions, ctx);

        Assert.Equal(1, first.CallCount);
        Assert.Equal(1, second.CallCount);
        // Sequence numbers prove registration-order dispatch.
        Assert.True(first.LastCallSequence < second.LastCallSequence);
    }

    /// <summary>
    /// Patchers receive the same context instance passed in.
    /// </summary>
    [Fact]
    public void RegisteredPatcher_ReceivesPassedContext()
    {
        PatcherRegistry registry = new();
        RecordingPatcher patcher = new("p");
        registry.Register(patcher);

        PatcherContext ctx = MakeContext();
        List<LinkedAction> actions = new();
        registry.ApplyAll(actions, ctx);

        Assert.Same(ctx, patcher.LastContext);
        Assert.Same(actions, patcher.LastActions);
    }

    private static PatcherContext MakeContext() => new(
        Target: new TargetRules
        {
            Name = "TestTarget",
            TargetType = BuildTargetType.Editor,
            Platform = Platform.Win64,
            Configuration = BuildConfiguration.Development,
            StationRole = StationRole.Engineer,
        },
        EngineRootPath: @"C:\repo\Engine",
        LogChannel: "LiveCoding.Test");

    /// <summary>
    /// Test double: a patcher that counts invocations and records the
    /// last call's arguments so the test can assert against them.
    /// </summary>
    private sealed class RecordingPatcher : IActionGraphPatcher
    {
        private static int s_sequenceGenerator;

        public string Name { get; }
        public int CallCount { get; private set; }
        public int LastCallSequence { get; private set; }
        public IList<LinkedAction>? LastActions { get; private set; }
        public PatcherContext? LastContext { get; private set; }

        public RecordingPatcher(string name) { Name = name; }

        public void Patch(IList<LinkedAction> actions, PatcherContext context)
        {
            CallCount++;
            LastActions = actions;
            LastContext = context;
            LastCallSequence = System.Threading.Interlocked.Increment(ref s_sequenceGenerator);
        }
    }
}
