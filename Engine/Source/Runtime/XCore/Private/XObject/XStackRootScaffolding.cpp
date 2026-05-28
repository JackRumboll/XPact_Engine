// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XStackRootScaffolding.cpp -- precise stack-root registry (Phase 5.e).
// =====================================================================
//
// XCoreXObject Rev 4 Section 5.3 + Section 5.4. Phase 5.e ships the
// process-global FStackRootMap registry: a TArray-like list of map
// pointers, FRWLock-protected, exposed via Register / Unregister /
// GetCount / ForEach.
//
// The XIL2CPP-emitted code that POPULATES the maps + the GC mark-
// phase walker that CONSUMES them are System 6 / Phase 5.g deliverables
// respectively.
//
// Phase 5.e responsibility:
//   1. The struct shape headers (done -- XStackRootScaffolding.h).
//   2. The registration API stubs (this file).
//   3. The ABI-locked storage (this file).
//   4. The iteration API (template body in-header via the helper
//      free-functions below).
//
// =====================================================================

#include "XObject/XStackRootScaffolding.h"

#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "HAL/FRWLock.h"

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <new>

namespace XCore::Detail
{

    // -----------------------------------------------------------------
    // Process-global registry of active FStackRootMaps.
    //
    // The map pointers are stored in a hand-rolled dynamic array
    // (mirrors XGCRootSpanRegistry's discipline) because pulling
    // Containers/TArray.h into XStackRootScaffolding.h would couple
    // the per-module XIL2CPP-emitted init code to TArray's header
    // (XIL2CPP-emitted code prefers a minimum-include surface).
    //
    // m_maps          -- the active-map pointer table.
    // m_count         -- atomic; live count.
    // m_capacity      -- table allocation capacity.
    // m_lock          -- FRWLock; SHARED for iteration; EXCLUSIVE for
    //                    Register / Unregister / grow.
    // -----------------------------------------------------------------
    namespace
    {
        struct FStackRootRegistry
        {
            const FStackRootMap**           m_maps;
            ::std::atomic<::std::int32_t>   m_count;
            ::int32                         m_capacity;
            mutable ::XCore::HAL::FRWLock   m_lock;

            FStackRootRegistry() noexcept
                : m_maps(nullptr)
                , m_count(0)
                , m_capacity(0)
            {
                constexpr ::int32 kInitialCapacity = 32;
                m_capacity = kInitialCapacity;
                m_maps = static_cast<const FStackRootMap**>(
                    ::XCore::HAL::FMemory::MallocOrAbort(
                        static_cast<::SIZE_T>(m_capacity) * sizeof(const FStackRootMap*),
                        alignof(const FStackRootMap*),
                        ::XCore::HAL::FMemTag::Reflection));
                for (::int32 I = 0; I < m_capacity; ++I)
                {
                    m_maps[I] = nullptr;
                }
            }

            ~FStackRootRegistry() noexcept
            {
                if (m_maps != nullptr)
                {
                    ::XCore::HAL::FMemory::Free(m_maps);
                    m_maps = nullptr;
                }
            }

            // Non-copyable / non-movable.
            FStackRootRegistry(const FStackRootRegistry&)            = delete;
            FStackRootRegistry(FStackRootRegistry&&)                 = delete;
            FStackRootRegistry& operator=(const FStackRootRegistry&) = delete;
            FStackRootRegistry& operator=(FStackRootRegistry&&)      = delete;

            // Pre-condition: caller holds m_lock EXCLUSIVE.
            void EnsureCapacityUnderLock() noexcept
            {
                if (m_count.load(::std::memory_order_relaxed) < m_capacity)
                {
                    return;
                }
                const ::int32 NewCapacity = m_capacity * 2;
                const FStackRootMap** NewMaps =
                    static_cast<const FStackRootMap**>(
                        ::XCore::HAL::FMemory::MallocOrAbort(
                            static_cast<::SIZE_T>(NewCapacity) * sizeof(const FStackRootMap*),
                            alignof(const FStackRootMap*),
                            ::XCore::HAL::FMemTag::Reflection));
                for (::int32 I = 0; I < m_capacity; ++I)
                {
                    NewMaps[I] = m_maps[I];
                }
                for (::int32 I = m_capacity; I < NewCapacity; ++I)
                {
                    NewMaps[I] = nullptr;
                }
                ::XCore::HAL::FMemory::Free(m_maps);
                m_maps     = NewMaps;
                m_capacity = NewCapacity;
            }
        };

        // The function-local-static singleton accessor. Magic-static
        // initialisation is thread-safe (C++11+ [stmt.dcl]/4).
        FStackRootRegistry& GetRegistry() noexcept
        {
            static FStackRootRegistry s_instance;
            return s_instance;
        }
    } // namespace

