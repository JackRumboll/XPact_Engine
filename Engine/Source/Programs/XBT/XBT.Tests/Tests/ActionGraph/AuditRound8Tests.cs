// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XBT.ActionGraph;
using Simgenics.XPact.XBT.Manifest;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.ActionGraph;

/// <summary>
/// Audit Round 8 fixes covering the action-graph surface:
/// <list type="bullet">
///   <item>R8-Mi3: <see cref="ExternalAction.Create"/> rejects reserved
///   Phase 2 slots so a Phase 1 emit cannot accidentally claim a slot
///   reserved for distributed-cache / live-coding / etc.</item>
///   <item>R8-Mi5: every emit-eligible slot in
///   <see cref="XActionType"/> (i.e. every value whose name does NOT
///   start with <c>Reserved_</c>) is enumerated in
///   <see cref="ContractSurface.ActionTypes"/> so the canonical
///   contract surface tracks every action a Phase 1 build can emit.</item>
/// </list>
/// </summary>
public sealed class AuditRound8Tests
{
    /// <summary>
    /// R8-Mi3: an <see cref="ExternalAction"/> with a
    /// <c>Reserved_*</c> action type fails the
    /// <see cref="ExternalAction.Create"/> gate with
    /// <see cref="ArgumentException"/>. The exception message names the
    /// rejected slot so the build log clearly identifies the offender.
    /// </summary>
    [Theory]
    [InlineData(XActionType.Reserved_DistributedCacheCheck)]
    [InlineData(XActionType.Reserved_DerivedDataFetch)]
    [InlineData(XActionType.Reserved_LiveCodingCascade)]
    [InlineData(XActionType.Reserved_PatchDllEmit)]
    [InlineData(XActionType.Reserved_Phase2_F)]
    [InlineData(XActionType.Reserved_Phase2_G)]
    public void ExternalAction_Create_RejectsReservedSlots(XActionType reservedSlot)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => ExternalAction.Create(new ExternalAction
            {
                ActionType = reservedSlot,
                CommandPath = "/fake/cl.exe",
                WorkingDirectory = "/tmp",
            }));
        Assert.Contains(reservedSlot.ToString(), ex.Message);
        Assert.Contains(((int)reservedSlot).ToString(), ex.Message);
    }

    /// <summary>
    /// R8-Mi3: a named (non-Reserved_) slot passes the gate. Regression
    /// against the prior behaviour where every slot was accepted.
    /// </summary>
    [Theory]
    [InlineData(XActionType.CompileCppAction)]
    [InlineData(XActionType.LinkModuleAction)]
    [InlineData(XActionType.PCHGenerationAction)]
    [InlineData(XActionType.Tier2WholeProgramPass)]
    [InlineData(XActionType.BuildPluginManifestAction)]
    public void ExternalAction_Create_AcceptsNamedSlots(XActionType namedSlot)
    {
        ExternalAction result = ExternalAction.Create(new ExternalAction
        {
            ActionType = namedSlot,
            CommandPath = "/fake/cl.exe",
            WorkingDirectory = "/tmp",
        });
        Assert.Equal(namedSlot, result.ActionType);
    }

    /// <summary>
    /// R8-Mi3: tests that need the reserved slot for graph-shape
    /// coverage can bypass the gate by constructing the record
    /// directly. This is the documented escape hatch (the
    /// determinism-invariant validation runs only inside
    /// <see cref="ExternalAction.Create"/>).
    /// </summary>
    [Fact]
    public void ExternalAction_DirectConstruction_AllowsReservedSlots()
    {
        // The record-literal path skips Create() so reserved-slot
        // construction is permitted for graph-shape tests that need a
        // synthetic placeholder.
        ExternalAction direct = new()
        {
            ActionType = XActionType.Reserved_DistributedCacheCheck,
            CommandPath = "/fake/cache-check.exe",
            WorkingDirectory = "/tmp",
        };
        Assert.Equal(XActionType.Reserved_DistributedCacheCheck, direct.ActionType);
    }

    /// <summary>
    /// Audit fix R8-Mi5: every emit-eligible slot in
    /// <see cref="XActionType"/> (any value whose name does NOT start
    /// with <c>Reserved_</c>) MUST be enumerated in
    /// <see cref="ContractSurface.ActionTypes"/>. This catches the
    /// regression where a developer adds a new named action type but
    /// forgets to register it on the contract surface.
    /// </summary>
    [Fact]
    public void ContractSurface_ActionTypes_CoversEveryNonReservedXActionType()
    {
        string[] allActionTypeNames = Enum.GetNames(typeof(XActionType));
        string[] emitEligible = allActionTypeNames
            .Where(n => !n.StartsWith("Reserved_", StringComparison.Ordinal))
            .ToArray();

        HashSet<string> surfaceSet = new(ContractSurface.ActionTypes, StringComparer.Ordinal);
        List<string> missingFromSurface = new();
        foreach (string name in emitEligible)
        {
            if (!surfaceSet.Contains(name))
            {
                missingFromSurface.Add(name);
            }
        }

        Assert.True(
            missingFromSurface.Count == 0,
            "ContractSurface.ActionTypes is missing the following non-Reserved_ slots " +
            $"from XActionType: {string.Join(", ", missingFromSurface)}. " +
            "Every action type that Phase 1 can emit must be enumerated on the contract " +
            "surface so a rename of any such slot rotates ContractVersion (and forces " +
            "downstream cache invalidation).");
    }

    /// <summary>
    /// Audit fix R8-Mi5: the converse direction --
    /// <see cref="ContractSurface.ActionTypes"/> MUST NOT contain any
    /// entry that isn't a valid <see cref="XActionType"/> name.
    /// Catches the regression where the surface gets stale relative
    /// to the enum.
    /// </summary>
    [Fact]
    public void ContractSurface_ActionTypes_EveryEntryIsAValidXActionTypeName()
    {
        HashSet<string> enumNames = new(Enum.GetNames(typeof(XActionType)), StringComparer.Ordinal);
        List<string> notInEnum = new();
        foreach (string surfaceName in ContractSurface.ActionTypes)
        {
            if (!enumNames.Contains(surfaceName))
            {
                notInEnum.Add(surfaceName);
            }
            if (surfaceName.StartsWith("Reserved_", StringComparison.Ordinal))
            {
                notInEnum.Add(surfaceName + " (reserved slots must not appear on the surface)");
            }
        }

        Assert.True(
            notInEnum.Count == 0,
            "ContractSurface.ActionTypes contains entries that are not valid (non-reserved) " +
            $"XActionType names: {string.Join(", ", notInEnum)}.");
    }
}
