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
#include "Private/HAL/FMallocBinnedX.h"   // FMallocBinnedX::__ThreadExitFlushBundle (Phase 1g)

#include "HAL/FMemory.h"                  // FMemory::IsAlive guard for post-shutdown drain
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
    // Thread-exit drain (Phase 1g Fix B's MAJOR #2):
    //
    // The C++20 thread_local destruction guarantee runs the wrapper
    // class's destructor when the owning thread exits. We wrap the
    // FTLSBinCache in FTLSBinCacheGuard whose ~ctor calls
    // CrossThreadFlushOnExit on the embedded cache. The wrapper has
    // zero-cost storage (one bool of padding on top of FTLSBinCache).
    //
    // The struct is in an anonymous namespace so it has internal
    // linkage and the thread_local symbol does not leak into the
    // module's exported surface. Downstream callers go through
    // GetThreadCache() exclusively.
    // -----------------------------------------------------------------
    namespace
    {
        struct FTLSBinCacheGuard
        {
            FTLSBinCache Cache;
            // No explicit ctor: zero-initialise the cache.
            // The destructor routes through CrossThreadFlushOnExit so
            // exiting threads drain their per-bin caches to the
            // allocator's reclaim queue before the TLS storage is
            // torn down.
            ~FTLSBinCacheGuard() noexcept
            {
                CrossThreadFlushOnExit(Cache);
            }
        };

        thread_local FTLSBinCacheGuard g_tlsCacheGuard = {};
        // Backward-compat alias for the existing `g_tlsCache` references;
        // a reference to the guard's Cache field reads identically.
        XPACT_FORCEINLINE FTLSBinCache& AccessTlsCache() noexcept
        {
            return g_tlsCacheGuard.Cache;
        }

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
        FTLSBinCache& Cache = AccessTlsCache();
        EnsureInitialized(Cache);
        return Cache;
    }

    // -----------------------------------------------------------------
    // CrossThreadFlushOnExit -- thread-exit hook (Phase 1g Fix B's
    // MAJOR #2).
    //
    // Walks Cache.FreeListHead[bin] for every bin and routes each
    // FreeBlock to the central allocator's per-bin reclaim queue via
    // FMallocBinnedX::__ThreadExitFlushBundle. After the walk the
    // per-bin head pointers + counts are reset to zero and the cache
    // is marked un-initialised so a subsequent same-thread reuse of
    // the slot re-inits cleanly.
    //
    // Threading: this runs on the EXITING thread, so the cache's
    // owner is the caller; no cross-thread CAS on the cache itself.
    // The route-target queue (FPoolTable::CrossThreadReclaimQueue) is
    // MPSC-safe so the per-block TryEnqueue is correct.
    //
    // No allocations happen on this path; the FreeBlock storage is
    // already owned by the allocator (just being returned).
    // -----------------------------------------------------------------
    void CrossThreadFlushOnExit(FTLSBinCache& Cache) noexcept
    {
        if (!Cache.bInitialized)
        {
            // Never touched on this thread: nothing to flush.
            return;
        }

        // Post-shutdown guard: if FMemory has already torn down (e.g.,
        // a thread is exiting AFTER FMemory::__Shutdown drained the
        // pools), routing through the central allocator would touch
        // freed state. Silently drop the chains; the engine is shutting
        // down and the blocks will be reclaimed by the process exit.
        if (!::XCore::HAL::FMemory::IsAlive())
        {
            Cache.bInitialized = false;
            return;
        }

        for (::SIZE_T BinIdx = 0; BinIdx < kBinCount; ++BinIdx)
        {
            FFreeBlock* Head  = Cache.FreeListHead[BinIdx];
            const ::uint32 N  = Cache.FreeListCount[BinIdx];
            if (Head == nullptr || N == 0)
            {
                continue;
            }

            // Hand the bin's chain to the central allocator. The
            // allocator takes ownership (the blocks land in either
            // the bounded MPSC queue or the Treiber-stack fallback).
            g_Allocator.__ThreadExitFlushBundle(
                static_cast<::uint32>(BinIdx), Head, N);

            // Reset the per-bin head + count; the cache is being
            // torn down but defense-in-depth covers a re-init path
            // observing stale pointers.
            Cache.FreeListHead[BinIdx]  = nullptr;
            Cache.FreeListCount[BinIdx] = 0;
        }

        // Mark un-init so any subsequent same-thread reuse of the slot
        // re-inits cleanly. The TLS slot itself is freed by the OS
        // destructor when this hook completes.
        Cache.bInitialized = false;
    }

} // namespace XCore::HAL
