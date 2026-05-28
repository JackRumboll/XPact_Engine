// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XSoftPtr.Tests/Reset.cpp -- Reset() invariant (XCoreXObject Rev 4 §6.3).
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XObject.h"
#include "XObject/XSoftPtr.h"
#include "Reflection/FSoftObjectPath.h"

#include <cstdint>
#include <iostream>

int main()
{
    using ::XCore::FXObjectArray;
    using ::XCore::XObject;
    using ::XCore::XSoftPtr;
    using ::XCore::Reflect::FSoftObjectPath;

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

    // Reset() on a Path-bound soft-ptr clears Path AND CachedRef.
    {
        FSoftObjectPath Path;
        Path.Storage = 0x1234u;
        XSoftPtr<XObject> S(Path);
        S.Reset();
        Check(S.GetPath().IsNull(),   "post-Reset: Path not null");
        Check(S.CachedRef.IsNull(),    "post-Reset: CachedRef not null");
        Check(S.IsNull(),              "post-Reset: IsNull() false");
    }

    // Reset() on a from-T*-bound soft-ptr clears CachedRef (Path was
    // null at construction in Phase 5.c posture).
    {
        XObject Obj;
        ::uint32 Serial = 0;
        const ::int32 Idx = Array.ReserveSlot(&Serial);
        Obj.InternalIndex = Idx;
        Obj.SerialNumber  = Serial;
        Array.BindObject(Idx, &Obj);

        XSoftPtr<XObject> S(&Obj);
        Check(!S.IsNull(), "pre-Reset: IsNull() true (CachedRef should be populated)");
        S.Reset();
        Check(S.IsNull(), "post-Reset: IsNull() false");
        Check(S.CachedRef.IsNull(), "post-Reset: CachedRef not null");

        Array.FreeEntry(Idx);
    }

    Array.__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XSoftPtr.Reset: PASS\n";
        return 0;
    }
    std::cerr << "XSoftPtr.Reset: " << FailureCount << " FAIL(s)\n";
    return 1;
}
