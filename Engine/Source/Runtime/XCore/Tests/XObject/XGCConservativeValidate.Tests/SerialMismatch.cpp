// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGCConservativeValidate.Tests/SerialMismatch.cpp -- gates 3/4 reject
// (XCoreXObject Rev 4 §5.3 steps 3-4; Phase 5.e).
// =====================================================================
//
// Gate 3 (entry-bind) + Gate 4 (serial match) catch the "slot was
// freed + reused" footgun. A candidate captured pre-free still has
// the old InternalIndex but the FXObjectArrayEntry's SerialNumber has
// bumped past it.
//
// Test pattern:
//   1. Allocate Obj_A via the heap; register; capture pointer +
//      InternalIndex + SerialNumber.
//   2. Stamp Obj_A's slot bytes with a remembered serial value (the
//      slot bytes still live in the heap; gate 1 passes).
//   3. FreeEntry on Obj_A's slot (the entry's SerialNumber bumps).
//   4. Validate the captured pointer -- gate 3 (entry.Object == this)
//      fails because Free nulled the entry's Object pointer; OR gate 4
//      (serial match) fails because the entry's SerialNumber moved.
//      Either rejection is correct.
//   5. Re-allocate the slot for Obj_B; the captured-Obj_A pointer's
//      InternalIndex is the same; gate 3 catches Obj_A.Object !=
//      entry.Object (entry now binds Obj_B); gate 4 catches the
//      mismatched serial.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XGCConservativeValidate.h"
#include "XObject/XObject.h"

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

    // Allocate Obj_A; register; capture identity.
    void* Storage_A = FXObjectAllocator::Get().AllocateRaw(
        sizeof(XObject), alignof(XObject), nullptr);
    XObject* Obj_A = new (Storage_A) XObject();
    ::uint32 Serial_A = 0;
    const ::int32 Idx_A = Array.ReserveSlot(&Serial_A);
    Obj_A->InternalIndex = Idx_A;
    Obj_A->SerialNumber  = Serial_A;
    Array.BindObject(Idx_A, Obj_A);

    // Validate Obj_A right now: should succeed (all gates pass).
    Check(ValidateConservativeCandidate(Obj_A) == Obj_A,
          "baseline: live Obj_A should validate");

    // Free Obj_A's slot. The entry's SerialNumber bumps; the entry's
    // Object pointer is nulled.
    Array.FreeEntry(Idx_A);

    // Obj_A's storage is STILL the same heap address (we have not
    // returned it to the allocator) so gate 1 still passes. But the
    // stored XObject header still claims InternalIndex == Idx_A and
    // SerialNumber == Serial_A. Gate 3 (entry.Object == Obj_A) fails
    // because the entry's Object was nulled by FreeEntry.
    Check(ValidateConservativeCandidate(Obj_A) == nullptr,
          "after-FreeEntry: stale Obj_A pointer should not validate");

    // Now allocate Obj_B and let it claim the freed slot (LIFO free
    // list returns Idx_A first).
    void* Storage_B = FXObjectAllocator::Get().AllocateRaw(
        sizeof(XObject), alignof(XObject), nullptr);
    XObject* Obj_B = new (Storage_B) XObject();
    ::uint32 Serial_B = 0;
    const ::int32 Idx_B = Array.ReserveSlot(&Serial_B);
    Obj_B->InternalIndex = Idx_B;
    Obj_B->SerialNumber  = Serial_B;
    Array.BindObject(Idx_B, Obj_B);

    // If the free-list reused Idx_A, then Idx_B == Idx_A; the entry's
    // SerialNumber has bumped from Serial_A; Obj_A's captured serial
    // no longer matches.
    if (Idx_B == Idx_A)
    {
        // Obj_B is now bound at the same index Obj_A was. Validating
        // Obj_A's pointer:
        //   * Gate 1: Obj_A's storage is heap -> passes.
        //   * Gate 2: Obj_A->InternalIndex is in range -> passes.
        //   * Gate 3: entry.Object == Obj_B != Obj_A -> FAILS. Rejected.
        Check(ValidateConservativeCandidate(Obj_A) == nullptr,
              "post-reuse: stale Obj_A should be rejected at gate 3");
        // Obj_B should validate freshly.
        Check(ValidateConservativeCandidate(Obj_B) == Obj_B,
              "fresh Obj_B should validate");
    }
    else
    {
        // The free list happened to give Obj_B a different slot. The
        // entry at Idx_A is now genuinely free (Object == nullptr).
        // Gate 3 rejects.
        Check(ValidateConservativeCandidate(Obj_A) == nullptr,
              "post-reuse-different-slot: stale Obj_A should be rejected");
    }

    // Cleanup.
    Obj_B->~XObject();
    FXObjectAllocator::Get().Deallocate(Obj_B);
    Array.FreeEntry(Idx_B);
    Obj_A->~XObject();
    FXObjectAllocator::Get().Deallocate(Obj_A);
    Array.__ResetForTests();
    FXObjectAllocator::Get().__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XGCConservativeValidate.SerialMismatch: PASS\n";
        return 0;
    }
    std::cerr << "XGCConservativeValidate.SerialMismatch: " << FailureCount
              << " FAIL(s)\n";
    return 1;
}
