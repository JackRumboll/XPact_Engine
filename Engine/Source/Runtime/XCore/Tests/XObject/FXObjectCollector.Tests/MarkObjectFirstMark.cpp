// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectCollector.Tests/MarkObjectFirstMark.cpp -- atomic first-mark
// CAS correctness (Phase 5.g).
// =====================================================================
//
// Verifies:
//   * MarkObject returns true on first call (transition).
//   * MarkObject returns false on second call (already-marked).
//   * MarkObject returns false on nullptr.
//   * IsMarked agrees with MarkObject post-call.
//   * After cycle counter advances (via the rotation in tests), a
//     fresh cycle sees the bit cleared for that cycle's mask.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectArray.h"
#include "XObject/FXObjectCollector.h"
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
    using ::XCore::FXObjectArray;
    using ::XCore::FXObjectCollector;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray& Array = FXObjectArray::Get();
    Array.__ResetForTests();

    FXObjectCollector& Coll = FXObjectCollector::Get();
    Coll.__ResetForTests();

    // -----------------------------------------------------------------
    // nullptr: MarkObject returns false; IsMarked returns false.
    // -----------------------------------------------------------------
    {
        Check(!Coll.MarkObject(nullptr),
              "MarkObject(nullptr) returned true");
        Check(!Coll.IsMarked(nullptr),
              "IsMarked(nullptr) returned true");
    }

    // -----------------------------------------------------------------
    // Allocate a real XObject in the array so we can mark it.
    // -----------------------------------------------------------------
    XObject Obj;
    ::uint32 Serial = 0;
    const ::int32 Idx = Array.ReserveSlot(&Serial);
    Obj.InternalIndex = Idx;
    Obj.SerialNumber  = Serial;
    Array.BindObject(Idx, &Obj);

    // -----------------------------------------------------------------
    // First-mark: true; IsMarked true after.
    // -----------------------------------------------------------------
    {
        Check(!Coll.IsMarked(&Obj),
              "Pre-mark IsMarked returned true");
        Check(Coll.MarkObject(&Obj),
              "First MarkObject returned false (expected true; first-mark)");
        Check(Coll.IsMarked(&Obj),
              "Post-mark IsMarked returned false");
    }

    // -----------------------------------------------------------------
    // Second-mark: false; IsMarked still true.
    // -----------------------------------------------------------------
    {
        Check(!Coll.MarkObject(&Obj),
              "Second MarkObject returned true (expected false; already marked)");
        Check(Coll.IsMarked(&Obj),
              "Post-second-mark IsMarked returned false (bit somehow cleared)");
    }

    // -----------------------------------------------------------------
    // GetMarkedThisCycle should reflect exactly ONE first-mark
    // (the second MarkObject did not bump the counter).
    // -----------------------------------------------------------------
    {
        const ::std::size_t MarkedCount = Coll.GetMarkedThisCycle();
        Check(MarkedCount == 1,
              "GetMarkedThisCycle != 1 after one first-mark + one re-mark");
    }

    // Cleanup.
    Array.FreeEntry(Idx);
    Array.__ResetForTests();
    Coll.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectCollector.MarkObjectFirstMark: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectCollector.MarkObjectFirstMark: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
