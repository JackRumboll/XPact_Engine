// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XCoreDelegates_OnHotReload.cpp -- delegate bodies for the hot-reload
// cascade-bracket lifecycle (XCoreXObject Rev 4 §9.2; Phase 5.j).
// =====================================================================
//
// Implements FOnHotReloadDelegate's Subscribe / Unsubscribe / Broadcast
// surface declared in Public/XObject/XCoreDelegates_OnHotReload.h, plus
// the three process-singleton accessors (GetOnHotReloadStart /
// Complete / Abort).
//
// The body MIRRORS XCoreDelegates_OnClassReplaced.cpp (Phase 5.k) at
// the dispatch level: snapshot-then-release Broadcast for re-entry
// safety; atomic monotonic handle counter for lock-free Subscribe
// handle issuance; swap-remove for O(1) Unsubscribe.
//
// =====================================================================

#include "XObject/XCoreDelegates_OnHotReload.h"

#include <atomic>

namespace XCore::CoreDelegates
{

    // =================================================================
    // GetOnHotReloadStart -- magic-static singleton accessor.
    //
    // Function-local-static guarantees thread-safe initialisation per
    // C++11 [stmt.dcl]/3. The delegate is default-constructed at first
    // call (no allocations at construction; m_callbacks's TArray starts
    // empty).
    // =================================================================
    FOnHotReloadDelegate& GetOnHotReloadStart() noexcept
    {
        static FOnHotReloadDelegate s_instance;
        return s_instance;
    }

    // =================================================================
    // GetOnHotReloadComplete -- magic-static singleton accessor.
    // =================================================================
    FOnHotReloadDelegate& GetOnHotReloadComplete() noexcept
    {
        static FOnHotReloadDelegate s_instance;
        return s_instance;
    }

    // =================================================================
    // GetOnHotReloadAbort -- magic-static singleton accessor.
    // =================================================================
    FOnHotReloadDelegate& GetOnHotReloadAbort() noexcept
    {
        static FOnHotReloadDelegate s_instance;
        return s_instance;
    }

    // =================================================================
    // Subscribe -- register a callback. Returns the handle.
    //
    // The handle issuance is lock-free (atomic fetch_add); the
    // TArray growth + append is under exclusive lock.
    // =================================================================
    FOnHotReloadDelegate::FHandle
    FOnHotReloadDelegate::Subscribe(FOnHotReloadCallback Callback) noexcept
    {
        const FHandle Handle =
            m_handleCounter.fetch_add(1, ::std::memory_order_relaxed);

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
    bool FOnHotReloadDelegate::Unsubscribe(FHandle Handle) noexcept
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
    void FOnHotReloadDelegate::Broadcast(const FHotReloadContext& Context) noexcept
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
                Entry.Callback(Context);
            }
        }
    }

    // =================================================================
    // GetSubscriberCount -- diagnostic.
    // =================================================================
    ::int32 FOnHotReloadDelegate::GetSubscriberCount() const noexcept
    {
        ::XCore::HAL::FScopedLock Lock(m_lock);
        return m_callbacks.Num();
    }

    // =================================================================
    // __ResetForTests -- destructive reset.
    // =================================================================
    void FOnHotReloadDelegate::__ResetForTests() noexcept
    {
        ::XCore::HAL::FScopedLock Lock(m_lock);
        m_callbacks.Reset();
        // m_handleCounter intentionally NOT reset: handles must remain
        // unique across test cases for any external code that retains
        // a handle past a __ResetForTests boundary (matches Phase 5.k
        // FOnClassReplacedDelegate posture).
    }

} // namespace XCore::CoreDelegates
