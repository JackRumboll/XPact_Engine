// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XWeakPtr.Tests/NoSelfRoot.cpp -- documents the strictly-non-rooting
// contract (XCoreXObject Rev 4 §6.2).
// =====================================================================
//
// Spec §6.2: XWeakPtr does NOT participate in GC root traversal --
// neither self-rooted nor property-scan-rooted. The collector ignores
// XWeakPtr-shaped slots during reachability analysis.
//
// Observable proxy at Phase 5.c: constructing / copying XWeakPtrs
// does NOT touch the FXObjectArrayEntry::StateBits refcount field
// (which IS bumped by XStrongPtr -- the deliberate contrast).
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

    XObject Obj;
    ::uint32 Serial = 0;
    const ::int32 Idx = Array.ReserveSlot(&Serial);
    Obj.InternalIndex = Idx;
    Obj.SerialNumber  = Serial;
    Array.BindObject(Idx, &Obj);

    const ::uint32 Before = Array.GetRefCount(Idx);
    Check(Before == 0u, "pre-XWeakPtr: refcount != 0 baseline");

    {
        XWeakPtr<XObject> W(&Obj);
        (void)W;
        const ::uint32 AfterCtor = Array.GetRefCount(Idx);
        Check(AfterCtor == 0u,
              "post-XWeakPtr ctor: refcount changed (XWeakPtr is "
              "rooting?)");

        XWeakPtr<XObject> W2(W);
        (void)W2;
        const ::uint32 AfterCopy = Array.GetRefCount(Idx);
        Check(AfterCopy == 0u,
              "post-XWeakPtr copy: refcount changed");
    }

    const ::uint32 AfterDtor = Array.GetRefCount(Idx);
    Check(AfterDtor == 0u,
          "post-XWeakPtr dtor: refcount changed");

    Array.FreeEntry(Idx);
    Array.__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XWeakPtr.NoSelfRoot: PASS\n";
        return 0;
    }
    std::cerr << "XWeakPtr.NoSelfRoot: " << FailureCount << " FAIL(s)\n";
    return 1;
}
