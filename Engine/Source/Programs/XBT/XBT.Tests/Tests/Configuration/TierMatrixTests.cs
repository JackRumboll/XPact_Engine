// Copyright Simgenics. All Rights Reserved.

using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Manifest;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Configuration;

/// <summary>
/// Verifies every cell of the cross-tier dependency-edge matrix per
/// Toolchain Contract Rev 13 Section 9.3 and
/// <c>/Documents/XBT.html</c> Rev 4 Section 13.1.
/// </summary>
/// <remarks>
/// The matrix is the authoritative cross-tier rule; the tests below
/// exhaust the 3 x 3 x 2 = 18-cell product (3 source tiers x 3
/// destination tiers x 2 dependency kinds) by enumerating each cell
/// explicitly. A spec drift that flips any single cell will fail
/// exactly one test, naming the offending cell.
/// </remarks>
public sealed class TierMatrixTests
{
    // ----- Same-tier link edges always allowed -----

    [Fact]
    public void Engine_To_Engine_Link_Allowed()
    {
        Assert.True(TierMatrix.IsEdgeAllowed(ModuleTier.Engine, ModuleTier.Engine, DependencyKind.Link));
    }

    [Fact]
    public void Studio_To_Studio_Link_Allowed()
    {
        Assert.True(TierMatrix.IsEdgeAllowed(ModuleTier.Studio, ModuleTier.Studio, DependencyKind.Link));
    }

    [Fact]
    public void Project_To_Project_Link_Allowed()
    {
        Assert.True(TierMatrix.IsEdgeAllowed(ModuleTier.Project, ModuleTier.Project, DependencyKind.Link));
    }

    // ----- Downward link edges always allowed -----

    [Fact]
    public void Studio_To_Engine_Link_Allowed()
    {
        Assert.True(TierMatrix.IsEdgeAllowed(ModuleTier.Studio, ModuleTier.Engine, DependencyKind.Link));
    }

    [Fact]
    public void Project_To_Engine_Link_Allowed()
    {
        Assert.True(TierMatrix.IsEdgeAllowed(ModuleTier.Project, ModuleTier.Engine, DependencyKind.Link));
    }

    [Fact]
    public void Project_To_Studio_Link_Allowed()
    {
        Assert.True(TierMatrix.IsEdgeAllowed(ModuleTier.Project, ModuleTier.Studio, DependencyKind.Link));
    }

    // ----- Upward link edges always banned -----

    [Fact]
    public void Engine_To_Studio_Link_Banned()
    {
        Assert.False(TierMatrix.IsEdgeAllowed(ModuleTier.Engine, ModuleTier.Studio, DependencyKind.Link));
    }

    [Fact]
    public void Engine_To_Project_Link_Banned()
    {
        Assert.False(TierMatrix.IsEdgeAllowed(ModuleTier.Engine, ModuleTier.Project, DependencyKind.Link));
    }

    [Fact]
    public void Studio_To_Project_Link_Banned()
    {
        Assert.False(TierMatrix.IsEdgeAllowed(ModuleTier.Studio, ModuleTier.Project, DependencyKind.Link));
    }

    // ----- Dynamic edges always allowed (including upward; the audit-finding fix) -----

    [Fact]
    public void Engine_To_Studio_Dynamic_Allowed_AuditFix()
    {
        // The Rev 11 audit-finding fix: Engine modules may dynamically
        // load Studio plugins for hot-reload. Without this rule,
        // hot-reload is impossible.
        Assert.True(TierMatrix.IsEdgeAllowed(ModuleTier.Engine, ModuleTier.Studio, DependencyKind.Dynamic));
    }

    [Fact]
    public void Engine_To_Project_Dynamic_Allowed()
    {
        Assert.True(TierMatrix.IsEdgeAllowed(ModuleTier.Engine, ModuleTier.Project, DependencyKind.Dynamic));
    }

    [Fact]
    public void Studio_To_Project_Dynamic_Allowed()
    {
        Assert.True(TierMatrix.IsEdgeAllowed(ModuleTier.Studio, ModuleTier.Project, DependencyKind.Dynamic));
    }

    [Fact]
    public void Engine_To_Engine_Dynamic_Allowed()
    {
        Assert.True(TierMatrix.IsEdgeAllowed(ModuleTier.Engine, ModuleTier.Engine, DependencyKind.Dynamic));
    }

    [Fact]
    public void Studio_To_Engine_Dynamic_Allowed()
    {
        Assert.True(TierMatrix.IsEdgeAllowed(ModuleTier.Studio, ModuleTier.Engine, DependencyKind.Dynamic));
    }

    [Fact]
    public void Project_To_Engine_Dynamic_Allowed()
    {
        Assert.True(TierMatrix.IsEdgeAllowed(ModuleTier.Project, ModuleTier.Engine, DependencyKind.Dynamic));
    }

    [Fact]
    public void Studio_To_Studio_Dynamic_Allowed()
    {
        Assert.True(TierMatrix.IsEdgeAllowed(ModuleTier.Studio, ModuleTier.Studio, DependencyKind.Dynamic));
    }

    [Fact]
    public void Project_To_Studio_Dynamic_Allowed()
    {
        Assert.True(TierMatrix.IsEdgeAllowed(ModuleTier.Project, ModuleTier.Studio, DependencyKind.Dynamic));
    }

    [Fact]
    public void Project_To_Project_Dynamic_Allowed()
    {
        Assert.True(TierMatrix.IsEdgeAllowed(ModuleTier.Project, ModuleTier.Project, DependencyKind.Dynamic));
    }
}
