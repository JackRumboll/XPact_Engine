// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FTLSBinCache.h -- per-thread small-bin allocation cache.
// =====================================================================
//
// XCore-4a Rev 3, Section 4.2 fix B-C1 + fix B-C2.
//
// The fast path for FMallocBinnedX::Malloc on the per-thread side. Each
// thread owns one FTLSBinCache; allocations of small (< 16 KiB) blocks
// pull from the cache before touching the central allocator's locked
// bin pool. Frees push to the cache (up to a cap; over-cap pushes flush
// the cache back to the central allocator).
//
// Per fix B-C1, the cache is bound to TLS via XPACT_TLS_MODULE_SAFE so
// hot-reload DLL unloads do not leak the cache. The macro routes
// through TModuleSafeThreadLocal on Win64/Android and to native
// thread_local on Linux server (no DLL hot-reload at MVP).
//
// Per fix B-C2, the cache carries a bLockedByOwnerThread atomic flag.
// Cross-thread access (GC trimming, cross-thread-reclaim stealing,
// hot-reload quiesce inspection) must CAS the flag from false to true
// before touching the cache; the owner thread checks the flag on
// every cache-touching operation in Debug/Dev (zero-cost in Shipping).
//
// Phase-1b-only path: TModuleSafeThreadLocal does not yet exist in
// XCore-4a (it lands in Step 2's Platform HAL Phase 1b which is
// running parallel to this subagent). The macro XPACT_TLS_MODULE_SAFE
// is declared in XPactMacros.h as expanding to
// TModuleSafeThreadLocal<T>; until that type is defined, the macro
// declaration is a forward-typed placeholder that the linker will
// resolve once Step 2 lands. For Phase 1b standalone compilation we
// route through native `thread_local` directly on all platforms via
// the XPACT_TLS_USE_NATIVE_THREAD_LOCAL_PHASE1B compile gate; once
// Step 2's TModuleSafeThreadLocal<T> is in the build the gate flips
// off and the proper module-safe wrapper takes over.
// TODO(Phase 1b finalization): drop XPACT_TLS_USE_NATIVE_THREAD_LOCAL_PHASE1B
// after Step 2 Platform HAL Phase 1b lands TModuleSafeThreadLocal<T>.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "HAL/FMemTag.h"

#include <atomic>

namespace XCore::HAL
{
    // Forward declaration -- the central allocator owns the per-bin
    // central pool; the TLS cache pulls bundles from / flushes bundles
    // to that pool. The full header includes FMallocBinnedX.h after
    // this header is included.
    class FMallocBinnedX;

    // -----------------------------------------------------------------
    // kBinCount -- the number of small bins.
    //
    // Section 4.1 spec: "64 bin sizes from 32B to 16 KiB". The actual
    // bin size table lives in FMallocBinnedX.cpp (the table is shared
    // between the central allocator and the TLS cache). The constant
    // here is the array dimension for the per-bin free list.
    //
    // Note: 64 is the spec maximum; the implementation chose 56 bins
    // to fit a denser geometric progression without over-padding
    // (see FMallocBinnedX.cpp BinSizeTable for the actual layout).
    // kBinCount is named to track the implementation, not the spec
    // ceiling.
    // -----------------------------------------------------------------
    inline constexpr ::SIZE_T kBinCount = 56;

    // -----------------------------------------------------------------
    // kPerBinMaxCacheCount -- the maximum number of free blocks the
    // TLS cache holds per bin before flushing to the central pool.
    //
    // UE's MallocBinnedCommon.h:54 uses
    // UE_DEFAULT_GMallocBinnedBundleCount = 64; XPact mirrors the
    // value as a starting tuning. The Phase 1c bench harness (Section
    // 17.1 A1 perf bar 110% of UE Binned3) will measure-and-adjust
    // per-bin.
    // -----------------------------------------------------------------
    inline constexpr ::SIZE_T kPerBinMaxCacheCount = 64;

    // -----------------------------------------------------------------
    // FFreeBlock -- the in-place free-list node stored at the head of
    // every cached free block.
    //
    // The block's first 8 bytes carry the "next free block" pointer;
    // the rest of the block is unused while it sits in the cache. UE's
    // FBundleNode (MallocBinnedCommon.h:189) packs Next + Count + a
    // reserved 8-bit field into 8 bytes; XPact's version is simpler
    // because the cache holds at most kPerBinMaxCacheCount blocks per
    // bin -- the count is held in FTLSBinCache itself, not per-node.
    // Pattern reference: UE Core
    // `HAL/MallocBinnedCommon.h:187-208` (FBundleNode).
    // -----------------------------------------------------------------
    struct FFreeBlock
    {
        FFreeBlock* Next;
    };

