// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXGrayQueue.Tests/PushPopRoundTrip.cpp -- basic LIFO Push/Pop
// (Phase 5.g).
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXGrayQueue.h"
#include "XObject/XObject.h"

#include <iostream>

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
    // Empty-queue baseline.
    // -----------------------------------------------------------------
    Check(Queue.IsEmpty(), "Initial queue not empty");
    Check(Queue.Pop() == nullptr, "Initial Pop did not return nullptr");
    Check(Queue.Size() == 0, "Initial Size != 0");

    // -----------------------------------------------------------------
    // Push three; Pop in LIFO order.
    // -----------------------------------------------------------------
    XObject Obj1, Obj2, Obj3;

    Queue.Push(&Obj1);
    Queue.Push(&Obj2);
    Queue.Push(&Obj3);

    Check(Queue.Size() == 3, "Size != 3 after three Push");
    Check(!Queue.IsEmpty(), "IsEmpty returned true with 3 entries");

    Check(Queue.Pop() == &Obj3, "First Pop != Obj3 (LIFO violated)");
    Check(Queue.Pop() == &Obj2, "Second Pop != Obj2 (LIFO violated)");
    Check(Queue.Pop() == &Obj1, "Third Pop != Obj1 (LIFO violated)");
    Check(Queue.Pop() == nullptr, "Fourth Pop != nullptr (queue should be empty)");

    // -----------------------------------------------------------------
    // Defensive: Push(nullptr) is a no-op.
    // -----------------------------------------------------------------
    Queue.Push(nullptr);
    Check(Queue.IsEmpty(), "Push(nullptr) added an entry");
    Check(Queue.Size() == 0, "Push(nullptr) bumped size");

    Queue.ResetForCycle();
    FXGrayOverflowList::Get().__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXGrayQueue.PushPopRoundTrip: PASS\n";
        return 0;
    }
    std::cerr << "FXGrayQueue.PushPopRoundTrip: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
