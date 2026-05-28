// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XCoreDelegates_OnClassReplaced.cpp -- delegate body for the Phase
// 5.j hot-reload class-replacement notification (XCoreXObject Rev 4
// §10.10.1).
// =====================================================================
//
// Implements the FOnClassReplacedDelegate's Subscribe / Unsubscribe /
// Broadcast surface declared in
// Public/XObject/XCoreDelegates_OnClassReplaced.h.
//
// CONCURRENCY DESIGN:
//
//   * m_handleCounter -- atomic uint64; one fetch_add per Subscribe.
//     No lock required for handle issuance.
//
//   * m_callbacks (TArray<FEntry>) -- protected by m_lock
//     (FCriticalSection). Mutations (Subscribe, Unsubscribe) happen
//     under exclusive ownership; Broadcast takes a SNAPSHOT under the
//     lock then releases before invoking callbacks (so callbacks can
//     re-enter Subscribe / Unsubscribe without deadlock).
//
// CORRECTNESS UNDER RE-ENTRY:
//
// The Broadcast snapshot is a COPY of m_callbacks at the moment the
// lock is held. A callback that calls Subscribe DOES register a new
// entry in m_callbacks, but that entry is NOT in the snapshot; the
// current Broadcast invocation does not fire the just-registered
// callback. The next Broadcast call will. This matches UE's
// FDelegate semantics and is the principled behavior (an immediately-
// firing Subscribe would create an unbounded recursion hazard).
//
// A callback that calls Unsubscribe on its own handle (the typical
// "one-shot" pattern) removes the entry from m_callbacks, but the
// snapshot still holds the entry for the duration of the loop; the
// callback completes normally. Subsequent Broadcasts skip the entry.
//
// =====================================================================

#include "XObject/XCoreDelegates_OnClassReplaced.h"

#include <atomic>

namespace XCore::CoreDelegates
{

    // =================================================================
    // GetOnClassReplaced -- magic-static singleton accessor.
    //
    // Function-local-static guarantees thread-safe initialisation
    // per C++11 [stmt.dcl]/3. The delegate is default-constructed at
    // first call (no allocations at construction; m_callbacks's TArray
    // starts empty).
    // =================================================================
    FOnClassReplacedDelegate& GetOnClassReplaced() noexcept
    {
        static FOnClassReplacedDelegate s_instance;
        return s_instance;
    }

    // =================================================================
    // Subscribe -- register a callback. Returns the handle.
    // =================================================================
    FOnClassReplacedDelegate::FHandle
    FOnClassReplacedDelegate::Subscribe(FOnClassReplacedCallback Callback) noexcept
    {
        // Handle issuance is lock-free (atomic fetch_add).
        const FHandle Handle =
            m_handleCounter.fetch_add(1, ::std::memory_order_relaxed);

        // The exclusive lock guards the TArray growth + append.
        {
            ::XCore::HAL::FScopedLock Lock(m_lock);
            FEntry Entry;
            Entry.Handle   = Handle;
            Entry.Callback = ::std::move(Callback);
            m_callbacks.Add(::std::move(Entry));
        }

        return Handle;
    }

    // =================================================================
    // Unsubscribe -- remove a callback by handle.
    // =================================================================
    bool FOnClassReplacedDelegate::Unsubscribe(FHandle Handle) noexcept
    {
        if (Handle == kInvalidHandle)
        {
            return false;
        }

        ::XCore::HAL::FScopedLock Lock(m_lock);

        const ::int32 Count = m_callbacks.Num();
        for (::int32 i = 0; i < Count; ++i)
        {
            if (m_callbacks[i].Handle == Handle)
            {
                // Swap-remove for O(1) removal. Order does not matter
                // for the multicast contract (subscribers receive the
                // broadcast unordered).
                m_callbacks.RemoveAtSwap(i);
                return true;
            }
        }
        return false;
    }

    // =================================================================
    // Broadcast -- invoke every subscribed callback synchronously.
    //
    // SNAPSHOT-THEN-RELEASE: take a copy of the callback list under
    // the lock then release before invoking. This makes Broadcast
    // re-entrant safe (a callback may call Subscribe / Unsubscribe).
    // =================================================================
    void FOnClassReplacedDelegate::Broadcast(
        const ::XCore::Reflect::FClass* OldClass,
        const ::XCore::Reflect::FClass* NewClass) noexcept
    {
        // Snapshot the callbacks under lock.
        ::XCore::TArray<FEntry> Snapshot;
        {
            ::XCore::HAL::FScopedLock Lock(m_lock);
            const ::int32 Count = m_callbacks.Num();
            Snapshot.Reserve(Count);
            for (::int32 i = 0; i < Count; ++i)
            {
                Snapshot.Add(m_callbacks[i]);
            }
        }

        // Invoke each callback. Lock released by this point.
        const ::int32 SnapCount = Snapshot.Num();
        for (::int32 i = 0; i < SnapCount; ++i)
        {
            const auto& Entry = Snapshot[i];
            if (Entry.Callback)
            {
                Entry.Callback(OldClass, NewClass);
            }
        }
    }

    // =================================================================
    // GetSubscriberCount -- diagnostic.
    // =================================================================
    ::int32 FOnClassReplacedDelegate::GetSubscriberCount() const noexcept
    {
        ::XCore::HAL::FScopedLock Lock(m_lock);
        return m_callbacks.Num();
    }

    // =================================================================
    // __ResetForTests -- destructive reset.
    // =================================================================
    void FOnClassReplacedDelegate::__ResetForTests() noexcept
    {
        ::XCore::HAL::FScopedLock Lock(m_lock);
        m_callbacks.Reset();
        // m_handleCounter intentionally NOT reset: handles must remain
        // unique across test cases for any external code that retains
        // a handle past a __ResetForTests boundary (the test harness
        // should not, but defence-in-depth).
    }

} // namespace XCore::CoreDelegates
