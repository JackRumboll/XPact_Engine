// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXGrayQueue.Tests/OverflowSpill.cpp -- TLS overflow to global list
// (Phase 5.g).
// =====================================================================
//
// Verifies:
//   * Pushing more than kCapacity entries triggers spill to the global
//     overflow list.
//   * Subsequent Pop() refills from the global list when TLS is empty.
//   * Total Push/Pop count balances (every Push'ed pointer is Pop'd
//     exactly once, possibly across spill/refill boundaries).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXGrayQueue.h"
#include "XObject/XObject.h"

#include <cstddef>
#include <iostream>
#include <set>
#include <vector>

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
    using ::XCore::FXGrayQueue;
    using ::XCore::FXGrayOverflowList;
    using ::XCore::GetThreadGrayQueue;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    FXGrayOverflowList::Get().__ResetForTests();
    FXGrayQueue& Queue = GetThreadGrayQueue();
    Queue.ResetForCycle();

    // -----------------------------------------------------------------
    // Allocate kCapacity + 50 XObjects so the queue spills once.
    // -----------------------------------------------------------------
    constexpr ::std::size_t kPushCount = FXGrayQueue::kCapacity + 50;
    std::vector<XObject> Objects(kPushCount);

    // Push all.
    for (::std::size_t I = 0; I < kPushCount; ++I)
    {
        Queue.Push(&Objects[I]);
    }

    // The TLS queue should now have 50 entries (the +50 over capacity)
    // and the global overflow list should hold the spilled 256.
    Check(Queue.Size() == 50,
          "Post-overflow TLS size != 50 (the post-spill entries)");
    Check(!FXGrayOverflowList::Get().IsEmpty(),
          "Post-overflow global overflow list is empty (spill failed?)");

    // -----------------------------------------------------------------
    // Pop everything. The pointer set we Pop'd should be exactly the
    // set we Push'd, with no duplicates and no losses.
    // -----------------------------------------------------------------
    std::set<XObject*> Seen;
    ::std::size_t PopCount = 0;
    for (;;)
    {
        XObject* const Object = Queue.Pop();
        if (Object == nullptr)
        {
            break;
        }
        Seen.insert(Object);
        ++PopCount;
    }

    Check(PopCount == kPushCount,
          "Pop count != Push count (entries lost across spill/refill)");
    Check(Seen.size() == kPushCount,
          "Pop set size != Push count (duplicate Pops or missing entries)");

    // Verify every Push'd address appears in Seen.
    for (::std::size_t I = 0; I < kPushCount; ++I)
    {
        if (Seen.count(&Objects[I]) == 0)
        {
            ++g_FailureCount;
            std::cerr << "FAIL: Push'd object at index " << I
                      << " was not Pop'd back\n";
        }
    }

    Queue.ResetForCycle();
    FXGrayOverflowList::Get().__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXGrayQueue.OverflowSpill: PASS\n";
        return 0;
    }
    std::cerr << "FXGrayQueue.OverflowSpill: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
