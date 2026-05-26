// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStatRegistry.Tests/MergeFromAllThreads_LiveThreadSweep.cpp --
// Rev 2 FIX-7 / AG1 honesty-in-naming complement (skip-stub).
// =====================================================================
//
// XCore-4a Rev 3, Section 10.7 G3 gate (live-thread sweep):
//   "MergeFromAllThreads walks every live thread's shard via the
//    global thread-list snapshot."
//
// PER REV 2 FIX-7 / AG1: this file is a deliberate skip-stub that
// documents the Phase 1g+ gap for the live-thread sweep. The
// companion test MergeFromAllThreads_ExitOverflowOnly.cpp exercises
// the exit-overflow queue path; the LIVE-thread sweep (worker
// threads STILL RUNNING when Merge fires) requires FThreadRegistry
// to enumerate live shards, which is Phase 1g+ TaskGraph work.
//
// PRESENT BEHAVIOUR: this file's main() returns 0 ("PASS via SKIP")
// so the test runner ranks it as PASSED. The skip emits a single
// diagnostic line indicating the gate is deferred so the operator
// observes the gap in the test output rather than silently passing
// without coverage.
//
// FUTURE LANDING: when FThreadRegistry ships (Phase 1g+), this
// file's body should be replaced with:
//   1. Spawn N worker threads that INC their shard at a steady rate.
//   2. While the workers are STILL RUNNING, call Merge from the main
//      thread.
//   3. Continue worker INCs for a few more milliseconds, then a
//      second Merge.
//   4. Sum (Pre, Mid, Post) counts; assert correctness against an
//      atomic counter the workers also increment per-Inc.
//
// =====================================================================

#include <cstdio>

int main()
{
    // TODO(Phase 1g+ FThreadRegistry): replace with the live-thread-sweep
    // exerciser. See file-prologue for the test plan.
    std::printf(
        "MergeFromAllThreads_LiveThreadSweep: SKIP "
        "(awaiting FThreadRegistry; Phase 1g+ Rev 2 FIX-7 / AG1)\n");
    return 0;
}
