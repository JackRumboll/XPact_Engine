// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XStrongPtr.Tests/MoveDoesNotBump.cpp -- move ctor + move-assign
// (XCoreXObject Rev 4 §6.5).
// =====================================================================
//
// Spec §6.5: move ctor "moves; no refcount change". The source's Ptr
// is nulled WITHOUT calling ReleaseRef; the destination takes over the
// existing refcount. Net refcount is unchanged across the move.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XObject.h"
#include "XObject/XStrongPtr.h"

#include <cstdint>
#include <iostream>
#include <utility>

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

    // Move ctor: refcount unchanged; source becomes null.
    {
        XStrongPtr<XObject> S1(&Obj);
        Check(Array.GetRefCount(Idx) == 1u, "pre-move-ctor: refcount != 1");

        XStrongPtr<XObject> S2(std::move(S1));
        Check(Array.GetRefCount(Idx) == 1u, "post-move-ctor: refcount != 1");
        Check(S2.Get() == &Obj, "post-move-ctor: S2.Get() != &Obj");
        Check(S1.Get() == nullptr,
              "post-move-ctor: S1.Get() != nullptr (source not nulled)");
    }
    Check(Array.GetRefCount(Idx) == 0u,
          "post-scope (move-ctor): refcount != 0 after dtor");

    // Move-assign: target's prior refcount released; source's refcount
    // transferred without change.
    {
        XStrongPtr<XObject> S1(&Obj);
        XStrongPtr<XObject> S2;

        Check(Array.GetRefCount(Idx) == 1u, "pre-move-assign: refcount != 1");
        S2 = std::move(S1);
        Check(Array.GetRefCount(Idx) == 1u, "post-move-assign: refcount != 1");
        Check(S2.Get() == &Obj, "post-move-assign: S2.Get() != &Obj");
        Check(S1.Get() == nullptr,
              "post-move-assign: S1.Get() != nullptr");
    }
    Check(Array.GetRefCount(Idx) == 0u,
          "post-scope (move-assign): refcount != 0 after dtor");

    Array.FreeEntry(Idx);
    Array.__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XStrongPtr.MoveDoesNotBump: PASS\n";
        return 0;
    }
    std::cerr << "XStrongPtr.MoveDoesNotBump: " << FailureCount
              << " FAIL(s)\n";
    return 1;
}
