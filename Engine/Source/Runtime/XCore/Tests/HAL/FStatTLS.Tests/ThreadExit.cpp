// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStatTLS.Tests/ThreadExit.cpp -- fix C-7 thread-exit handoff.
// =====================================================================
//
// XCore-4a Rev 3, Section 10.5 fix C-7 ("Thread-exit shard handoff"):
//
//   "When a thread exits, its FStatShard destructor migrates any
//    remaining un-merged counts to a static FStatExitOverflowQueue (a
//    Treiber stack at file scope). The destructor runs BEFORE the
//    thread_local table itself is torn down. The next
//    FStatRegistry::MergeFromAllThreads pops the overflow queue and
//    folds it into the global tree before walking live threads. Under
//    100-thread spawn/exit churn the invariant holds: every XSTAT_INC
//    issued before thread exit is observable in the next frame's
//    merge total."
//
// =====================================================================

#include "HAL/FStatTLS.h"
#include "HAL/FStatRegistry.h"
#include "Macros/XCoreTypes.h"

#include <iostream>
#include <thread>
#include <vector>

namespace
{
    XSTAT_DECL(ThreadExitStat, ChurnGroup);

    constexpr int kThreads = 100;
    constexpr int kIncsPerThread = 1000;

    void ThreadBody()
    {
        for (int i = 0; i < kIncsPerThread; ++i)
        {
            XSTAT_INC(ThreadExitStat);
        }
        // Thread exits here; FStatShard destructor runs and migrates
        // the count to the exit-overflow Treiber stack.
    }

    int RunThreadExit()
    {
        // Drain any pre-existing pending counts so the baseline is
        // clean.
        ::XCore::Stat::FStatRegistry::MergeFromAllThreads();
        ::XCore::Stat::FStatSnapshot Pre;
        ::XCore::Stat::FStatRegistry::Snapshot(Pre);
        const ::int64 PreCount = Pre.Lookup(ThreadExitStat_StatId.Hash);

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
        // All worker threads have exited; each one's shard destructor
        // has pushed remaining counts to the exit-overflow Treiber
        // stack.

        // Now merge -- the merger pops the queue and adds the counts
        // to the global tree.
        ::XCore::Stat::FStatRegistry::MergeFromAllThreads();
        ::XCore::Stat::FStatSnapshot Post;
        ::XCore::Stat::FStatRegistry::Snapshot(Post);
        const ::int64 PostCount = Post.Lookup(ThreadExitStat_StatId.Hash);

        const ::int64 ExpectedDelta = static_cast<::int64>(kThreads) * kIncsPerThread;
        const ::int64 ActualDelta = PostCount - PreCount;
        if (ActualDelta != ExpectedDelta)
        {
            std::cerr << "FAIL: thread-exit count loss; expected delta=" << ExpectedDelta
                      << " actual=" << ActualDelta
                      << " (pre=" << PreCount << " post=" << PostCount << ")\n";
            return 1;
        }

        std::cout << "PASS: ThreadExit (100 threads * 1000 INCs => "
                  << ExpectedDelta << " observed; no count loss)\n";
        return 0;
    }
} // namespace

int main()
{
    return RunThreadExit();
}
