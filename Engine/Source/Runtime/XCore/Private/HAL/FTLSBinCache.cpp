// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FTLSBinCache.cpp -- per-thread small-bin allocation cache body.
// =====================================================================
//
// XCore-4a Rev 3, Section 4.2 fix B-C1 + fix B-C2. See FTLSBinCache.h
// header for the full contract.
//
// This .cpp delivers the lazy-init logic for the TLS-backed
// FTLSBinCache and the thread-exit hook stub. The actual
// pull-bundles-from-central / push-bundles-to-central interactions
// live in FMallocBinnedX.cpp where they share state with the central
// allocator's per-bin locked pool.
//
// Phase 1b note: TModuleSafeThreadLocal<T> (Step 2 Platform HAL) is
// not yet shipped. For Phase 1b we use native thread_local on every
// platform; the .h header's XPACT_TLS_USE_NATIVE_THREAD_LOCAL_PHASE1B
// is the swap point.
//
// =====================================================================

#include "Private/HAL/FTLSBinCache.h"

#include "Macros/XCoreTypes.h"
#include "Macros/XCoreDefines.h"
#include "Macros/XPactMacros.h"

#include <cstring>  // ::std::memset
#include <thread>   // ::std::this_thread::get_id

namespace XCore::HAL
{
    // -----------------------------------------------------------------
    // The native thread_local storage. One instance per thread, lazily
    // initialised via GetThreadCache's first-touch path.
    //
    // The cache is zero-initialised at thread creation by the C++
    // runtime (per the C++20 [basic.start.dynamic] rules); the
    // bInitialized = false sentinel fires the lazy-init path the first
    // time GetThreadCache is called on the thread.
    //
    // The struct is in an anonymous namespace so it has internal
    // linkage and the thread_local symbol does not leak into the
    // module's exported surface. Downstream callers go through
    // GetThreadCache() exclusively.
    // -----------------------------------------------------------------
    namespace
    {
        thread_local FTLSBinCache g_tlsCache = {};

        // -----------------------------------------------------------------
        // GetCurrentThreadIdPhase1b -- platform-neutral thread ID stub.
        //
        // Phase 1b stub: the Platform HAL FPlatformProcess::GetCurrentThreadId
        // is the eventual call; for Phase 1b we route through the C++
        // standard library's std::this_thread::get_id which is portable
        // but heavier than the platform-specific path.
        // TODO(Phase 1b finalization): switch to
        // FPlatformProcess::GetCurrentThreadId once Step 2 Platform HAL
        // is in the build (the call costs a single CPUID-cached fetch
        // on Win64 vs the std::this_thread::get_id route's two function
        // hops plus a thread::id->uint64 conversion).
        // -----------------------------------------------------------------
        [[nodiscard]] ::uint64 GetCurrentThreadIdPhase1b() noexcept
        {
            ::std::hash<::std::thread::id> Hasher;
            const ::SIZE_T H = Hasher(::std::this_thread::get_id());
            return static_cast<::uint64>(H);
        }

        // -----------------------------------------------------------------
        // EnsureInitialized -- the lazy-init body.
        //
        // Runs once per thread on the first GetThreadCache call.
        // Idempotent (subsequent calls see bInitialized = true and
        // skip).
        // -----------------------------------------------------------------
        void EnsureInitialized(FTLSBinCache& Cache) noexcept
        {
            if (XPACT_LIKELY(Cache.bInitialized))
            {
                return;
            }

            // Zero-fill the free lists and counts. The struct is
            // already zero-initialised by the C++ runtime; the
            // memset below is redundant in practice but documents
            // the contract (a future move to a non-thread_local
            // allocation path would need this).
            ::std::memset(Cache.FreeListHead,  0, sizeof(Cache.FreeListHead));
            ::std::memset(Cache.FreeListCount, 0, sizeof(Cache.FreeListCount));

            Cache.bLockedByOwnerThread.store(false, ::std::memory_order_relaxed);
            Cache.OwnerThreadId = GetCurrentThreadIdPhase1b();
            Cache.bInitialized  = true;
        }
    } // anonymous

    // -----------------------------------------------------------------
    // GetThreadCache -- public TLS accessor.
    // -----------------------------------------------------------------
    FTLSBinCache& GetThreadCache() noexcept
    {
        FTLSBinCache& Cache = g_tlsCache;
        EnsureInitialized(Cache);
        return Cache;
    }

    // -----------------------------------------------------------------
    // CrossThreadFlushOnExit -- thread-exit hook.
    //
    // Phase 1b stub: the per-thread exit drain is wired in Phase 1c
    // via FPlatformTLS::RegisterDestructor. For Phase 1b the engine
    // shutdown path (FMallocBinnedX::__Shutdown) handles drain at
    // program exit.
    //
    // The stub still resets bInitialized so a subsequent
    // GetThreadCache call on the same thread (after a hypothetical
    // re-init path) re-runs the lazy-init.
    // -----------------------------------------------------------------
    void CrossThreadFlushOnExit(FTLSBinCache& Cache) noexcept
    {
        // TODO(Phase 1c): walk Cache.FreeListHead per bin, route each
        // free block to the central allocator's reclaim queue.
        // For Phase 1b we mark the cache as un-init so any
        // subsequent same-thread reuse of the slot re-inits cleanly.
        Cache.bInitialized = false;
    }

} // namespace XCore::HAL
