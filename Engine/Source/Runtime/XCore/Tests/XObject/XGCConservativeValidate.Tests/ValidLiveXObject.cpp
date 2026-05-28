// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGCConservativeValidate.Tests/ValidLiveXObject.cpp -- gate happy path
// (XCoreXObject Rev 4 §5.3 + Rev 2 FIX-A-MED-35; Phase 5.e).
// =====================================================================
//
// Round-trip: allocate an XObject via FXObjectAllocator + register it
// with FXObjectArray; ValidateConservativeCandidate on its address
// returns the same XObject.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FClass.h"
#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XGCConservativeValidate.h"
#include "XObject/XObject.h"

#include <iostream>

int main()
{
    using ::XCore::FXObjectAllocator;
    using ::XCore::FXObjectArray;
    using ::XCore::ValidateConservativeCandidate;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray& Array = FXObjectArray::Get();
    Array.__ResetForTests();
    FXObjectAllocator::Get().__ResetForTests();

    int FailureCount = 0;
    auto Check = [&](bool Cond, const char* Diagnostic)
    {
        if (!Cond)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++FailureCount;
        }
    };

    // Allocate via the heap path; the allocator's slab tracking enables
    // IsHeapAddress to return true for the resulting pointer.
    void* Storage = FXObjectAllocator::Get().AllocateRaw(
        sizeof(XObject), alignof(XObject), nullptr);
    Check(Storage != nullptr, "allocator returned nullptr");

    XObject* Obj = new (Storage) XObject();
    ::uint32 Serial = 0;
    const ::int32 Idx = Array.ReserveSlot(&Serial);
    Obj->InternalIndex = Idx;
    Obj->SerialNumber  = Serial;
    Array.BindObject(Idx, Obj);

    // Validate: all four gates pass; result == Obj.
    XObject* Result = ValidateConservativeCandidate(Obj);
    Check(Result == Obj,
          "ValidateConservativeCandidate(live XObject) did not return the object");

    // Re-validate multiple times: each call independently passes the
    // four gates.
    for (int I = 0; I < 8; ++I)
    {
        XObject* R = ValidateConservativeCandidate(Obj);
        Check(R == Obj,
              "repeat ValidateConservativeCandidate failed");
    }

    // Cleanup.
    Obj->~XObject();
    FXObjectAllocator::Get().Deallocate(Obj);
    Array.FreeEntry(Idx);
    Array.__ResetForTests();
    FXObjectAllocator::Get().__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XGCConservativeValidate.ValidLiveXObject: PASS\n";
        return 0;
    }
    std::cerr << "XGCConservativeValidate.ValidLiveXObject: " << FailureCount
              << " FAIL(s)\n";
    return 1;
}
