// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectArray.Tests/Index0Sentinel.cpp -- null-sentinel reservation
// (XCoreXObject Rev 4 §3.3).
// =====================================================================
//
// Spec §3.3 trailing prose: "Index 0 is reserved as the null sentinel.
// A freshly-zeroed XWeakPtr / XObjectKey / XPtr with InternalIndex == 0
// represents null; the array's bootstrap reserves index 0 with
// Object = nullptr and SerialNumber = 0 so the slot can never be
// assigned to a real object."
//
// Verifies:
//
//   1. AllocateEntry NEVER returns 0.
//   2. ReserveSlot NEVER returns 0.
//   3. GetObjectAtIndex(0, AnySerial) returns nullptr.
//   4. kFXObjectArrayNullIndex == 0 constant matches.
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
    // The null-sentinel constant.
    // -----------------------------------------------------------------
    Check(::XCore::kFXObjectArrayNullIndex == 0,
          "kFXObjectArrayNullIndex != 0");

    // -----------------------------------------------------------------
    // AllocateEntry returns Index >= 1.
    // -----------------------------------------------------------------
    {
        XObject Obj;
        ::int32 Indices[8];
        for (int I = 0; I < 8; ++I)
        {
            Indices[I] = Array.AllocateEntry(&Obj);
            Check(Indices[I] >= 1,
                  "AllocateEntry returned Index < 1 (null sentinel violated)");
        }
        for (int I = 0; I < 8; ++I)
        {
            Array.FreeEntry(Indices[I]);
        }
    }

    // -----------------------------------------------------------------
    // ReserveSlot returns Index >= 1.
    // -----------------------------------------------------------------
    {
        for (int I = 0; I < 8; ++I)
        {
            ::uint32 Serial = 0;
            const ::int32 Idx = Array.ReserveSlot(&Serial);
            Check(Idx >= 1,
                  "ReserveSlot returned Index < 1 (null sentinel violated)");
            Array.FreeEntry(Idx);
        }
    }

    // -----------------------------------------------------------------
    // GetObjectAtIndex(0, ...) returns nullptr.
    // -----------------------------------------------------------------
    {
        XObject* AtZero = Array.GetObjectAtIndex(0, 0);
        Check(AtZero == nullptr,
              "GetObjectAtIndex(0, 0) != nullptr (null sentinel violated)");
        // Even with a non-zero serial.
        XObject* AtZero2 = Array.GetObjectAtIndex(0, 12345);
        Check(AtZero2 == nullptr,
              "GetObjectAtIndex(0, NonZero) != nullptr");
    }

    // -----------------------------------------------------------------
    // After AllocateEntry + FreeEntry cycles, index 0 is STILL not
    // returned (the free-list path can re-use freed slots but never
    // index 0).
    //
    // We allocate + free 100 distinct slots; index 0 must never appear.
    // -----------------------------------------------------------------
    {
        XObject Obj;
        for (int I = 0; I < 100; ++I)
        {
            const ::int32 Idx = Array.AllocateEntry(&Obj);
            Check(Idx != 0, "Cycle alloc returned Index 0");
            Array.FreeEntry(Idx);
        }
    }

    Array.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectArray.Index0Sentinel: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectArray.Index0Sentinel: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