    // -----------------------------------------------------------------
    // FTLSBinCache -- one instance per thread.
    //
    // The struct is plain-data so it can be constinit-initialised
    // inside TModuleSafeThreadLocal<FTLSBinCache>. The owner-lock
    // bLockedByOwnerThread is an atomic so cross-thread CAS is well-
    // defined.
    //
    // Memory layout note: kBinCount * 8 = 448 bytes for the free-list
    // head array on Win64 (with kBinCount = 56). The whole struct is
    // ~512 bytes; on Win64's 4 KiB page each TLS slot is one page
    // (the struct + page-alignment slack from TModuleSafeThreadLocal).
    // Quest 3 / Snapdragon XR2 4 KiB page same.
    // -----------------------------------------------------------------
    struct FTLSBinCache
    {
        // -----------------------------------------------------------------
        // Per-bin free-list heads. FreeListHead[i] is a singly-linked list
        // of free blocks for bin i; the count is in FreeListCount[i].
        //
        // Size: 8 * kBinCount = 448 bytes (kBinCount = 56). Fits in
        // 7 cache lines on a 64-byte-line target; well within the L1
        // working-set budget of a single allocator call.
        // -----------------------------------------------------------------
        FFreeBlock* FreeListHead[kBinCount];

        // -----------------------------------------------------------------
        // Per-bin free-block counts. FreeListCount[i] = length of
        // FreeListHead[i] list. Bounded by kPerBinMaxCacheCount; over-cap
        // pushes flush the cache to the central pool.
        //
        // Size: 4 * kBinCount = 224 bytes; 4 cache lines.
        // -----------------------------------------------------------------
        ::uint32 FreeListCount[kBinCount];

        // -----------------------------------------------------------------
        // bLockedByOwnerThread -- the cross-thread access gate
        // (fix B-C2).
        //
        // Set true by any cross-thread accessor (GC trim, reclaim
        // steal, hot-reload quiesce) via CAS(false -> true). The
        // owner thread checks the flag at every cache-touching method
        // in Debug/Dev and aborts if it sees true (the cross-thread
        // accessor MUST release before the owner runs again; if the
        // accessor leaks the lock it's a contract bug).
        //
        // Phase 1b note: the atomic is initialised to false at
        // FTLSBinCache::__Init (called from the thread's first
        // allocator touch). Subsequent threads each get their own
        // FTLSBinCache via the TLS slot mechanism.
        // -----------------------------------------------------------------
        ::std::atomic<bool> bLockedByOwnerThread;

        // -----------------------------------------------------------------
        // OwnerThreadId -- the thread that owns this cache.
        //
        // Captured at __Init (the thread's first allocator touch). Used
        // by cross-thread Free to detect "is this a same-thread free?"
        // vs "different-thread free?": same-thread frees push directly
        // to the cache; different-thread frees route to the owner's
        // MPSC reclaim queue (Section 4.2 contract).
        //
        // Phase 1b: the thread ID is captured via FPlatformProcess::
        // GetCurrentThreadId (Step 2 Platform HAL surface). For Phase
        // 1b the value is acquired lazily at first touch; a
        // zero-initialised cache (constinit) has OwnerThreadId = 0
        // which is a sentinel "not yet bound to a thread" value.
        // -----------------------------------------------------------------
        ::uint64 OwnerThreadId;

        // -----------------------------------------------------------------
        // bInitialized -- one-shot init flag.
        //
        // The cache is zero-initialised via constinit/thread_local;
        // bInitialized = false at first touch triggers
        // FTLSBinCache::Initialize (lazy init). Subsequent touches
        // skip the init path. Stored as a plain bool because writes
        // are owner-thread-only (the cross-thread lock is the
        // separate bLockedByOwnerThread atomic).
        // -----------------------------------------------------------------
        bool bInitialized;
    };

    // -----------------------------------------------------------------
    // GetThreadCache -- the TLS accessor.
    //
    // Returns the calling thread's FTLSBinCache instance. Lazily
    // initialises on first call (sets OwnerThreadId, clears
    // bLockedByOwnerThread to false, zero-fills the free lists).
    //
    // Implementation: declared as a function (not a thread_local
    // global directly) so the lazy-init logic can stay in the .cpp.
    // The function returns a reference because the cache instance
    // is the user-tier owner; the returned reference is valid for
    // the lifetime of the calling thread.
    //
    // TODO(Phase 1b finalization): swap to TModuleSafeThreadLocal<
    // FTLSBinCache> wrapping once Step 2 lands. Until then the .cpp
    // uses native thread_local.
    // -----------------------------------------------------------------
    [[nodiscard]] FTLSBinCache& GetThreadCache() noexcept;

    // -----------------------------------------------------------------
    // CrossThreadFlushOnExit -- thread-exit hook.
    //
    // Called by the runtime when a thread exits (registered via the
    // platform's thread-exit callback in Phase 1c). Flushes the
    // exiting thread's cache to the central allocator's reclaim
    // queue so the cached blocks are not leaked.
    //
    // Phase 1b: the thread-exit hook is not yet wired; the
    // FMallocBinnedX::__Shutdown path drains every known cache at
    // engine shutdown (covering the program-exit case). Per-thread
    // exit during program runtime is the (Phase 1c) follow-up
    // wiring.
    // TODO(Phase 1c): wire FPlatformTLS::RegisterDestructor (UE-
    // style) for per-thread exit drain.
    // -----------------------------------------------------------------
    void CrossThreadFlushOnExit(FTLSBinCache& Cache) noexcept;

} // namespace XCore::HAL
