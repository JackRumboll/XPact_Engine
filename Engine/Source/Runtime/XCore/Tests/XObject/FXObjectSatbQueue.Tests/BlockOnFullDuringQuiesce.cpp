// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectSatbQueue.Tests/BlockOnFullDuringQuiesce.cpp -- writer
// blocks when queue full + drains paused (XCoreXObject Rev 4 §5.6 +
// Rev 2 FIX-A-MED-37 / MAJOR-A30).
// =====================================================================
//
// Per spec §5.6: "During hot-reload quiesce, the per-thread SATB
// queues continue to receive entries from write-barriers (256-entry
// circular buffer per thread). The global drain is paused. If a
// per-thread queue fills, the writer thread blocks until quiesce ends.
// This is bounded by §11 acceptance criterion (e) cascade hot-patch
// timeout (<120 s nominal / <150 s kill)."
//
// Verifies:
//   * g_XGCAcceptDrains == false + queue full -> writer blocks.
//   * g_XGCAcceptDrains restored to true -> writer unblocks + drain
//     happens.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "HAL/FPlatformProcess.h"
#include "XObject/FXObjectSatbQueue.h"
#include "XObject/XGCConcurrentState.h"
#include "XObject/XObject.h"

#include <atomic>
#include <chrono>
#include <cstdint>
#include <iostream>
#include <thread>

namespace
{
    int g_FailureCount = 0;

    void Check(bool Condition, const char* Diagnostic)
    {
        if (!Condition)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++g_FailureCount;
        }
    }
}

int main()
{
    using ::XCore::FXObjectGlobalSatbLog;
    using ::XCore::FXObjectSatbQueue;
    using ::XCore::g_XGCAcceptDrains;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    // Ensure clean state.
    g_XGCAcceptDrains.store(true, ::std::memory_order_release);
    FXObjectGlobalSatbLog::Get().__ResetForTests();

    XObject Sentinel;

    // -----------------------------------------------------------------
    // Stage 1: Fill the queue to capacity WHILE drains are accepted.
    // The 256 entries land in the per-thread queue.
    // -----------------------------------------------------------------
    FXObjectSatbQueue Queue;
    for (::std::size_t I = 0; I < FXObjectSatbQueue::kCapacity; ++I)
    {
        Queue.Push(&Sentinel);
    }
    Check(Queue.IsFull(), "Pre-quiesce: queue is not full after kCapacity pushes");

    // -----------------------------------------------------------------
    // Stage 2: Pause drains (simulate hot-reload quiesce). The writer
    // thread will attempt one more Push; this must block because the
    // queue is at capacity AND drains are paused.
    // -----------------------------------------------------------------
    g_XGCAcceptDrains.store(false, ::std::memory_order_release);

    std::atomic<bool> WriterPushed{false};
    std::atomic<bool> WriterStarted{false};

    std::thread Writer([&]() {
        WriterStarted.store(true, ::std::memory_order_release);
        // This Push should observe IsFull() + g_XGCAcceptDrains==false
        // and block on XGCWaitForDrainsAccepted until we re-enable
        // drains in Stage 3.
        Queue.Push(&Sentinel);
        WriterPushed.store(true, ::std::memory_order_release);
    });

    // Wait for the writer thread to actually start.
    while (!WriterStarted.load(::std::memory_order_acquire))
    {
        std::this_thread::yield();
    }

    // Sleep briefly to give the writer time to enter the block. If
    // the block is honoured, WriterPushed remains false.
    ::XCore::HAL::FPlatformProcess::Sleep(0.05f);

    Check(!WriterPushed.load(::std::memory_order_acquire),
          "Writer did not block on full queue + paused drains "
          "(WriterPushed observed true mid-quiesce)");

    // -----------------------------------------------------------------
    // Stage 3: Re-enable drains. The writer thread should unblock,
    // drain the queue into the global log, and complete its Push.
    // -----------------------------------------------------------------
    g_XGCAcceptDrains.store(true, ::std::memory_order_release);

    // Wait for the writer to finish (bounded by 5 seconds; if it
    // doesn't unblock within that window the test fails).
    const auto Deadline = std::chrono::steady_clock::now()
        + std::chrono::seconds(5);
    while (!WriterPushed.load(::std::memory_order_acquire)
           && std::chrono::steady_clock::now() < Deadline)
    {
        ::XCore::HAL::FPlatformProcess::Sleep(0.01f);
    }

    Check(WriterPushed.load(::std::memory_order_acquire),
          "Writer did not unblock after drains resumed (5 s timeout)");

    Writer.join();

    // Post-resume: queue should have just the writer's 1 new entry.
    Check(Queue.Size() == 1,
          "Post-quiesce queue Size != 1 (expected: drain emptied + "
          "writer's new entry remains)");

    // Global SATB log should have the prior 256 entries.
    Check(FXObjectGlobalSatbLog::Get().Size() == FXObjectSatbQueue::kCapacity,
          "Global SATB log did not receive 256 drained entries");

    FXObjectGlobalSatbLog::Get().__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectSatbQueue.BlockOnFullDuringQuiesce: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectSatbQueue.BlockOnFullDuringQuiesce: "
              << g_FailureCount << " FAIL(s)\n";
    return 1;
}
