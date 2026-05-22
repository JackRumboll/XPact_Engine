// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// TMpscQueue.h -- multi-producer single-consumer lock-free queue.
// =====================================================================
//
// XCore-4a Rev 3, Section 8.1 (Threading Primitives) -- "Vyukov's
// intrusive queue algorithm" + Section 8.1 unbounded variant
// alongside TBoundedMpscQueue.
//
// Per locked decision 6: legacy UE TQueue dropped.
//
// Vyukov's intrusive MPSC algorithm (1024cores.net): producers swap
// the Head pointer atomically with their new node, then link the
// previous Head's Next to the new node with a release store. The
// consumer reads Tail->Next with acquire and shifts forward.
//
// Pattern reference: UE Core Containers/MpscQueue.h:46-56 is the
// same algorithm:
//
//     FNode* New = ::new(...) FNode;
//     FNode* Prev = Head.exchange(New, std::memory_order_acq_rel);
//     Prev->Next.store(New, std::memory_order_release);
//
// XPact's variant is structurally identical; the differences are:
//   1. Allocation routes through `new FNode` for Phase 1c (Phase 1d
//      will swap to FMemory::MallocOrAbort with Threading tag).
//   2. Cache-line padding is explicit via XPACT_CACHE_LINE_SIZE
//      (Section 13.1 fix B-M3).
//   3. We document the algorithm's memory orderings inline so a
//      reader can audit the correctness without chasing the upstream
//      reference.
//
// THREAD SAFETY DISCIPLINE:
//   * Enqueue: called by ANY producer thread (multi-producer).
//   * TryDequeue / IsEmpty: called ONLY by the single consumer
//     thread (single-consumer).
//   * Multiple consumers is UB.
//
// USE CASE: cross-thread work-stealing (multiple workers push tasks
// onto a queue; one consumer drains them) -- but for the allocator-
// reclaim path specifically, use TBoundedMpscQueue (Section 8.1 fix
// B-C4) which has back-pressure semantics.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include <atomic>
#include <new>
#include <utility>

namespace XCore::HAL
{

template<typename T>
class TMpscQueue
{
public:
    // Constructor: allocate the sentinel node (the initial tail).
    //
    // Vyukov's algorithm: Tail starts pointing at a sentinel; Head
    // also points at the sentinel. Producers swap Head with new
    // nodes; the consumer walks from Tail forward via Next.
    TMpscQueue() noexcept
    {
        FNode* Sentinel = AllocateNode();
        Sentinel->Next.store(nullptr, ::std::memory_order_relaxed);
        m_head.store(Sentinel, ::std::memory_order_relaxed);
        m_tail = Sentinel;
    }

    ~TMpscQueue() noexcept
    {
        // Drain remaining elements + free sentinel.
        // m_tail is the current sentinel (its value is uninitialised
        // / already-moved-out); every node reached from m_tail->Next
        // carries a valid value.
        FNode* Current = m_tail;
        bool IsSentinel = true;
        while (Current != nullptr)
        {
            FNode* Next = Current->Next.load(::std::memory_order_relaxed);
            if (!IsSentinel)
            {
                reinterpret_cast<T*>(Current->ValueStorage)->~T();
            }
            DeallocateNode(Current);
            Current = Next;
            IsSentinel = false;
        }
    }

    TMpscQueue(const TMpscQueue&)            = delete;
    TMpscQueue& operator=(const TMpscQueue&) = delete;
    TMpscQueue(TMpscQueue&&)                 = delete;
    TMpscQueue& operator=(TMpscQueue&&)      = delete;

