// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGCRoot.Tests/AddRootRemoveRoot.cpp -- bit-pin round trip
// (XCoreXObject Rev 4 §5.1 / §5.2; Phase 5.e).
// =====================================================================
//
// Spec §5.2: AddRoot sets kRootPinnedBit on the entry's StateBits;
// RemoveRoot clears it. IsRooted reflects the current bit state.
//
// Phase 5.e bit-pin divergence: AddRoot is idempotent. The first call
// returns true (transition); a second call returns false (already
// pinned). RemoveRoot is symmetric: first returns true; second
// returns false (already cleared).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XGCRoot.h"
#include "XObject/XObject.h"

#include <iostream>

int main()
{
    using ::XCore::FXObjectArray;
    using ::XCore::XGCRoot;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray& Array = FXObjectArray::Get();
    Array.__ResetForTests();

    int FailureCount = 0;
    auto Check = [&](bool Cond, const char* Diagnostic)
    {
        if (!Cond)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++FailureCount;
        }
    };

    // Register a stack XObject with the FXObjectArray.
    XObject Obj;
    ::uint32 Serial = 0;
    const ::int32 Idx = Array.ReserveSlot(&Serial);
    Obj.InternalIndex = Idx;
    Obj.SerialNumber  = Serial;
    Array.BindObject(Idx, &Obj);

    // -----------------------------------------------------------------
    // Baseline: not pinned.
    // -----------------------------------------------------------------
    Check(!XGCRoot::IsRooted(&Obj),
          "baseline: IsRooted(&Obj) returned true before AddRoot");

    // -----------------------------------------------------------------
    // First AddRoot: returns true (transition); IsRooted now true.
    // -----------------------------------------------------------------
    Check(XGCRoot::AddRoot(&Obj),
          "AddRoot first call returned false (expected true on transition)");
    Check(XGCRoot::IsRooted(&Obj),
          "post-AddRoot: IsRooted(&Obj) returned false");

    // -----------------------------------------------------------------
    // First RemoveRoot: returns true (transition); IsRooted false.
    // -----------------------------------------------------------------
    Check(XGCRoot::RemoveRoot(&Obj),
          "RemoveRoot first call returned false (expected true on transition)");
    Check(!XGCRoot::IsRooted(&Obj),
          "post-RemoveRoot: IsRooted(&Obj) returned true");

    // -----------------------------------------------------------------
    // Multiple cycles: each AddRoot + RemoveRoot pair returns true.
    // -----------------------------------------------------------------
    for (int Cycle = 0; Cycle < 10; ++Cycle)
    {
        Check(XGCRoot::AddRoot(&Obj),
              "cycle AddRoot returned false");
        Check(XGCRoot::IsRooted(&Obj),
              "cycle IsRooted false after AddRoot");
        Check(XGCRoot::RemoveRoot(&Obj),
              "cycle RemoveRoot returned false");
        Check(!XGCRoot::IsRooted(&Obj),
              "cycle IsRooted true after RemoveRoot");
    }

    // Cleanup.
    Array.FreeEntry(Idx);
    Array.__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XGCRoot.AddRootRemoveRoot: PASS\n";
        return 0;
    }
    std::cerr << "XGCRoot.AddRootRemoveRoot: " << FailureCount << " FAIL(s)\n";
    return 1;
}
