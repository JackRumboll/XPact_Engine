// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// TSpscQueue.h -- single-producer single-consumer lock-free queue.
// =====================================================================
//
// XCore-4a Rev 3, Section 8.1 (Threading Primitives) -- "Lamport's
// bounded ring with relaxed loads on the consumer side." Plus the
// node-based intrusive variant per Section 8.1 (which is what we
// ship for unbounded growth).
//
// Per locked decision 6 (Section 1.3 row 6): legacy UE TQueue is
// dropped; XCore-4a ships only TSpscQueue and TMpscQueue. UE's
// TQueue (Containers/Queue.h:10) is deprecated as of UE 5.x; we
// don't inherit deprecated-from-day-one types.
//
// Pattern reference: UE Core Containers/MpscQueue.h:13-148 is UE's
// MPSC implementation; the SPSC variant is similar but with simpler
// memory orderings (the producer's exchange becomes a plain store-
// release; the consumer doesn't need a CAS because there's only one
// consumer).
//
// USE CASE: render-thread -> game-thread "command queue" where only
// the render thread produces and only the game thread consumes.
//
// THREAD SAFETY DISCIPLINE:
//   * Enqueue: called ONLY by the producer thread.
//   * TryDequeue / IsEmpty: called ONLY by the consumer thread.
//   * Mixing producer/consumer threads is UB.
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
class TSpscQueue
{
public:
    // Constructor: allocate the sentinel head node.
    //
    // The sentinel carries no value (its ValueStorage is
    // uninitialised; only its Next pointer is meaningful). Both
    // m_head and m_tail start pointing at the sentinel; the queue
    // is empty when sentinel->Next == nullptr.
    TSpscQueue() noexcept
    {
        FNode* Sentinel = AllocateNode();
        Sentinel->Next.store(nullptr, ::std::memory_order_relaxed);
        m_head = Sentinel;
        m_tail = Sentinel;
    }

    // Destructor: drain the queue and free all nodes.
    //
    // NOT thread-safe; the queue must be quiesced before destruction.
    // m_head is always a sentinel (its Value is uninitialised);
    // every subsequent node carries a valid Value that must be
    // destructed before the node is freed.
    ~TSpscQueue() noexcept
    {
        FNode* Current = m_head;
        bool IsSentinel = true;  // first iteration is the sentinel
        while (Current != nullptr)
        {
            FNode* Next = Current->Next.load(::std::memory_order_relaxed);
            if (!IsSentinel)
            {
                // Real node: destruct the value in storage.
                reinterpret_cast<T*>(Current->ValueStorage)->~T();
            }
            DeallocateNode(Current);
            Current = Next;
            IsSentinel = false;
        }
    }

    TSpscQueue(const TSpscQueue&)            = delete;
    TSpscQueue& operator=(const TSpscQueue&) = delete;
    TSpscQueue(TSpscQueue&&)                 = delete;
    TSpscQueue& operator=(TSpscQueue&&)      = delete;

    // -----------------------------------------------------------------
    // Enqueue -- producer-side; append a new element.
    //
    // PRECONDITION: called only by the producer thread.
    // -----------------------------------------------------------------
    void Enqueue(T&& Item) noexcept
    {
        FNode* New = AllocateNode();
        ::new (static_cast<void*>(New->ValueStorage)) T(::std::move(Item));
        New->Next.store(nullptr, ::std::memory_order_relaxed);

        // Publish to consumer. release-store on Next pairs with the
        // consumer's acquire-load.
        m_tail->Next.store(New, ::std::memory_order_release);
        m_tail = New;  // producer-only state
    }

    // Copy-enqueue overload.
    void Enqueue(const T& Item) noexcept
    {
        FNode* New = AllocateNode();
        ::new (static_cast<void*>(New->ValueStorage)) T(Item);
        New->Next.store(nullptr, ::std::memory_order_relaxed);

        m_tail->Next.store(New, ::std::memory_order_release);
        m_tail = New;
    }

    // -----------------------------------------------------------------
    // TryDequeue -- consumer-side; remove the front element.
    //
    // PRECONDITION: called only by the consumer thread.
    //
    // Returns true if an element was dequeued (moved into OutItem),
    // false if the queue was empty.
    //
    // The "sentinel shift" pattern: m_head is always a sentinel
    // whose ValueStorage is meaningless. Dequeue reads m_head->Next;
    // if non-null, moves the next node's Value into OutItem, frees
    // the old sentinel, and shifts m_head to the next node (which
    // becomes the new sentinel; its Value just got moved out and
    // destructed).
    // -----------------------------------------------------------------
    [[nodiscard]] bool TryDequeue(T& OutItem) noexcept
    {
        FNode* Next = m_head->Next.load(::std::memory_order_acquire);
        if (Next == nullptr)
        {
            return false;
        }

        // Move out the value; destruct the source slot.
        T* SourcePtr = reinterpret_cast<T*>(Next->ValueStorage);
        OutItem = ::std::move(*SourcePtr);
        SourcePtr->~T();

        // Free the old sentinel; shift m_head forward to Next (which
        // becomes the new sentinel).
        DeallocateNode(m_head);
        m_head = Next;
        return true;
    }

    // -----------------------------------------------------------------
    // IsEmpty -- consumer-side; check queue emptiness.
    //
    // The return is a hint: an empty queue may have become non-empty
    // by the time the caller reads the result.
    // -----------------------------------------------------------------
    [[nodiscard]] bool IsEmpty() const noexcept
    {
        return m_head->Next.load(::std::memory_order_acquire) == nullptr;
    }

private:
    struct FNode
    {
        ::std::atomic<FNode*> Next;
        // Aligned storage for T; populated only on non-sentinel nodes.
        alignas(T) ::std::byte ValueStorage[sizeof(T)];
    };

    // ---- node alloc / dealloc -- TODO(Phase 1d): per-instance freelist
    [[nodiscard]] FNode* AllocateNode() noexcept
    {
        // Phase 1c: direct heap allocation via operator new. The
        // node is small (16-32 bytes typically); future revisions
        // route through FMemory::MallocOrAbort with a Threading tag.
        //
        // TODO(Phase 1d): swap to a per-instance freelist (a
        // Treiber stack of recently-freed nodes) so steady-state
        // Enqueue/Dequeue does not touch the global allocator.
        return new FNode;
    }

    void DeallocateNode(FNode* Node) noexcept
    {
        delete Node;
    }

    // Cache-line padding to prevent false sharing between the
    // producer's tail-write and the consumer's head-read.
    alignas(XCore::XPACT_CACHE_LINE_SIZE) FNode* m_head;
    alignas(XCore::XPACT_CACHE_LINE_SIZE) FNode* m_tail;
};

} // namespace XCore::HAL
