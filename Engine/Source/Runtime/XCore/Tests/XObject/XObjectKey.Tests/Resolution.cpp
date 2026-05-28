// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XObjectKey.Tests/Resolution.cpp -- Resolve() entry point
// (XCoreXObject Rev 4 §6.4).
// =====================================================================
//
// XObjectKey.Resolve() routes through FXObjectArray::GetObjectAtIndex.
// Returns nullptr on:
//   * null sentinel (IsNull())
//   * out-of-range InternalIndex
//   * SerialNumber mismatch (slot reused)
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XObject.h"
#include "XObject/XObjectKey.h"

#include <cstdint>
#include <iostream>

int main()
{
    using ::XCore::FXObjectArray;
    using ::XCore::XObject;
    using ::XCore::XObjectKey;

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

    // -----------------------------------------------------------------
    // Null key resolves to nullptr.
    // -----------------------------------------------------------------
    {
        XObjectKey K;
        Check(K.Resolve() == nullptr, "null key: Resolve() != nullptr");
        Check(!K.IsValid(),           "null key: IsValid() true");
    }

    // -----------------------------------------------------------------
    // Live key resolves to the bound XObject.
    // -----------------------------------------------------------------
    {
        XObject Obj;
        ::uint32 Serial = 0;
        const ::int32 Idx = Array.ReserveSlot(&Serial);
        Obj.InternalIndex = Idx;
        Obj.SerialNumber  = Serial;
        Array.BindObject(Idx, &Obj);

        XObjectKey K(&Obj);
        Check(K.InternalIndex == Idx,    "live: K.InternalIndex != Idx");
        Check(K.SerialNumber  == Serial, "live: K.SerialNumber  != Serial");

        XObject* Resolved = K.Resolve();
        Check(Resolved == &Obj, "live: Resolve() != &Obj");
        Check(K.IsValid(),      "live: IsValid() false");

        Array.FreeEntry(Idx);
    }

    // -----------------------------------------------------------------
    // After FreeEntry the captured key resolves to nullptr (the
    // SerialNumber was bumped at free time so the captured value no
    // longer matches the entry's current value).
    // -----------------------------------------------------------------
    {
        XObject Obj;
        ::uint32 Serial = 0;
        const ::int32 Idx = Array.ReserveSlot(&Serial);
        Obj.InternalIndex = Idx;
        Obj.SerialNumber  = Serial;
        Array.BindObject(Idx, &Obj);

        XObjectKey K(&Obj);
        Check(K.Resolve() == &Obj, "pre-free: Resolve() != &Obj");
        Check(K.IsValid(),         "pre-free: IsValid() false");

        Array.FreeEntry(Idx);

        Check(K.Resolve() == nullptr, "post-free: Resolve() != nullptr");
        Check(!K.IsValid(),           "post-free: IsValid() true");
    }

    // -----------------------------------------------------------------
    // If the slot is REUSED for a different object, the prior key
    // resolves to nullptr (SerialNumber mismatch).
    // -----------------------------------------------------------------
    {
        XObject FirstObj;
        ::uint32 FirstSerial = 0;
        const ::int32 FirstIdx = Array.ReserveSlot(&FirstSerial);
        FirstObj.InternalIndex = FirstIdx;
        FirstObj.SerialNumber  = FirstSerial;
        Array.BindObject(FirstIdx, &FirstObj);

        // Capture key referencing FirstObj's slot.
        XObjectKey K(&FirstObj);

        // Free + reallocate the slot. LIFO free list returns the same
        // slot index; the SerialNumber is bumped at FreeEntry so the
        // reused slot has a different SerialNumber from the captured
        // FirstSerial.
        Array.FreeEntry(FirstIdx);

        XObject SecondObj;
        ::uint32 SecondSerial = 0;
        const ::int32 SecondIdx = Array.ReserveSlot(&SecondSerial);
        SecondObj.InternalIndex = SecondIdx;
        SecondObj.SerialNumber  = SecondSerial;
        Array.BindObject(SecondIdx, &SecondObj);

        Check(SecondIdx == FirstIdx,
              "slot reuse: expected LIFO free-list to return the same Idx");
        Check(SecondSerial != FirstSerial,
              "slot reuse: SerialNumber not bumped at FreeEntry");

        // The captured key referenced FirstSerial; the entry now
        // carries SecondSerial. Resolve must return nullptr.
        Check(K.Resolve() == nullptr,
              "slot reuse: Resolve() returned non-null after SerialNumber bump");
        Check(!K.IsValid(),
              "slot reuse: IsValid() true after SerialNumber bump");

        Array.FreeEntry(SecondIdx);
    }

    Array.__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XObjectKey.Resolution: PASS\n";
        return 0;
    }
    std::cerr << "XObjectKey.Resolution: " << FailureCount << " FAIL(s)\n";
    return 1;
}
