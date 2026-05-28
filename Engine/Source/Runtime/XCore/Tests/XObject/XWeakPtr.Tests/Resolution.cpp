// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XWeakPtr.Tests/Resolution.cpp -- Get() + IsValid invariants
// (XCoreXObject Rev 4 §6.2).
// =====================================================================
//
// Spec §6.2 Get() returns the bound T* iff the captured SerialNumber
// matches the entry's. Returns nullptr on slot reuse / out-of-range /
// BeginDestroyed flag set.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XObject.h"
#include "XObject/XWeakPtr.h"

#include <cstdint>
#include <iostream>

int main()
{
    using ::XCore::FXObjectArray;
    using ::XCore::XObject;
    using ::XCore::XWeakPtr;
    using ::XCore::EObjectFlags;

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

    // Null weak-ptr.
    {
        XWeakPtr<XObject> W;
        Check(W.Get() == nullptr, "null: Get() != nullptr");
        Check(!W.IsValid(),       "null: IsValid() true");
    }

    // Live weak-ptr.
    {
        XObject Obj;
        ::uint32 Serial = 0;
        const ::int32 Idx = Array.ReserveSlot(&Serial);
        Obj.InternalIndex = Idx;
        Obj.SerialNumber  = Serial;
        Array.BindObject(Idx, &Obj);

        XWeakPtr<XObject> W(&Obj);
        Check(W.Get() == &Obj, "live: Get() != &Obj");
        Check(W.IsValid(),     "live: IsValid() false");

        Array.FreeEntry(Idx);
    }

    // Post-free weak-ptr returns nullptr (SerialNumber bumped on free).
    {
        XObject Obj;
        ::uint32 Serial = 0;
        const ::int32 Idx = Array.ReserveSlot(&Serial);
        Obj.InternalIndex = Idx;
        Obj.SerialNumber  = Serial;
        Array.BindObject(Idx, &Obj);

        XWeakPtr<XObject> W(&Obj);
        Array.FreeEntry(Idx);

        Check(W.Get() == nullptr, "post-free: Get() != nullptr");
        Check(!W.IsValid(),       "post-free: IsValid() true");
    }

    // BeginDestroyed flag suppresses Get() per spec §6.2 reference
    // impl.
    {
        XObject Obj;
        ::uint32 Serial = 0;
        const ::int32 Idx = Array.ReserveSlot(&Serial);
        Obj.InternalIndex = Idx;
        Obj.SerialNumber  = Serial;
        Array.BindObject(Idx, &Obj);

        XWeakPtr<XObject> W(&Obj);
        Check(W.Get() == &Obj, "pre-BeginDestroyed: Get() != &Obj");

        Obj.SetFlags(EObjectFlags::BeginDestroyed);
        Check(W.Get() == nullptr,
              "post-BeginDestroyed: Get() != nullptr (the BeginDestroyed "
              "filter at spec §6.2 reference impl is not in effect)");

        // Clear so cleanup paths see a sane state.
        Obj.ClearFlags(EObjectFlags::BeginDestroyed);

        Array.FreeEntry(Idx);
    }

    Array.__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XWeakPtr.Resolution: PASS\n";
        return 0;
    }
    std::cerr << "XWeakPtr.Resolution: " << FailureCount << " FAIL(s)\n";
    return 1;
}
