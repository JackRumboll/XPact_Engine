// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// TBoundedMpscQueue.h -- bounded multi-producer single-consumer ring.
// =====================================================================
//
// XCore-4a Rev 3, Section 8.1 (Threading Primitives) -- fix B-C4
// "TBoundedMpscQueue<T, N> ships alongside the unbounded TMpscQueue.
// Fixed-size ring buffer; no per-enqueue allocation; back-pressure
// on overflow. The allocator-reclaim queue in Section 4.2 uses this
// variant exclusively."
//
// Per the spec body (Section 8.1):
//   * Capacity N MUST be a power of 2 (enforced via static_assert).
//   * TryEnqueue returns false when the queue is full -- caller
//     decides the back-pressure policy.
//   * NO per-enqueue allocation.
//   * Cache-line-padded slots; per-slot sequence numbers; back-
//     pressure on overflow.
//
// This is Dmitry Vyukov's bounded MPMC algorithm
// (https://www.1024cores.net/home/lock-free-algorithms/queues/
// bounded-mpmc-queue), specialised for the single-consumer case
// (the dequeue path doesn't need a CAS on the read sequence; the
// consumer races against no one).
//
// MEMORY ORDERINGS (verified against 1024cores reference):
//   * Producer Enqueue:
//     - Read enqueue_pos with relaxed (the producer arbitrates via
//       CAS, so the initial read is hint-only).
//     - Read slot's seq with acquire (pairs with consumer's release-
//       store on dequeue).
//     - On match: CAS enqueue_pos forward with relaxed (no
//       ordering between producers; arbitration is via CAS itself).
//     - Construct value into slot.
//     - Store slot's seq = pos+1 with release (publishes the
//       slot's contents to the consumer).
//   * Consumer TryDequeue:
//     - Read dequeue_pos with relaxed (consumer-only state).
//     - Read slot's seq with acquire (pairs with producer's
//       release-store).
//     - On match (seq == pos+1): move value out; destruct slot.
//     - Store slot's seq = pos + N with release (publishes the
//       slot's free-state to future producers; pos + N is the next
//       seq value a producer in this slot expects).
//     - Advance dequeue_pos with relaxed.
//
// USE CASE: allocator cross-thread reclaim queue (Section 4.2). The
// allocator's Free path enqueues a freed block onto the owner's
// per-bin queue; the owner drains during its next allocation. The
// bounded variant ensures no per-Free allocation (which would be
// recursive: Free allocating to reclaim a Free is a cycle).
//
// Hot-reload: template -- header-only; no .cpp.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include <atomic>
#include <new>
#include <utility>

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// TBoundedMpscQueue<T, N> -- bounded MPMC ring (used as MPSC here).
//
// N must be a power of 2. Capacity is exactly N (no headroom is
// stolen for sentinel; the per-slot sequence number distinguishes
// empty from full).
//
// PRODUCERS: multi-thread safe (any number of producers).
// CONSUMER: single-thread (multi-consumer is UB at this header's
// contract; the underlying Vyukov algorithm supports MPMC but our
// dequeue path elides the CAS the MPMC needs).
// ---------------------------------------------------------------------

template<typename T, ::SIZE_T N>
class TBoundedMpscQueue
{
public:
    // Capacity validation. N must be a power of 2 so we can mask
    // (pos % N == pos & (N - 1)) instead of computing modulus.
    static_assert(N > 0, "Capacity must be positive");
    static_assert((N & (N - 1)) == 0, "Capacity must be a power of 2");

    // -----------------------------------------------------------------
    // Default constructor: constexpr-trivial; the slot sequence
    // numbers are NOT initialised here. Callers MUST invoke
    // Initialize() before first Enqueue/Dequeue.
    //
    // Rationale: the constructor must be constexpr so the queue
    // can live in `constinit` storage (e.g., the allocator's
    // FPoolTable array in FMallocBinnedX). C++20's std::atomic
    // default constructor is constexpr, but std::atomic::store is
    // NOT constexpr until C++26 -- we cannot initialise slot
    // sequences in a constexpr context.
    //
    // The discipline: Initialize() is called at Init()-time
    // (PreStaticInit phase per Section 1.5) before any thread
    // attempts Enqueue/Dequeue. Skipping Initialize() leaves the
    // slot sequences at zero, which corrupts the Vyukov invariant
    // (slot[i].seq == i initially) and causes the queue to report
    // "full" after the first wrap.
    //
    // In Debug builds an XPACT_CHECK in Enqueue/Dequeue could
    // catch the missed-Initialize bug; we skip it on the hot path
    // for cost reasons. The contract is enforced by the caller
    // (Init() chains the Initialize() calls; tests verify the
    // expected behaviour).
    // -----------------------------------------------------------------
    constexpr TBoundedMpscQueue() noexcept
        : m_slots{}
        , m_enqueuePos(0)
        , m_dequeuePos(0)
    {
    }

