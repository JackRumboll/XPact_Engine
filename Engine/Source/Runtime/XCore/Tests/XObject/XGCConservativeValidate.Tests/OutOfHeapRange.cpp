// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGCConservativeValidate.Tests/OutOfHeapRange.cpp -- gate 1 rejection
// (XCoreXObject Rev 4 §5.3 step 1; Phase 5.e).
// =====================================================================
//
// Gate 1 (heap-range): non-heap addresses are rejected WITHOUT any
// dereference of the candidate. This test passes addresses that
// CANNOT be heap-allocated XObjects:
//
//   * nullptr.
//   * A stack-local XObject's address (stack is not in the allocator's
//     slab range).
//   * A garbage non-heap address (0x1234, a small numeric value).
//   * A heap-allocated non-XObject byte buffer (the allocator's slab
//     range is for XObjects only; an FMemory::Malloc allocation lives
//     in a different region).
//
// Each MUST return nullptr from ValidateConservativeCandidate.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XGCConservativeValidate.h"
#include "XObject/XObject.h"

#include <cstdint>
#include <iostream>

int main()
{
    using ::XCore::FXObjectAllocator;
    using ::XCore::FXObjectArray;
    using ::XCore::ValidateConservativeCandidate;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray::Get().__ResetForTests();
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

    // nullptr.
    Check(ValidateConservativeCandidate(nullptr) == nullptr,
          "nullptr should be rejected by gate 1");

    // Stack-local XObject.
    {
        XObject Stack;
        Check(ValidateConservativeCandidate(&Stack) == nullptr,
              "stack XObject should be rejected by gate 1");
    }

    // Small numeric garbage value.
    {
        const void* Garbage = reinterpret_cast<const void*>(
            static_cast<::uintptr_t>(0x1234));
        Check(ValidateConservativeCandidate(Garbage) == nullptr,
              "garbage 0x1234 should be rejected by gate 1");
    }

    // Larger garbage value.
    {
        const void* Garbage = reinterpret_cast<const void*>(
            static_cast<::uintptr_t>(0xDEADBEEFCAFE));
        Check(ValidateConservativeCandidate(Garbage) == nullptr,
              "garbage 0xDEADBEEFCAFE should be rejected by gate 1");
    }

    // Heap-allocated NON-XObject buffer (FMemory::Malloc, not the
    // XObject allocator). The FMemory tag isn't FMemTag::XObject so
    // the FXObjectAllocator's slab tables don't track it; gate 1
    // rejects.
    {
        void* Buffer = ::XCore::HAL::FMemory::MallocOrAbort(
            128, 8, ::XCore::HAL::FMemTag::Reflection);
        Check(ValidateConservativeCandidate(Buffer) == nullptr,
              "FMemory-buffer should be rejected by gate 1 (non-XObject heap)");
        ::XCore::HAL::FMemory::Free(Buffer);
    }

    FXObjectArray::Get().__ResetForTests();
    FXObjectAllocator::Get().__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XGCConservativeValidate.OutOfHeapRange: PASS\n";
        return 0;
    }
    std::cerr << "XGCConservativeValidate.OutOfHeapRange: " << FailureCount
              << " FAIL(s)\n";
    return 1;
}
