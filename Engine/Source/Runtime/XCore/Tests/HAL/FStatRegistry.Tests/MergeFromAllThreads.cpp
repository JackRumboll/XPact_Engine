// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStatRegistry.Tests/MergeFromAllThreads.cpp -- 8 thread merge.
// =====================================================================
//
// XCore-4a Rev 3, Section 10.7 acceptance row 1:
//   "1 billion Inc from 8 threads then Merge; total == 8 billion."
//
// Scaled down for the unit-test budget: 8 threads * 100K = 800K total.
//
// PHASE 1F LIMITATION: the Merge in Phase 1f sweeps only the calling
// thread's shard at Merge call time + the exit-overflow queue (which
// captures every joined thread's pre-exit count). The 8-thread sweep
// works because the spawned threads all exit before the main thread
// calls Merge; their counts are pulled from the exit-overflow queue.
//
// Phase 1g wires FThreadRegistry so Merge can also sweep live thread
// shards (currently the main thread's shard plus the exit queue).
//
// =====================================================================

#include "HAL/FStatTLS.h"
#include "HAL/FStatRegistry.h"

#include <iostream>
#include <thread>
#include <vector>

namespace
{
    XSTAT_DECL(MergeStat, BigGroup);

    constexpr int kThreads = 8;
    constexpr int kIncsPerThread = 100000;

    void ThreadBody()
    {
        for (int i = 0; i < kIncsPerThread; ++i)
        {
            XSTAT_INC(MergeStat);
        }
    }

    int RunMerge()
    {
        // Baseline.
        ::XCore::Stat::FStatRegistry::MergeFromAllThreads();
        ::XCore::Stat::FStatSnapshot Pre;
        ::XCore::Stat::FStatRegistry::Snapshot(Pre);
        const ::int64 PreCount = Pre.Lookup(MergeStat_StatId.Hash);

        std::vector<std::thread> Threads;
        Threads.reserve(kThreads);
        for (int t = 0; t < kThreads; ++t)
        {
            Threads.emplace_back(ThreadBody);
        }
        for (auto& T : Threads)
        {
            T.join();
        }

        // All 8 threads have exited; each pushed its remaining count
        // onto the exit-overflow queue.
        ::XCore::Stat::FStatRegistry::MergeFromAllThreads();
        ::XCore::Stat::FStatSnapshot Post;
        ::XCore::Stat::FStatRegistry::Snapshot(Post);
        const ::int64 PostCount = Post.Lookup(MergeStat_StatId.Hash);

        const ::int64 ExpectedDelta = static_cast<::int64>(kThreads) * kIncsPerThread;
        const ::int64 ActualDelta = PostCount - PreCount;

        if (ActualDelta != ExpectedDelta)
        {
            std::cerr << "FAIL: 8-thread merge mismatch; expected delta=" << ExpectedDelta
                      << " actual=" << ActualDelta << "\n";
            return 1;
        }

        std::cout << "PASS: MergeFromAllThreads (8 threads * 100K = "
                  << ExpectedDelta << " observed)\n";
        return 0;
    }
} // namespace

int main()
{
    return RunMerge();
}