    // -----------------------------------------------------------------
    // Enqueue -- producer-side; append a new element.
    //
    // Multi-producer safe via the Head.exchange CAS. Each producer:
    //   1. Allocates a new node, constructs its value, sets Next to
    //      nullptr.
    //   2. Atomically swaps Head with the new node (acq_rel:
    //      producer's prior writes are released; the previous Head
    //      pointer is acquired so we can link it).
    //   3. Stores the new node into the previous Head's Next with
    //      release (publishes the new node to the consumer).
    //
    // The brief window between (2) and (3) is where the queue is
    // "torn": Head points at the new node, but the consumer might
    // already be walking forward from m_tail and see the previous
    // node's Next still as nullptr. The consumer treats nullptr-
    // Next as "no more items currently visible"; the consumer will
    // re-check on its next Dequeue attempt and see the linked node.
    //
    // The "lost-visibility" interval has NO correctness consequences:
    // every Enqueued element is eventually visible to the consumer
    // (the producer's (3) store-release runs in finite time);
    // dequeue may transiently see a stale "empty" but never sees an
    // incomplete element.
    // -----------------------------------------------------------------
    void Enqueue(T&& Item) noexcept
    {
        FNode* New = AllocateNode();
        ::new (static_cast<void*>(New->ValueStorage)) T(::std::move(Item));
        New->Next.store(nullptr, ::std::memory_order_relaxed);

        // Swap Head with New; the previous Head is returned.
        // acq_rel: release the producer's writes; acquire the
        // previous Head pointer's writes.
        FNode* Prev = m_head.exchange(New, ::std::memory_order_acq_rel);

        // Link the previous Head to the new node. release pairs
        // with the consumer's acquire on Next.
        Prev->Next.store(New, ::std::memory_order_release);
    }

    // Copy overload.
    void Enqueue(const T& Item) noexcept
    {
        FNode* New = AllocateNode();
        ::new (static_cast<void*>(New->ValueStorage)) T(Item);
        New->Next.store(nullptr, ::std::memory_order_relaxed);

        FNode* Prev = m_head.exchange(New, ::std::memory_order_acq_rel);
        Prev->Next.store(New, ::std::memory_order_release);
    }

    // -----------------------------------------------------------------
    // TryDequeue -- consumer-side; remove the front element.
    //
    // PRECONDITION: called only by the single consumer thread.
    //
    // Same sentinel-shift pattern as TSpscQueue. The "lost-
    // visibility" interval in Enqueue means TryDequeue can return
    // false even though a producer is mid-Enqueue; the caller
    // typically retries.
    // -----------------------------------------------------------------
    [[nodiscard]] bool TryDequeue(T& OutItem) noexcept
    {
        FNode* Next = m_tail->Next.load(::std::memory_order_acquire);
        if (Next == nullptr)
        {
            return false;
        }

        T* SourcePtr = reinterpret_cast<T*>(Next->ValueStorage);
        OutItem = ::std::move(*SourcePtr);
        SourcePtr->~T();

        // Free the old sentinel (m_tail); shift m_tail forward.
        DeallocateNode(m_tail);
        m_tail = Next;
        return true;
    }

    // -----------------------------------------------------------------
    // IsEmpty -- consumer-side; check queue emptiness.
    //
    // Returns true if no elements currently visible. Subject to the
    // same "lost-visibility" semantics as TryDequeue.
    // -----------------------------------------------------------------
    [[nodiscard]] bool IsEmpty() const noexcept
    {
        return m_tail->Next.load(::std::memory_order_acquire) == nullptr;
    }

private:
    struct FNode
    {
        ::std::atomic<FNode*> Next;
        alignas(T) ::std::byte ValueStorage[sizeof(T)];
    };

    [[nodiscard]] FNode* AllocateNode() noexcept
    {
        // TODO(Phase 1d): per-instance freelist + FMemory routing.
        return new FNode;
    }

    void DeallocateNode(FNode* Node) noexcept
    {
        delete Node;
    }

    // Cache-line-padded head + tail so the producer's Head writes
    // and the consumer's Tail walks don't false-share.
    alignas(XCore::XPACT_CACHE_LINE_SIZE) ::std::atomic<FNode*> m_head;
    alignas(XCore::XPACT_CACHE_LINE_SIZE) FNode* m_tail;
};

} // namespace XCore::HAL
