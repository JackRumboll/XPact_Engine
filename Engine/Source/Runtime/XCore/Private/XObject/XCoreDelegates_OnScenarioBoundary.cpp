// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XCoreDelegates_OnScenarioBoundary.cpp -- delegate body for the
// Phase 5.i scenario-boundary notification
// (XCoreXObject Rev 4 §3.6 + §4.7).
// =====================================================================
//
// Implements the FOnScenarioBoundaryDelegate's Subscribe / Unsubscribe
// / Broadcast surface declared in
// Public/XObject/XCoreDelegates_OnScenarioBoundary.h.
//
// The structural shape is a 1:1 mirror of
// XCoreDelegates_OnClassReplaced.cpp -- same lock discipline, same
// snapshot-then-release Broadcast pattern, same monotonic-uint64
// handle issuance. The only difference is the payload type
// (FScenarioBoundaryContext vs the OldClass/NewClass pair).
//
// =====================================================================

#include "XObject/XCoreDelegates_OnScenarioBoundary.h"

#include <atomic>

namespace XCore::CoreDelegates
{

    // =================================================================
    // GetOnScenarioBoundary -- magic-static singleton accessor.
    //
    // Function-local-static guarantees thread-safe initialisation per
    // C++11 [stmt.dcl]/3. The delegate is default-constructed at
    // first call (no allocations at construction; m_callbacks's TArray
    // starts empty).
    // =================================================================
    FOnScenarioBoundaryDelegate& GetOnScenarioBoundary() noexcept
    {
        static FOnScenarioBoundaryDelegate s_instance;
        return s_instance;
    }

    // =================================================================
    // Subscribe -- register a callback. Returns the handle.
    // =================================================================
    FOnScenarioBoundaryDelegate::FHandle
    FOnScenarioBoundaryDelegate::Subscribe(FOnScenarioBoundaryCallback Callback) noexcept
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
    bool FOnScenarioBoundaryDelegate::Unsubscribe(FHandle Handle) noexcept
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
                // Swap-remove for O(1) removal.
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
    void FOnScenarioBoundaryDelegate::Broadcast(
        const FScenarioBoundaryContext& Ctx) noexcept
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
                Entry.Callback(Ctx);
            }
        }
    }

    // =================================================================
    // GetSubscriberCount -- diagnostic.
    // =================================================================
    ::int32 FOnScenarioBoundaryDelegate::GetSubscriberCount() const noexcept
    {
        ::XCore::HAL::FScopedLock Lock(m_lock);
        return m_callbacks.Num();
    }

    // =================================================================
    // __ResetForTests -- destructive reset.
    // =================================================================
    void FOnScenarioBoundaryDelegate::__ResetForTests() noexcept
    {
        ::XCore::HAL::FScopedLock Lock(m_lock);
        m_callbacks.Reset();
        // m_handleCounter intentionally NOT reset (matches OnClassReplaced
        // posture: handles must remain unique across test cases for any
        // external code that retains a handle past __ResetForTests).
    }

} // namespace XCore::CoreDelegates