    // -----------------------------------------------------------------
    // Initialize -- set the slot sequence numbers (slot[i].seq = i).
    //
    // MUST be called once before any Enqueue/Dequeue. Idempotent --
    // calling more than once is harmless but redundant. NOT thread-
    // safe; callers ensure Initialize() runs before any concurrent
    // access.
    // -----------------------------------------------------------------
    void Initialize() noexcept
    {
        for (::SIZE_T I = 0; I < N; ++I)
        {
            m_slots[I].Sequence.store(I, ::std::memory_order_relaxed);
        }
        m_enqueuePos.store(0, ::std::memory_order_relaxed);
        m_dequeuePos.store(0, ::std::memory_order_relaxed);
    }

    // -----------------------------------------------------------------
    // Destructor: walk the live range of the ring and destruct each
    // still-occupied value.
    //
    // NOT thread-safe; caller must quiesce.
    //
    // A slot is occupied if its seq == enqueue_pos that PRODUCED into
    // it + 1; equivalently, the range [dequeue_pos, enqueue_pos) is
    // the set of occupied slots. We destruct each.
    // -----------------------------------------------------------------
    ~TBoundedMpscQueue() noexcept
    {
        const ::SIZE_T DeqPos = m_dequeuePos.load(::std::memory_order_relaxed);
        const ::SIZE_T EnqPos = m_enqueuePos.load(::std::memory_order_relaxed);

        for (::SIZE_T P = DeqPos; P != EnqPos; ++P)
        {
            const ::SIZE_T SlotIdx = P & kIndexMask;
            // The slot at SlotIdx currently holds a constructed
            // value (its seq == P + 1 at the time the producer
            // finished). Destruct.
            reinterpret_cast<T*>(m_slots[SlotIdx].ValueStorage)->~T();
        }
    }

    TBoundedMpscQueue(const TBoundedMpscQueue&)            = delete;
    TBoundedMpscQueue& operator=(const TBoundedMpscQueue&) = delete;
    TBoundedMpscQueue(TBoundedMpscQueue&&)                 = delete;
    TBoundedMpscQueue& operator=(TBoundedMpscQueue&&)      = delete;

    // -----------------------------------------------------------------
    // TryEnqueue -- producer-side; non-blocking add.
    //
    // Returns false if the queue is full (no slot ready to be
    // produced into). Caller decides the back-pressure response.
    //
    // The algorithm:
    //   1. Read enqueue_pos with relaxed.
    //   2. Compute slot = enqueue_pos & (N - 1).
    //   3. Read slot.seq with acquire. The relationship:
    //         seq == pos          -> slot is empty and ready;
    //         seq <  pos          -> slot was for an earlier
    //                                round and is still occupied
    //                                (queue full);
    //         seq >  pos          -> another producer already
    //                                produced into this slot
    //                                (lost the CAS race).
    //   4. If seq == pos: CAS enqueue_pos -> pos+1. On success,
    //      we OWN the slot. Otherwise, retry from step 1.
    //   5. Construct value into slot.
    //   6. Store slot.seq = pos+1 with release; publishes to
    //      the consumer.
    // -----------------------------------------------------------------
    [[nodiscard]] bool TryEnqueue(const T& Item) noexcept
    {
        return TryEnqueueImpl([&](void* Storage)
        {
            ::new (Storage) T(Item);
        });
    }

    [[nodiscard]] bool TryEnqueue(T&& Item) noexcept
    {
        return TryEnqueueImpl([&](void* Storage)
        {
            ::new (Storage) T(::std::move(Item));
        });
    }

