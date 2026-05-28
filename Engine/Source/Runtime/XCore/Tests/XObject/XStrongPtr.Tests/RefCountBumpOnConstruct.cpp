// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XStrongPtr.Tests/RefCountBumpOnConstruct.cpp -- AddRef invariant
// (XCoreXObject Rev 4 §6.5 + spec §3.3 StateBits refcount).
// =====================================================================
//
// Spec §6.5: XStrongPtr's ctor calls FXObjectArray::AddRef which
// increments the refcount sub-field in FXObjectArrayEntry::StateBits.
// The test verifies the bump is observable via GetRefCount.
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

    // Setup: a stack XObject bound to an FXObjectArray slot. The
    // ClassPrivate is set to s_FakeClassPtr so the XStrongPtr's Dev
    // IsValidLowLevel check passes.
    XObject Obj;
    Obj.ClassPrivate = s_FakeClassPtr;
    ::uint32 Serial = 0;
    const ::int32 Idx = Array.ReserveSlot(&Serial);
    Obj.InternalIndex = Idx;
    Obj.SerialNumber  = Serial;
    Array.BindObject(Idx, &Obj);

    Check(Array.GetRefCount(Idx) == 0u,
          "baseline: refcount != 0 (pre-XStrongPtr expectation)");

    // Construct an XStrongPtr; refcount must bump to 1.
    {
        XStrongPtr<XObject> S(&Obj);
        Check(Array.GetRefCount(Idx) == 1u,
              "post-ctor: refcount != 1");
        Check(S.Get() == &Obj, "post-ctor: Get() != &Obj");
    }
    // After scope exit the destructor ran; refcount back to 0.
    Check(Array.GetRefCount(Idx) == 0u,
          "post-dtor: refcount != 0");

    // Nullptr ctor does NOT bump.
    {
        XStrongPtr<XObject> S(nullptr);
        (void)S;
        Check(Array.GetRefCount(Idx) == 0u,
              "nullptr ctor: refcount changed");
    }

    // Multiple XStrongPtrs to the same object: refcount counts.
    {
        XStrongPtr<XObject> S1(&Obj);
        Check(Array.GetRefCount(Idx) == 1u, "S1: refcount != 1");
        XStrongPtr<XObject> S2(&Obj);
        Check(Array.GetRefCount(Idx) == 2u, "S2: refcount != 2");
        XStrongPtr<XObject> S3(&Obj);
        Check(Array.GetRefCount(Idx) == 3u, "S3: refcount != 3");
    }
    Check(Array.GetRefCount(Idx) == 0u,
          "post-scope: refcount != 0 after 3 XStrongPtrs released");

    Array.FreeEntry(Idx);
    Array.__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XStrongPtr.RefCountBumpOnConstruct: PASS\n";
        return 0;
    }
    std::cerr << "XStrongPtr.RefCountBumpOnConstruct: " << FailureCount << " FAIL(s)\n";
    return 1;
}
