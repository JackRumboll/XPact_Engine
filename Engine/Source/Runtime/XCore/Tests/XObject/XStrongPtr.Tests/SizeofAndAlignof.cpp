// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XStrongPtr.Tests/SizeofAndAlignof.cpp -- ABI lock smoke test
// (XCoreXObject Rev 4 §6.5 / Rev 2 FIX-A-MIN-38).
// =====================================================================

#include "XObject/XObject.h"
#include "XObject/XStrongPtr.h"

#include <cstddef>
#include <iostream>
#include <type_traits>

int main()
{
    using ::XCore::XObject;
    using ::XCore::XStrongPtr;

    int FailureCount = 0;
    auto Check = [&](bool Cond, const char* Diagnostic)
    {
        if (!Cond)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++FailureCount;
        }
    };

    Check(sizeof(XStrongPtr<XObject>)  == 8u, "sizeof(XStrongPtr<XObject>) != 8");
    Check(alignof(XStrongPtr<XObject>) == 8u, "alignof(XStrongPtr<XObject>) != 8");
    Check(offsetof(XStrongPtr<XObject>, Ptr) == 0u, "offsetof(Ptr) != 0");

    // Trait contract: NOT trivially copyable (the copy ctor / assign
    // touch the refcount; the destructor releases). Mirrors UE
    // TStrongObjectPtr discipline.
    Check(!std::is_trivially_copyable_v<XStrongPtr<XObject>>,
          "XStrongPtr<XObject> trivially copyable (should NOT be)");
    Check(!std::is_polymorphic_v<XStrongPtr<XObject>>,
          "XStrongPtr<XObject> polymorphic (should NOT be)");

    if (FailureCount == 0)
    {
        std::cout << "XStrongPtr.SizeofAndAlignof: PASS\n";
        return 0;
    }
    std::cerr << "XStrongPtr.SizeofAndAlignof: " << FailureCount << " FAIL(s)\n";
    return 1;
}