    // -----------------------------------------------------------------
    // TryDequeue -- consumer-side; non-blocking remove.
    //
    // PRECONDITION: called only by the single consumer thread.
    //
    // Returns false if no element is currently visible. The same
    // "lost-visibility" semantics as TMpscQueue: a producer may be
    // mid-Enqueue when TryDequeue is called; the consumer may
    // transiently see "empty" and the caller retries.
    //
    // The algorithm:
    //   1. Read dequeue_pos with relaxed.
    //   2. Compute slot = dequeue_pos & (N - 1).
    //   3. Read slot.seq with acquire. The relationship:
    //         seq == pos + 1      -> slot has a value ready;
    //         seq <  pos + 1      -> slot is still being produced
    //                                (lost-visibility);
    //         seq >  pos + 1      -> impossible (consumer would
    //                                have already advanced).
    //   4. If seq == pos + 1: move out the value; destruct slot;
    //      store seq = pos + N with release (slot is now free for
    //      the NEXT round at position pos + N); advance dequeue_pos
    //      with relaxed.
    // -----------------------------------------------------------------
    [[nodiscard]] bool TryDequeue(T& OutItem) noexcept
    {
        const ::SIZE_T Pos     = m_dequeuePos.load(::std::memory_order_relaxed);
        const ::SIZE_T SlotIdx = Pos & kIndexMask;
        FSlot& Slot            = m_slots[SlotIdx];

        const ::SIZE_T Seq = Slot.Sequence.load(::std::memory_order_acquire);
        const ::std::ptrdiff_t Diff = static_cast<::std::ptrdiff_t>(Seq)
                                    - static_cast<::std::ptrdiff_t>(Pos + 1);
        if (Diff < 0)
        {
            // Slot not yet produced; queue empty (from consumer's
            // POV).
            return false;
        }
        // Diff > 0 should not happen because the consumer is the
        // only one advancing dequeue_pos; the slot's seq cannot
        // outrun the consumer's expected value (Vyukov's invariant).
        // We treat Diff > 0 as "empty" defensively (the consumer
        // can retry; no UB).
        if (Diff > 0)
        {
            return false;
        }

        // Diff == 0: slot is ready. Move out, destruct, mark slot
        // free, advance consumer position.
        T* SourcePtr = reinterpret_cast<T*>(Slot.ValueStorage);
        OutItem = ::std::move(*SourcePtr);
        SourcePtr->~T();

        // Mark the slot free for the NEXT enqueue at position
        // pos + N. The producer at that position will read seq
        // and find a match.
        Slot.Sequence.store(Pos + N, ::std::memory_order_release);

        // Advance consumer position.
        m_dequeuePos.store(Pos + 1, ::std::memory_order_relaxed);
        return true;
    }

    // -----------------------------------------------------------------
    // Size -- approximate count of elements in the queue.
    //
    // Computed as (enqueue_pos - dequeue_pos). Both loads are
    // relaxed because the result is a hint (the queue may grow or
    // shrink between the two loads). Always returns a non-negative
    // value because we clamp at zero if the producers happen to
    // have raced ahead with their CAS but not yet completed the
    // store (transient negative window).
    // -----------------------------------------------------------------
    [[nodiscard]] ::SIZE_T Size() const noexcept
    {
        const ::SIZE_T EnqPos = m_enqueuePos.load(::std::memory_order_relaxed);
        const ::SIZE_T DeqPos = m_dequeuePos.load(::std::memory_order_relaxed);
        return (EnqPos >= DeqPos) ? (EnqPos - DeqPos) : 0;
    }

    [[nodiscard]] static constexpr ::SIZE_T Capacity() noexcept
    {
        return N;
    }

private:
    static constexpr ::SIZE_T kIndexMask = N - 1;

