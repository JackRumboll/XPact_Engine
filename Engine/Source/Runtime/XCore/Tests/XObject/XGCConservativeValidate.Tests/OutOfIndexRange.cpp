// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGCConservativeValidate.Tests/OutOfIndexRange.cpp -- gate 2 rejection
// (XCoreXObject Rev 4 §5.3 step 2; Phase 5.e).
// =====================================================================
//
// Gate 2 (index-range): if the candidate's InternalIndex is out of
// the [1, FXObjectArray::Capacity()) range, the candidate is rejected.
//
// Test pattern: allocate a buffer in the XObject heap (gate 1 passes);
// stamp into the buffer an XObject-shaped layout whose InternalIndex
// field is bogus (negative, 0, or way past Capacity()).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XGCConservativeValidate.h"
#include "XObject/XObject.h"

#include <cstring>
#include <iostream>
#include <new>

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

    // Allocate via the heap so gate 1 passes.
    void* Storage = FXObjectAllocator::Get().AllocateRaw(
        sizeof(XObject), alignof(XObject), nullptr);
    Check(Storage != nullptr, "allocator returned nullptr");

    // Stamp an XObject layout into the storage but DO NOT register it
    // with FXObjectArray; instead use bogus InternalIndex values.

    // Case A: InternalIndex = 0 (null sentinel; gate 2 rejects).
    {
        XObject* Obj = new (Storage) XObject();
        Obj->InternalIndex = 0;
        Obj->SerialNumber  = 0;
        Check(ValidateConservativeCandidate(Obj) == nullptr,
              "Index 0 should be rejected by gate 2");
        Obj->~XObject();
    }

    // Case B: InternalIndex = -1 (sentinel for unregistered).
    {
        XObject* Obj = new (Storage) XObject();
        Obj->InternalIndex = -1;
        Obj->SerialNumber  = 0;
        Check(ValidateConservativeCandidate(Obj) == nullptr,
              "Index -1 should be rejected by gate 2");
        Obj->~XObject();
    }

    // Case C: InternalIndex = beyond capacity. Capacity() is a moving
    // target; pick a clearly out-of-range value (1 billion).
    {
        XObject* Obj = new (Storage) XObject();
        Obj->InternalIndex = 1'000'000'000;
        Obj->SerialNumber  = 0;
        Check(ValidateConservativeCandidate(Obj) == nullptr,
              "huge index should be rejected by gate 2");
        Obj->~XObject();
    }

    // Case D: InternalIndex = exactly Capacity() (boundary).
    {
        XObject* Obj = new (Storage) XObject();
        Obj->InternalIndex = Array.Capacity();
        Obj->SerialNumber  = 0;
        Check(ValidateConservativeCandidate(Obj) == nullptr,
              "Index == Capacity should be rejected by gate 2");
        Obj->~XObject();
    }

    // Cleanup.
    FXObjectAllocator::Get().Deallocate(Storage);
    Array.__ResetForTests();
    FXObjectAllocator::Get().__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XGCConservativeValidate.OutOfIndexRange: PASS\n";
        return 0;
    }
    std::cerr << "XGCConservativeValidate.OutOfIndexRange: " << FailureCount
              << " FAIL(s)\n";
    return 1;
}
