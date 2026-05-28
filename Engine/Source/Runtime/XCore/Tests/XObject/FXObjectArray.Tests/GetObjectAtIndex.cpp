// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectArray.Tests/GetObjectAtIndex.cpp -- weak-ptr deref entry
// point (XCoreXObject Rev 4 §3.3).
// =====================================================================
//
// Spec §3.3: GetObjectAtIndex returns the bound XObject* iff the
// captured SerialNumber matches the current entry's SerialNumber.
// Returns nullptr on:
//   * InternalIndex == 0 (null sentinel)
//   * InternalIndex out of committed range
//   * SerialNumber mismatch (slot was reused)
//   * Object pointer is nullptr (brief ReserveSlot/BindObject window)
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
    // Test 1: GetObjectAtIndex with matching SerialNumber returns the
    // bound object.
    // -----------------------------------------------------------------
    {
        XObject Obj;
        ::uint32 Serial = 0;
        const ::int32 Idx = Array.ReserveSlot(&Serial);
        Obj.SerialNumber = Serial;
        Array.BindObject(Idx, &Obj);

        XObject* Found = Array.GetObjectAtIndex(Idx, Serial);
        Check(Found == &Obj,
              "GetObjectAtIndex with matching serial did not return "
              "the bound object");

        Array.FreeEntry(Idx);
    }

    // -----------------------------------------------------------------
    // Test 2: GetObjectAtIndex with MISMATCHED SerialNumber returns
    // nullptr (the weak-ptr dangling-reference detection path).
    // -----------------------------------------------------------------
    {
        XObject Obj;
        ::uint32 Serial = 0;
        const ::int32 Idx = Array.ReserveSlot(&Serial);
        Obj.SerialNumber = Serial;
        Array.BindObject(Idx, &Obj);

        // Mismatched serial.
        XObject* Wrong = Array.GetObjectAtIndex(Idx, Serial + 100);
        Check(Wrong == nullptr,
              "GetObjectAtIndex with mismatched serial did not return "
              "nullptr");

        Array.FreeEntry(Idx);
    }

    // -----------------------------------------------------------------
    // Test 3: GetObjectAtIndex on the null-sentinel index (0) returns
    // nullptr regardless of the serial.
    // -----------------------------------------------------------------
    {
        XObject* AtZero = Array.GetObjectAtIndex(0, 0);
        Check(AtZero == nullptr,
              "GetObjectAtIndex(0) did not return nullptr");
        XObject* AtZeroAny = Array.GetObjectAtIndex(0, 12345);
        Check(AtZeroAny == nullptr,
              "GetObjectAtIndex(0, NonZeroSerial) did not return nullptr");
    }

    // -----------------------------------------------------------------
    // Test 4: GetObjectAtIndex on an out-of-range index returns nullptr.
    //
    // The committed capacity is the first 32k entries by default; we
    // probe at INT32_MAX which is guaranteed out-of-range.
    // -----------------------------------------------------------------
    {
        XObject* OutOfRange = Array.GetObjectAtIndex(2'000'000'000, 1);
        Check(OutOfRange == nullptr,
              "GetObjectAtIndex on out-of-range index did not return "
              "nullptr");
    }

    // -----------------------------------------------------------------
    // Test 5: Negative index returns nullptr (the implementation
    // short-circuits InternalIndex <= 0).
    // -----------------------------------------------------------------
    {
        XObject* Neg = Array.GetObjectAtIndex(-1, 1);
        Check(Neg == nullptr,
              "GetObjectAtIndex(-1) did not return nullptr");
    }

    // -----------------------------------------------------------------
    // Test 6: ReserveSlot without BindObject leaves Object == nullptr;
    // GetObjectAtIndex with the matching SerialNumber returns nullptr
    // because Object is still nullptr.
    // -----------------------------------------------------------------
    {
        ::uint32 Serial = 0;
        const ::int32 Idx = Array.ReserveSlot(&Serial);
        // Intentionally skip BindObject.
        XObject* PreBind = Array.GetObjectAtIndex(Idx, Serial);
        Check(PreBind == nullptr,
              "GetObjectAtIndex on unbound (reserved-but-not-bound) slot "
              "did not return nullptr");
        Array.FreeEntry(Idx);
    }

    Array.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectArray.GetObjectAtIndex: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectArray.GetObjectAtIndex: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
