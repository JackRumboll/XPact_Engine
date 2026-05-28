// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGCRoot.Tests/NullSentinelHandling.cpp -- nullptr + index-0 defence
// (XCoreXObject Rev 4 §5.2; Phase 5.e).
// =====================================================================
//
// Spec / Phase 5.e defence-in-depth:
//   * XGCRoot::AddRoot(nullptr)    -- no-op; returns false.
//   * XGCRoot::RemoveRoot(nullptr) -- no-op; returns false.
//   * XGCRoot::IsRooted(nullptr)   -- returns false.
//   * AddRoot on InternalIndex == 0 (the null sentinel slot) -- no-op
//     + returns false. FXObjectArray::SetRootPin rejects index 0.
//   * AddRoot on out-of-range InternalIndex -- no-op + returns false
//     (defence-in-depth; in practice the caller shouldn't construct
//     such an XObject, but the gate provides safety).
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

    // -----------------------------------------------------------------
    // nullptr passes through every entry point cleanly.
    // -----------------------------------------------------------------
    Check(!XGCRoot::AddRoot(nullptr),    "AddRoot(nullptr) returned true");
    Check(!XGCRoot::RemoveRoot(nullptr), "RemoveRoot(nullptr) returned true");
    Check(!XGCRoot::IsRooted(nullptr),   "IsRooted(nullptr) returned true");

    // -----------------------------------------------------------------
    // An unregistered XObject (InternalIndex == INDEX_NONE / -1) is
    // also a no-op.
    // -----------------------------------------------------------------
    {
        XObject Unregistered;  // default ctor: InternalIndex = INDEX_NONE
        Check(!XGCRoot::AddRoot(&Unregistered),
              "AddRoot on unregistered XObject returned true");
        Check(!XGCRoot::IsRooted(&Unregistered),
              "IsRooted on unregistered XObject returned true");
        Check(!XGCRoot::RemoveRoot(&Unregistered),
              "RemoveRoot on unregistered XObject returned true");
    }

    // -----------------------------------------------------------------
    // An XObject with InternalIndex == 0 (the null-sentinel slot) is
    // also rejected.
    // -----------------------------------------------------------------
    {
        XObject ZeroSlot;
        ZeroSlot.InternalIndex = 0;
        Check(!XGCRoot::AddRoot(&ZeroSlot),
              "AddRoot on InternalIndex == 0 returned true");
    }

    // -----------------------------------------------------------------
    // The valid case still works: a freshly-registered XObject can be
    // pinned + unpinned.
    // -----------------------------------------------------------------
    {
        XObject Obj;
        ::uint32 Serial = 0;
        const ::int32 Idx = Array.ReserveSlot(&Serial);
        Obj.InternalIndex = Idx;
        Obj.SerialNumber  = Serial;
        Array.BindObject(Idx, &Obj);

        Check(XGCRoot::AddRoot(&Obj),    "Valid AddRoot returned false");
        Check(XGCRoot::IsRooted(&Obj),   "Valid IsRooted returned false");
        Check(XGCRoot::RemoveRoot(&Obj), "Valid RemoveRoot returned false");
        Array.FreeEntry(Idx);
    }

    Array.__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XGCRoot.NullSentinelHandling: PASS\n";
        return 0;
    }
    std::cerr << "XGCRoot.NullSentinelHandling: " << FailureCount
              << " FAIL(s)\n";
    return 1;
}
