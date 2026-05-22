// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FMemory.Tests/ThreadExitDrain.cpp -- TLS thread-exit drain
// correctness (Phase 1g Fix B's MAJOR #2).
// =====================================================================
//
// XCore-4a Rev 3 Section 4.2 + Phase 1g fix:
//
//   "Walk Cache.FreeListHead per bin, route each free block to the
//    central allocator's reclaim queue (the TBoundedMpscQueue swapped
//    in by Phase 1c). Add a test that spawns + exits 100 transient
//    threads each with non-trivial TLS cache populations and verifies
//    no blocks leak."
//
// Strategy:
//   1. Snapshot the allocator's Generic-tag bytes pre-test.
//   2. Spawn 100 worker threads. Each:
//      a. Allocates 32 small blocks of varied sizes (covers several
//         bin indices).
//      b. Frees 16 of them inside the thread (populates the local
//         TLS bin cache).
//      c. Exits while still holding 16 blocks outstanding.
//   3. The main thread frees the 100 * 16 = 1600 outstanding blocks
//      via shared pointers it captured.
//   4. Snapshot Generic-tag bytes post-test. The delta must equal the
//      pre-test value (every byte allocated was freed).
//
// The thread-exit drain runs automatically when each worker's
// thread_local FTLSBinCacheGuard goes out of scope; the drain
// routes the worker's per-bin chains to the allocator's reclaim
// queue rather than leaking.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "HAL/XInitPhase.h"
#include "Macros/XCoreTypes.h"

#include <atomic>
#include <cstdio>
#include <mutex>
#include <thread>
#include <vector>

namespace
{
    // Per-thread allocation cap. Each thread allocates this many small
    // blocks; the bin distribution covers indices 0..7 (32..144 byte
    // sizes) so multiple bins exercise the drain.
    constexpr ::int32 kAllocsPerThread = 32;

    // Number of transient worker threads to spawn.
    constexpr ::int32 kNumWorkers = 100;

    // Block sizes covering a range of bin indices.
    constexpr ::SIZE_T kBlockSizes[] = { 32, 48, 64, 96, 128, 200, 256, 400 };
    constexpr ::SIZE_T kNumSizes = sizeof(kBlockSizes) / sizeof(kBlockSizes[0]);

    // Shared output: the surviving allocations from each worker.
    std::mutex                       g_survivorsMutex;
    std::vector<void*>               g_survivors;

    void WorkerBody(::int32 ThreadIdx)
    {
        std::vector<void*> LocalAllocs;
        LocalAllocs.reserve(kAllocsPerThread);

        // Allocate kAllocsPerThread blocks.
        for (::int32 I = 0; I < kAllocsPerThread; ++I)
        {
            const ::SIZE_T Size = kBlockSizes[I % kNumSizes];
            void* P = ::XCore::HAL::FMemory::Malloc(
                Size, 16, ::XCore::HAL::FMemTag::Generic);
            // Touch the memory so the allocator commits real pages.
            if (P != nullptr)
            {
                *static_cast<volatile ::uint8*>(P) =
                    static_cast<::uint8>(I & 0xFFu);
            }
            LocalAllocs.push_back(P);
        }

        // Free half locally so the thread's TLS bin cache is populated.
        for (::int32 I = 0; I < kAllocsPerThread / 2; ++I)
        {
            ::XCore::HAL::FMemory::Free(LocalAllocs[I]);
        }

        // Hand the surviving half to the shared survivors vector.
        std::lock_guard<std::mutex> Lock(g_survivorsMutex);
        for (::int32 I = kAllocsPerThread / 2; I < kAllocsPerThread; ++I)
        {
            g_survivors.push_back(LocalAllocs[I]);
        }

        // Thread exits -- the FTLSBinCacheGuard destructor runs and
        // CrossThreadFlushOnExit drains the remaining per-bin chains
        // back to the central allocator's reclaim queue. The half
        // we freed locally is NOT leaked: the freed blocks were
        // returned to the per-thread cache; the cache is now drained
        // to the central pool.
        (void)ThreadIdx;
    }

    int RunThreadExitDrain()
    {
        ::XCore::HAL::FMemory::__Init();
        ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);

        const ::uint64 PreBytes =
            ::XCore::HAL::FMemory::GetAllocatedBytes(
                ::XCore::HAL::FMemTag::Generic);

        std::vector<std::thread> Workers;
        Workers.reserve(kNumWorkers);
        for (::int32 T = 0; T < kNumWorkers; ++T)
        {
            Workers.emplace_back(WorkerBody, T);
        }
        for (auto& W : Workers)
        {
            W.join();
        }

        // Workers exited; their thread_local FTLSBinCacheGuard
        // destructors ran. Free the surviving allocations.
        {
            std::lock_guard<std::mutex> Lock(g_survivorsMutex);
            for (void* P : g_survivors)
            {
                ::XCore::HAL::FMemory::Free(P);
            }
            g_survivors.clear();
        }

        const ::uint64 PostBytes =
            ::XCore::HAL::FMemory::GetAllocatedBytes(
                ::XCore::HAL::FMemTag::Generic);

        if (PostBytes != PreBytes)
        {
            std::fprintf(stderr,
                "FAIL: Generic-tag bytes leaked across thread-exit drain. "
                "Pre=%llu Post=%llu delta=%lld.\n",
                static_cast<unsigned long long>(PreBytes),
                static_cast<unsigned long long>(PostBytes),
                static_cast<long long>(PostBytes - PreBytes));
            return 1;
        }

        std::printf("PASS: TLS thread-exit drain over %d workers x %d allocs\n",
                    kNumWorkers, kAllocsPerThread);
        return 0;
    }
}

int main()
{
    return RunThreadExitDrain();
}
