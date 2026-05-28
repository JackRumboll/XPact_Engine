// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XSoftPtr.Tests/SizeofAndAlignof.cpp -- Phase 5.c sizeof snapshot
// (XCoreXObject Rev 4 §6.3).
// =====================================================================
//
// Spec §6.3: XSoftPtr is NOT in the ABI-lock set; its size is variable
// (FSoftObjectPath is the System-5 full-impl's path-string handle).
// At Phase 5.c the FSoftObjectPath is an 8-byte placeholder, so:
//
//   sizeof(XSoftPtr<XObject>) == 8 (Path) + 8 (CachedRef XWeakPtr) = 16
//
// =====================================================================

#include "XObject/XObject.h"
#include "XObject/XSoftPtr.h"

#include <cstddef>
#include <iostream>

int main()
{
    using ::XCore::XObject;
    using ::XCore::XSoftPtr;

    int FailureCount = 0;
    auto Check = [&](bool Cond, const char* Diagnostic)
    {
        if (!Cond)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++FailureCount;
        }
    };

    Check(sizeof(XSoftPtr<XObject>)  == 16u, "sizeof(XSoftPtr<XObject>) != 16");
    Check(alignof(XSoftPtr<XObject>) ==  8u, "alignof(XSoftPtr<XObject>) != 8");
    Check(offsetof(XSoftPtr<XObject>, Path)      == 0u, "offsetof Path != 0");
    Check(offsetof(XSoftPtr<XObject>, CachedRef) == 8u, "offsetof CachedRef != 8");

    if (FailureCount == 0)
    {
        std::cout << "XSoftPtr.SizeofAndAlignof: PASS\n";
        return 0;
    }
    std::cerr << "XSoftPtr.SizeofAndAlignof: " << FailureCount << " FAIL(s)\n";
    return 1;
}
