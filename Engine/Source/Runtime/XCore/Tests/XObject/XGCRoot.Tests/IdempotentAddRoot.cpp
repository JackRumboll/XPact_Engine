// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGCRoot.Tests/IdempotentAddRoot.cpp -- bit-pin idempotency
// (XCoreXObject Rev 4 §5.2 + Phase 5.e divergence note).
// =====================================================================
//
// Phase 5.e divergence from spec §5.2 wording: AddRoot is idempotent on
// the binary kRootPinnedBit. Second AddRoot on an already-pinned object
// returns false (no transition). Similarly RemoveRoot returns false on
// an already-cleared object.
//
// This test pins the contract explicitly: it documents the Phase 5.e
// behaviour vs the spec's "reference-counted" wording.
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

    XObject Obj;
    ::uint32 Serial = 0;
    const ::int32 Idx = Array.ReserveSlot(&Serial);
    Obj.InternalIndex = Idx;
    Obj.SerialNumber  = Serial;
    Array.BindObject(Idx, &Obj);

    // First AddRoot: true. Subsequent AddRoots: false.
    Check(XGCRoot::AddRoot(&Obj),  "AddRoot #1: expected true");
    Check(!XGCRoot::AddRoot(&Obj), "AddRoot #2: expected false (already pinned)");
    Check(!XGCRoot::AddRoot(&Obj), "AddRoot #3: expected false");
    Check(!XGCRoot::AddRoot(&Obj), "AddRoot #4: expected false");
    Check(XGCRoot::IsRooted(&Obj), "IsRooted after multi-AddRoot");

    // RemoveRoot: first true; subsequent false.
    Check(XGCRoot::RemoveRoot(&Obj),  "RemoveRoot #1: expected true");
    Check(!XGCRoot::RemoveRoot(&Obj), "RemoveRoot #2: expected false (already clear)");
    Check(!XGCRoot::RemoveRoot(&Obj), "RemoveRoot #3: expected false");
    Check(!XGCRoot::IsRooted(&Obj),   "IsRooted false after RemoveRoot");

    // Re-pin: AddRoot transition returns true again.
    Check(XGCRoot::AddRoot(&Obj),  "AddRoot after RemoveRoot: expected true");
    Check(!XGCRoot::AddRoot(&Obj), "Second AddRoot post-clear cycle: expected false");

    // Cleanup.
    (void)XGCRoot::RemoveRoot(&Obj);
    Array.FreeEntry(Idx);
    Array.__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XGCRoot.IdempotentAddRoot: PASS\n";
        return 0;
    }
    std::cerr << "XGCRoot.IdempotentAddRoot: " << FailureCount << " FAIL(s)\n";
    return 1;
}