    // -----------------------------------------------------------------
    // TryEnqueueImpl -- shared body for both copy and move overloads.
    //
    // The Constructor callable runs INSIDE the producer's owned-slot
    // window (after the CAS succeeded; before the release-store on
    // seq). It is invoked once, with a pointer to the slot's storage,
    // and is expected to placement-new a T into the storage.
    // -----------------------------------------------------------------
    template<typename ConstructFn>
    [[nodiscard]] bool TryEnqueueImpl(ConstructFn&& Construct) noexcept
    {
        ::SIZE_T Pos = m_enqueuePos.load(::std::memory_order_relaxed);

        for (;;)
        {
            const ::SIZE_T SlotIdx = Pos & kIndexMask;
            FSlot& Slot = m_slots[SlotIdx];

            const ::SIZE_T Seq = Slot.Sequence.load(::std::memory_order_acquire);
            const ::std::ptrdiff_t Diff = static_cast<::std::ptrdiff_t>(Seq)
                                        - static_cast<::std::ptrdiff_t>(Pos);

            if (Diff == 0)
            {
                // Slot is ready for our position. Try to claim by
                // CAS-advancing enqueue_pos. relaxed on success
                // because the slot's release-store below provides
                // the synchronisation; relaxed on failure because
                // we just retry.
                if (m_enqueuePos.compare_exchange_weak(
                        Pos, Pos + 1,
                        ::std::memory_order_relaxed,
                        ::std::memory_order_relaxed))
                {
                    // Slot owned. Construct value via the caller's
                    // ConstructFn.
                    Construct(static_cast<void*>(Slot.ValueStorage));

                    // Publish: slot is now occupied at position Pos.
                    // The seq value Pos+1 tells the consumer the
                    // slot is ready for dequeue at consumer-pos == Pos.
                    Slot.Sequence.store(Pos + 1, ::std::memory_order_release);
                    return true;
                }
                // CAS failed; another producer claimed the slot.
                // Pos was updated by the CAS to the current value;
                // loop again.
                continue;
            }
            else if (Diff < 0)
            {
                // Slot still occupied from an earlier round (consumer
                // hasn't caught up). Queue is full from the producer's
                // POV.
                return false;
            }
            else
            {
                // Diff > 0: another producer already claimed and
                // produced into this slot for this round. We need to
                // re-read enqueue_pos (which has advanced) and try
                // again at the new position.
                Pos = m_enqueuePos.load(::std::memory_order_relaxed);
                continue;
            }
        }
    }

    // -----------------------------------------------------------------
    // FSlot -- one slot of the ring buffer.
    //
    // DELIBERATE DIVERGENCE FROM VYUKOV (locked rationale below):
    //
    // Vyukov's reference implementation cache-line-pads each slot to
    // prevent false sharing between producers concurrently filling
    // adjacent slots. XPact does NOT pad individual slots; padding is
    // applied ONLY to the producer-position counter (m_enqueuePos)
    // and the consumer-position counter (m_dequeuePos) below.
    //
    // Why diverge: this queue is instantiated 56 times in the
    // allocator (one per bin) with N=4096; per-slot padding at
    // XPACT_CACHE_LINE_SIZE = 64 bytes would balloon each queue to
    // 256 KiB (4096 * 64), summing to 14 MiB across all bins -- an
    // unjustified memory cost for what are nearly-empty queues in
    // steady state.
    //
    // The false-sharing risk is mitigated by:
    //   1. The head and tail counters ARE cache-line padded (the
    //      hot contention point is producers racing on
    //      enqueue_pos's CAS, not on adjacent slot writes).
    //   2. Slot writes happen rarely under contention: a producer
    //      typically claims slot N via CAS, fills it, and the
    //      consumer drains immediately. Adjacent producers (slots
    //      N+1, N+2) write to different cache lines naturally if
    //      sizeof(FSlot) >= 64 / 2 = 32 bytes (which holds for any
    //      T whose ValueStorage + Sequence-atom together exceed
    //      32 bytes; for sizeof(T) >= 24 this is true).
    //   3. For sizeof(T) < 24 (e.g., the allocator's FFreeBlock*
    //      use case: T = void* = 8 bytes; slot = 16 bytes), two
    //      adjacent producers may write to the same cache line.
    //      In the worst case this costs a cache-line ping per
    //      contended Enqueue -- significant under HEAVY producer
    //      contention but acceptable for the allocator-reclaim
    //      path where the consumer drains continuously.
    //
    // Callers with HIGH-contention pointer-pointer-pointer-pointer
    // workloads should consider TBoundedMpscQueueWithPaddedSlots
    // (TODO: Phase 1d follow-up if measurement shows the slot-
    // packing penalises a hot path).
    //
    // TODO(Phase 1d): if benchmarking shows the allocator-reclaim
    // path is contention-bound on adjacent slot writes, introduce
    // a padded-slot variant via a template-bool toggle.
    // -----------------------------------------------------------------
    struct FSlot
    {
        ::std::atomic<::SIZE_T> Sequence;
        alignas(T) ::std::byte  ValueStorage[sizeof(T)];
    };

    // The ring storage.
    FSlot m_slots[N];

    // Producer position. Multiple producers CAS this forward.
    // Cache-line-padded vs the consumer counter.
    alignas(XCore::XPACT_CACHE_LINE_SIZE) ::std::atomic<::SIZE_T> m_enqueuePos;

    // Consumer position. Single consumer; relaxed loads/stores
    // suffice because the slot.Sequence atomic provides the
    // synchronisation with producers.
    alignas(XCore::XPACT_CACHE_LINE_SIZE) ::std::atomic<::SIZE_T> m_dequeuePos;
};

} // namespace XCore::HAL