    // =================================================================
    // RegisterStackRootMap (Phase 5.e stub; XIL2CPP integration at
    // System 6).
    //
    // Appends the map pointer to the registry. EXCLUSIVE lock.
    // Idempotent on a duplicate Map* (the same module re-init at
    // hot-reload time may attempt to re-register; the duplicate-
    // registration is a no-op).
    // =================================================================
    void RegisterStackRootMap(const FStackRootMap* Map) noexcept
    {
        if (Map == nullptr)
        {
            return;
        }
        FStackRootRegistry& R = GetRegistry();
        ::XCore::HAL::FScopedWriteLock WriteLock(R.m_lock);

        const ::int32 CurrentCount = R.m_count.load(::std::memory_order_relaxed);

        // Duplicate-registration check (linear scan; rare path).
        for (::int32 I = 0; I < CurrentCount; ++I)
        {
            if (R.m_maps[I] == Map)
            {
                return;
            }
        }

        R.EnsureCapacityUnderLock();
        R.m_maps[CurrentCount] = Map;
        R.m_count.store(CurrentCount + 1, ::std::memory_order_release);
    }

    // =================================================================
    // UnregisterStackRootMap (Phase 5.e stub).
    //
    // Removes the map pointer from the registry via swap-with-last.
    // EXCLUSIVE lock. Idempotent on a not-registered Map (no-op).
    // =================================================================
    void UnregisterStackRootMap(const FStackRootMap* Map) noexcept
    {
        if (Map == nullptr)
        {
            return;
        }
        FStackRootRegistry& R = GetRegistry();
        ::XCore::HAL::FScopedWriteLock WriteLock(R.m_lock);

        const ::int32 CurrentCount = R.m_count.load(::std::memory_order_relaxed);
        for (::int32 I = 0; I < CurrentCount; ++I)
        {
            if (R.m_maps[I] == Map)
            {
                // Swap-with-last + decrement.
                R.m_maps[I] = R.m_maps[CurrentCount - 1];
                R.m_maps[CurrentCount - 1] = nullptr;
                R.m_count.store(CurrentCount - 1, ::std::memory_order_release);
                return;
            }
        }
    }

    // =================================================================
    // GetRegisteredStackRootMapCount -- diagnostic accessor.
    // =================================================================
    ::std::size_t GetRegisteredStackRootMapCount() noexcept
    {
        const ::int32 N = GetRegistry().m_count.load(::std::memory_order_acquire);
        return (N < 0) ? 0u : static_cast<::std::size_t>(N);
    }

    // =================================================================
    // Internal snapshot helper for the in-header ForEachStackRootMap
    // template.
    //
    // Acquires SHARED lock, copies up to OutCapacity map pointers into
    // OutBuffer, releases the lock, and returns the count actually
    // available (which may exceed OutCapacity -- caller retries with
    // a larger buffer).
    //
    // Snapshotting (rather than holding the lock across the visitor)
    // avoids deadlocks if the visitor accidentally calls Register /
    // Unregister (those would acquire EXCLUSIVE; the iteration's
    // SHARED would block them, leading to thread hang).
    // =================================================================
    namespace
    {
        ::int32 GetMapsSnapshot(
            const FStackRootMap** OutBuffer,
            ::int32               OutCapacity) noexcept
        {
            FStackRootRegistry& R = GetRegistry();
            ::XCore::HAL::FScopedReadLock ReadLock(R.m_lock);
            const ::int32 N = R.m_count.load(::std::memory_order_acquire);
            const ::int32 Copy = (N < OutCapacity) ? N : OutCapacity;
            for (::int32 I = 0; I < Copy; ++I)
            {
                OutBuffer[I] = R.m_maps[I];
            }
            return N;
        }
    }

    // -----------------------------------------------------------------
    // ForEachStackRootMap template specialiser support.
    //
    // The in-header template body cannot easily reach the anonymous-
    // namespace GetMapsSnapshot (which is TU-private). We expose a
    // named helper at namespace XCore::Detail scope that the in-header
    // template calls.
    //
    // Two-buffer pattern (stack first; heap fallback):
    //   1. Snapshot into a 32-slot stack buffer.
    //   2. If the registry has more than 32 maps, retry with a heap
    //      buffer sized to the actual count (FMemory::Malloc).
    //   3. Walk the snapshot calling the visitor.
    // -----------------------------------------------------------------
    ::int32 GetStackRootMapSnapshot(
        const FStackRootMap** OutBuffer,
        ::int32               OutCapacity) noexcept
    {
        return GetMapsSnapshot(OutBuffer, OutCapacity);
    }

    // -----------------------------------------------------------------
    // __ResetForTests -- Phase 5.e test-suite helper.
    //
    // Drops every registered map; resets the count. ONLY used by tests.
    // -----------------------------------------------------------------
    void __ResetStackRootRegistryForTests() noexcept
    {
        FStackRootRegistry& R = GetRegistry();
        ::XCore::HAL::FScopedWriteLock WriteLock(R.m_lock);
        const ::int32 N = R.m_count.load(::std::memory_order_relaxed);
        for (::int32 I = 0; I < N; ++I)
        {
            R.m_maps[I] = nullptr;
        }
        R.m_count.store(0, ::std::memory_order_release);
    }

} // namespace XCore::Detail
