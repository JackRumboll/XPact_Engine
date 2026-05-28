// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XWeakPtr.Tests/SizeofAndAlignof.cpp -- ABI lock smoke test
// (XCoreXObject Rev 4 §6.2 + §11.1 XPACT_XWEAKPTR_LAYOUT_TAG).
// =====================================================================

#include "XObject/XObject.h"
#include "XObject/XWeakPtr.h"

#include <cstddef>
#include <cstdint>
#include <iostream>
#include <type_traits>

int main()
{
    using ::XCore::XObject;
    using ::XCore::XWeakPtr;

    int FailureCount = 0;
    auto Check = [&](bool Cond, const char* Diagnostic)
    {
        if (!Cond)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++FailureCount;
        }
    };

    Check(sizeof(XWeakPtr<XObject>)  == 8u, "sizeof(XWeakPtr<XObject>) != 8");
    Check(alignof(XWeakPtr<XObject>) == 4u, "alignof(XWeakPtr<XObject>) != 4");
    Check(offsetof(XWeakPtr<XObject>, InternalIndex) == 0u,
          "offsetof(XWeakPtr<XObject>, InternalIndex) != 0");
    Check(offsetof(XWeakPtr<XObject>, SerialNumber) == 4u,
          "offsetof(XWeakPtr<XObject>, SerialNumber) != 4");

    Check(std::is_trivially_copyable_v<XWeakPtr<XObject>>,
          "XWeakPtr<XObject> not trivially copyable");
    Check(std::is_trivially_destructible_v<XWeakPtr<XObject>>,
          "XWeakPtr<XObject> not trivially destructible");
    Check(std::is_standard_layout_v<XWeakPtr<XObject>>,
          "XWeakPtr<XObject> not standard layout");

    if (FailureCount == 0)
    {
        std::cout << "XWeakPtr.SizeofAndAlignof: PASS\n";
        return 0;
    }
    std::cerr << "XWeakPtr.SizeofAndAlignof: " << FailureCount << " FAIL(s)\n";
    return 1;
}
