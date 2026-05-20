// Copyright Simgenics. All Rights Reserved.

using Simgenics.XPact.XBT.LiveCoding;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.LiveCoding;

/// <summary>
/// Canary against the Rev 3 ordering fix in
/// <see cref="QuiescePriority"/>: GC marker threads MUST quiesce
/// FIRST (priority 0) so no concurrent mark phase observes a
/// function pointer mid-rewrite during the hot-reload cascade's
/// Phase 2 patch window. Per <c>/Documents/XBT.html</c> Rev 4
/// Section 16.5.
/// </summary>
/// <remarks>
/// Rev 2 listed <c>GCMarkerThreads = 6</c>, which was wrong. Any
/// future "cleanup" of this enum that re-orders the values must
/// fail this test on purpose. The ordering is part of the Phase 2
/// hot-reload cascade contract and is referenced by Contract Rev 11
/// Hot-Reload row.
/// </remarks>
public sealed class QuiescePriorityTests
{
    /// <summary>
    /// GCMarkerThreads is priority 0 (quiesces first). The Rev 3
    /// ordering fix moved it from 6 to 0; this asserts it stays at 0.
    /// </summary>
    [Fact]
    public void GCMarkerThreads_HasOrdinalZero()
    {
        Assert.Equal(0, (int)QuiescePriority.GCMarkerThreads);
    }

    /// <summary>
    /// GameThread is priority 1.
    /// </summary>
    [Fact]
    public void GameThread_HasOrdinalOne()
    {
        Assert.Equal(1, (int)QuiescePriority.GameThread);
    }

    /// <summary>
    /// RenderThread is priority 2.
    /// </summary>
    [Fact]
    public void RenderThread_HasOrdinalTwo()
    {
        Assert.Equal(2, (int)QuiescePriority.RenderThread);
    }

    /// <summary>
    /// RHIThread is priority 3.
    /// </summary>
    [Fact]
    public void RHIThread_HasOrdinalThree()
    {
        Assert.Equal(3, (int)QuiescePriority.RHIThread);
    }

    /// <summary>
    /// AudioThread is priority 4.
    /// </summary>
    [Fact]
    public void AudioThread_HasOrdinalFour()
    {
        Assert.Equal(4, (int)QuiescePriority.AudioThread);
    }

    /// <summary>
    /// LoadingThreads is priority 5.
    /// </summary>
    [Fact]
    public void LoadingThreads_HasOrdinalFive()
    {
        Assert.Equal(5, (int)QuiescePriority.LoadingThreads);
    }

    /// <summary>
    /// SimPathWorkerPool is priority 6 (quiesces last).
    /// </summary>
    [Fact]
    public void SimPathWorkerPool_HasOrdinalSix()
    {
        Assert.Equal(6, (int)QuiescePriority.SimPathWorkerPool);
    }

    /// <summary>
    /// Strict-ascending ordering: every consecutive pair maintains
    /// priority &lt; priority. This is the canonical assertion the
    /// Phase 2 XLiveCoding cascade relies on.
    /// </summary>
    [Fact]
    public void Priorities_AreStrictlyAscendingAndContiguous()
    {
        QuiescePriority[] expected =
        {
            QuiescePriority.GCMarkerThreads,
            QuiescePriority.GameThread,
            QuiescePriority.RenderThread,
            QuiescePriority.RHIThread,
            QuiescePriority.AudioThread,
            QuiescePriority.LoadingThreads,
            QuiescePriority.SimPathWorkerPool,
        };
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(i, (int)expected[i]);
        }
    }
}
