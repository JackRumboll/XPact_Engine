// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XStrongPtr.Tests/RefCountDecOnDestruct.cpp -- ReleaseRef invariant
// (XCoreXObject Rev 4 §6.5 + spec §3.3 StateBits refcount).
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

    XObject Obj;
    Obj.ClassPrivate = s_FakeClassPtr;
    ::uint32 Serial = 0;
    const ::int32 Idx = Array.ReserveSlot(&Serial);
    Obj.InternalIndex = Idx;
    Obj.SerialNumber  = Serial;
    Array.BindObject(Idx, &Obj);

    // Build 5 XStrongPtrs, destroy them, expect refcount returns to 0.
    {
        XStrongPtr<XObject> S1(&Obj);
        XStrongPtr<XObject> S2(&Obj);
        XStrongPtr<XObject> S3(&Obj);
        XStrongPtr<XObject> S4(&Obj);
        XStrongPtr<XObject> S5(&Obj);
        Check(Array.GetRefCount(Idx) == 5u, "5x ctor: refcount != 5");
    }
    Check(Array.GetRefCount(Idx) == 0u,
          "5x dtor: refcount != 0 after balanced release");

    // Reset() also decrements.
    {
        XStrongPtr<XObject> S1(&Obj);
        XStrongPtr<XObject> S2(&Obj);
        Check(Array.GetRefCount(Idx) == 2u, "pre-Reset: refcount != 2");
        S1.Reset();
        Check(Array.GetRefCount(Idx) == 1u, "post-Reset(S1): refcount != 1");
        S2.Reset(nullptr);
        Check(Array.GetRefCount(Idx) == 0u, "post-Reset(S2): refcount != 0");
    }

    Array.FreeEntry(Idx);
    Array.__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XStrongPtr.RefCountDecOnDestruct: PASS\n";
        return 0;
    }
    std::cerr << "XStrongPtr.RefCountDecOnDestruct: " << FailureCount
              << " FAIL(s)\n";
    return 1;
}
