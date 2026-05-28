// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectArray.Tests/IndexReuseSerialBump.cpp -- SerialNumber bump
// on slot reuse (XCoreXObject Rev 4 §3.3).
// =====================================================================
//
// Spec §3.3 trailing prose: "FreeEntry bumps SerialNumber so every
// XWeakPtr captured before free returns nullptr on deref."
//
// This is the load-bearing weak-pointer invalidation event. The test
// allocates a slot, captures its SerialNumber, frees the slot, then
// re-allocates and verifies the SerialNumber bumped.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectArray.h"
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
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray& Array = FXObjectArray::Get();
    Array.__ResetForTests();

    // -----------------------------------------------------------------
    // Test 1: AllocateEntry + FreeEntry + AllocateEntry against the
    // same slot bumps SerialNumber.
    //
    // The FXObjectArray's free-list is LIFO, so freeing slot N then
    // allocating again returns slot N (with bumped serial).
    // -----------------------------------------------------------------
    {
        XObject Obj1;
        const ::int32 Idx1 = Array.AllocateEntry(&Obj1);
        Check(Idx1 >= 1, "AllocateEntry returned Index < 1");

        // The serial number of a fresh slot is 1 (per the FXObjectArray
        // ctor body: freshly-bumped slots get SerialNumber = 1).
        // Capture via the two-step API indirection: GetObjectAtIndex
        // with a 0-serial returns nullptr but does not raise.
        // We use the AllocateEntry serial-bump invariant: re-alloc
        // bumps it. Skip the direct read here.

        Array.FreeEntry(Idx1);

        XObject Obj2;
        const ::int32 Idx2 = Array.AllocateEntry(&Obj2);

        // LIFO: Idx2 should equal Idx1 (freed slot re-used).
        Check(Idx2 == Idx1,
              "AllocateEntry after Free did not re-use the freed slot");

        // Verify the SerialNumber bumped: GetObjectAtIndex with serial
        // == 1 (the original fresh slot's serial) should now return
        // nullptr because the serial has bumped to 2.
        XObject* Stale = Array.GetObjectAtIndex(Idx2, /*Expected=*/1);
        Check(Stale == nullptr,
              "GetObjectAtIndex with stale serial 1 returned non-null "
              "after FreeEntry+AllocateEntry (serial did not bump)");

        // The current serial is 2; GetObjectAtIndex with serial 2 should
        // return Obj2.
        XObject* Live = Array.GetObjectAtIndex(Idx2, /*Expected=*/2);
        Check(Live == &Obj2,
              "GetObjectAtIndex with current serial 2 returned wrong "
              "Object after slot reuse");

        Array.FreeEntry(Idx2);
    }

    // -----------------------------------------------------------------
    // Test 2: Multiple cycles of Free + Allocate keep bumping the
    // SerialNumber (each cycle increments by 1).
    // -----------------------------------------------------------------
    {
        XObject Obj;
        ::int32 LastIdx = Array.AllocateEntry(&Obj);
        for (int Cycle = 0; Cycle < 5; ++Cycle)
        {
            Array.FreeEntry(LastIdx);
            const ::int32 NewIdx = Array.AllocateEntry(&Obj);
            Check(NewIdx == LastIdx,
                  "LIFO contract: re-alloc returned a different slot");
            LastIdx = NewIdx;
        }
        // After 5 cycles, the serial number for this slot has bumped
        // multiple times. The probe with serial = 1 (the original)
        // returns nullptr.
        XObject* Stale = Array.GetObjectAtIndex(LastIdx, 1);
        Check(Stale == nullptr,
              "Stale-serial probe surfaced object after many bumps");
        Array.FreeEntry(LastIdx);
    }

    // -----------------------------------------------------------------
    // Test 3: ReserveSlot + BindObject round-trip. Reset the array
    // first so the freshly-bumped slot reliably has SerialNumber == 1.
    // -----------------------------------------------------------------
    Array.__ResetForTests();
    {
        ::uint32 Serial1 = 0;
        const ::int32 Idx = Array.ReserveSlot(&Serial1);
        Check(Idx >= 1, "ReserveSlot returned Index < 1");
        Check(Serial1 == 1u,
              "ReserveSlot post-reset fresh-slot serial != 1");

        XObject Obj;
        Obj.SerialNumber = Serial1;
        Array.BindObject(Idx, &Obj);

        Check(Array.GetObjectAtIndex(Idx, Serial1) == &Obj,
              "Post-BindObject: lookup did not return the bound object");

        Array.FreeEntry(Idx);

        // Re-reserve the same slot (LIFO recycle). The serial bumped
        // to 2 at FreeEntry; ReserveSlot reuses the bumped value.
        ::uint32 Serial2 = 0;
        const ::int32 Idx2 = Array.ReserveSlot(&Serial2);
        Check(Idx2 == Idx, "ReserveSlot: recycled slot index mismatch");
        Check(Serial2 != Serial1, "ReserveSlot: serial did not bump on recycle");

        Array.FreeEntry(Idx2);
    }

    Array.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectArray.IndexReuseSerialBump: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectArray.IndexReuseSerialBump: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
