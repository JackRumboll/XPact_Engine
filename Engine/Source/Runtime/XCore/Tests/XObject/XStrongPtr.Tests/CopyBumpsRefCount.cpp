// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XStrongPtr.Tests/CopyBumpsRefCount.cpp -- copy ctor + copy-assign
// (XCoreXObject Rev 4 §6.5).
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

    // Copy ctor bumps the refcount.
    {
        XStrongPtr<XObject> S1(&Obj);
        Check(Array.GetRefCount(Idx) == 1u, "S1: refcount != 1");

        XStrongPtr<XObject> S2(S1);
        Check(Array.GetRefCount(Idx) == 2u, "post-copy-ctor: refcount != 2");
        Check(S2.Get() == &Obj, "post-copy-ctor: S2.Get() != &Obj");
    }
    Check(Array.GetRefCount(Idx) == 0u, "post-scope: refcount != 0");

    // Copy-assign bumps the refcount (and releases the destination's
    // prior pointee; here that's null so only the bump fires).
    {
        XStrongPtr<XObject> S1(&Obj);
        Check(Array.GetRefCount(Idx) == 1u, "S1: refcount != 1");

        XStrongPtr<XObject> S2;
        S2 = S1;
        Check(Array.GetRefCount(Idx) == 2u, "post-copy-assign: refcount != 2");
        Check(S2.Get() == &Obj, "post-copy-assign: S2.Get() != &Obj");
    }
    Check(Array.GetRefCount(Idx) == 0u, "post-scope2: refcount != 0");

    // Self-assignment: refcount unchanged.
    {
        XStrongPtr<XObject> S(&Obj);
        Check(Array.GetRefCount(Idx) == 1u, "pre-self-assign: refcount != 1");
        S = S;
        Check(Array.GetRefCount(Idx) == 1u, "post-self-assign: refcount != 1");
    }
    Check(Array.GetRefCount(Idx) == 0u, "post-self-assign scope: refcount != 0");

    Array.FreeEntry(Idx);
    Array.__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XStrongPtr.CopyBumpsRefCount: PASS\n";
        return 0;
    }
    std::cerr << "XStrongPtr.CopyBumpsRefCount: " << FailureCount
              << " FAIL(s)\n";
    return 1;
}
