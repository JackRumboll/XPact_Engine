// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XStrongPtr.Tests/AssignReleaseAndBump.cpp -- assignment between two
// XStrongPtrs holding different objects (XCoreXObject Rev 4 §6.5).
// =====================================================================
//
// Spec §6.5: copy-assign releases the destination's prior refcount and
// bumps the new target's refcount. Two distinct objects' refcounts
// must reflect the swap.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XObject.h"
#include "XObject/XStrongPtr.h"

#include <cstdint>
#include <iostream>

namespace
{
    alignas(8) std::uint64_t s_FakeClassStorage = 0xDEADBEEFCAFEu;
    const ::XCore::Reflect::FClass* const s_FakeClassPtr =
        reinterpret_cast<const ::XCore::Reflect::FClass*>(&s_FakeClassStorage);
}

int main()
{
    using ::XCore::FXObjectArray;
    using ::XCore::XObject;
    using ::XCore::XStrongPtr;

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

    // Two distinct objects in two distinct slots.
    XObject ObjA; ObjA.ClassPrivate = s_FakeClassPtr;
    XObject ObjB; ObjB.ClassPrivate = s_FakeClassPtr;

    ::uint32 SA = 0;
    const ::int32 IdxA = Array.ReserveSlot(&SA);
    ObjA.InternalIndex = IdxA;
    ObjA.SerialNumber  = SA;
    Array.BindObject(IdxA, &ObjA);

    ::uint32 SB = 0;
    const ::int32 IdxB = Array.ReserveSlot(&SB);
    ObjB.InternalIndex = IdxB;
    ObjB.SerialNumber  = SB;
    Array.BindObject(IdxB, &ObjB);

    // Setup: S holds A; A refcount = 1, B refcount = 0.
    {
        XStrongPtr<XObject> S(&ObjA);
        Check(Array.GetRefCount(IdxA) == 1u, "init: A refcount != 1");
        Check(Array.GetRefCount(IdxB) == 0u, "init: B refcount != 0");

        // Assign to B via T*: S releases A, AddRef B. The Reset call
        // chain does AddRef(B) BEFORE ReleaseRef(A) per the spec
        // discipline (release-then-bump would expose A to GC reclaim
        // if A == B; we use AddRef-then-release for safety).
        S = &ObjB;
        Check(Array.GetRefCount(IdxA) == 0u,
              "post-assign-T*: A refcount != 0 (release missed)");
        Check(Array.GetRefCount(IdxB) == 1u,
              "post-assign-T*: B refcount != 1 (bump missed)");
        Check(S.Get() == &ObjB, "post-assign-T*: S.Get() != &ObjB");

        // Re-assign back to A via XStrongPtr copy.
        XStrongPtr<XObject> SA_ptr(&ObjA);
        Check(Array.GetRefCount(IdxA) == 1u, "SA_ptr: A refcount != 1");
        Check(Array.GetRefCount(IdxB) == 1u, "SA_ptr: B refcount != 1");

        S = SA_ptr;
        Check(Array.GetRefCount(IdxA) == 2u,
              "post-copy-assign: A refcount != 2 (S + SA_ptr should hold)");
        Check(Array.GetRefCount(IdxB) == 0u,
              "post-copy-assign: B refcount != 0 (S's prior B-ref not released)");
    }
    Check(Array.GetRefCount(IdxA) == 0u,
          "post-scope: A refcount != 0");
    Check(Array.GetRefCount(IdxB) == 0u,
          "post-scope: B refcount != 0");

    Array.FreeEntry(IdxA);
    Array.FreeEntry(IdxB);
    Array.__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XStrongPtr.AssignReleaseAndBump: PASS\n";
        return 0;
    }
    std::cerr << "XStrongPtr.AssignReleaseAndBump: " << FailureCount
              << " FAIL(s)\n";
    return 1;
}
