// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStatRegistry.Tests/MergeFromAllThreads_ExitOverflowOnly.cpp --
// 8-thread exit-overflow queue merge test (Rev 2 FIX-7 / AG1).
// =====================================================================
//
// XCore-4a Rev 3, Section 10.7 acceptance row 1:
//   "1 billion Inc from 8 threads then Merge; total == 8 billion."
//
// Scaled down for the unit-test budget: 8 threads * 100K = 800K total.
//
// REV 2 FIX-7 / AG1 -- TEST HONESTY-IN-NAMING.
//
//   This file was renamed from MergeFromAllThreads.cpp to
//   MergeFromAllThreads_ExitOverflowOnly.cpp to honestly reflect the
//   path actually exercised. The previous name implied a full
//   live-thread sweep + exit-overflow merge, but the test joins every
//   worker thread BEFORE calling Merge -- which means the merge
//   exercises ONLY the exit-overflow queue path. The live-thread
//   sweep (G3 gate) is documented as Phase 1g+ work pending
//   FThreadRegistry.
//
//   WHAT THIS TEST EXERCISES:
//     * Worker threads INC their per-thread shards while running.
//     * Worker threads exit; each pushes its remaining count to a
//       file-static FStatExitOverflowQueue (Treiber stack).
//     * Main thread calls MergeFromAllThreads, which pops the exit-
//       overflow queue and folds the accumulated counts into the
//       global tree.
//     * Test asserts the post-merge global count == expected delta.
//
//   WHAT THIS TEST DOES NOT EXERCISE:
//     * Live-thread shard sweep during a Merge call (where the worker
//       threads are STILL RUNNING when Merge fires). That path
//       requires FThreadRegistry to enumerate live thread shards;
//       FThreadRegistry is the Phase 1g+ TaskGraph deliverable.
//
//   A complementary test exercising the live-thread sweep will land
//   when FThreadRegistry ships. See
//   MergeFromAllThreads_LiveThreadSweep.cpp for the placeholder skip-
//   stub (TODO marker for Phase 1g+ FThreadRegistry).
//
// PHASE 1F LIMITATION (preserved here for clarity): the Merge in
// Phase 1f sweeps only the calling thread's shard at Merge call time
// + the exit-overflow queue. The 8-thread sweep below works because
// the spawned threads all exit before the main thread calls Merge;
// their counts are pulled from the exit-overflow queue.
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
