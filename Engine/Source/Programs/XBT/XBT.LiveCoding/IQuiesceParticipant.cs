// Copyright Simgenics. All Rights Reserved.

using System.Threading;
using System.Threading.Tasks;

namespace Simgenics.XPact.XBT.LiveCoding;

/// <summary>
/// Per <c>/Documents/XBT.html</c> Rev 4 Section 16.5: an object that
/// must be quiesced during Phase 2 of the hot-reload cascade (the
/// function-pointer write window). The fixed priority order ensures
/// GC marker threads quiesce FIRST so concurrent marks don't observe
/// torn pointers (the Rev 3 ordering fix).
/// </summary>
/// <remarks>
/// <para>
/// Phase 1's list of registered participants is <strong>empty</strong>.
/// Phase 2 XLiveCoding registers participants for concurrent GC marker
/// threads, game thread, render thread, RHI thread, audio thread,
/// loading threads, and the sim-path worker pool, in this priority
/// order.
/// </para>
/// </remarks>
public interface IQuiesceParticipant
{
    /// <summary>Order this participant quiesces in the cascade.
    /// Lower numbers quiesce first; the canonical order is locked
    /// in <see cref="QuiescePriority"/>.</summary>
    QuiescePriority Priority { get; }

    /// <summary>Human-readable name for diagnostic output.</summary>
    string Name { get; }

    /// <summary>
    /// Park this participant at a safe point. Blocks until parked
    /// or cancellation.
    /// </summary>
    ValueTask QuiesceAsync(CancellationToken token);

    /// <summary>
    /// Resume from the parked state. The cascade calls Resume in
    /// <em>reverse</em> priority order (the participant that quiesced
    /// first resumes last) so the runtime is fully re-attached to
    /// patched code before any GC marker re-walks it.
    /// </summary>
    ValueTask ResumeAsync();
}

/// <summary>
/// Per <c>/Documents/XBT.html</c> Rev 4 Section 16.5 (REVISED Rev 3 --
/// GC markers quiesce FIRST). Locks the canonical priority ordering
/// for every Phase 2 XLiveCoding hot-reload cascade. Two participants
/// at the same priority quiesce in unspecified order; XLiveCoding
/// registers exactly one participant per priority slot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why GC markers first.</b> Rev 2 listed
/// <c>GCMarkerThreads = 6</c> (highest numeric priority = quiesces
/// <em>last</em>). That was wrong: the hot-reload cascade requires GC
/// markers to drain <strong>before</strong> any function-pointer
/// write, otherwise a concurrent mark phase can observe a pointer
/// mid-rewrite and follow a torn value (Contract Rev 11 Hot-Reload
/// row exactly states this). Rev 3 moves GC markers to
/// <c>QuiescePriority = 0</c> (highest priority = quiesces
/// <em>first</em>) and renumbers the remaining threads accordingly.
/// </para>
/// <para>
/// <b>Canary against regression.</b>
/// <c>XBT.Tests.Tests.LiveCoding.QuiescePriorityTests</c> asserts the
/// exact ordinal of every value. Any future "cleanup" that re-orders
/// the enum fails the test on purpose -- the ordering is part of the
/// Phase 2 cascade contract.
/// </para>
/// </remarks>
public enum QuiescePriority
{
    /// <summary>(Rev 3 fix) quiesce FIRST -- before any function-pointer write.</summary>
    GCMarkerThreads = 0,

    /// <summary>Game thread (logic + dispatch).</summary>
    GameThread = 1,

    /// <summary>Render thread (commands enqueued to the RHI).</summary>
    RenderThread = 2,

    /// <summary>RHI thread (actual GPU work submission).</summary>
    RHIThread = 3,

    /// <summary>Audio thread.</summary>
    AudioThread = 4,

    /// <summary>Background loading threads (asset streaming, BGTaskGraph).</summary>
    LoadingThreads = 5,

    /// <summary>Sim-path worker pool (FixedClockHz lockstep workers).</summary>
    SimPathWorkerPool = 6,
}
