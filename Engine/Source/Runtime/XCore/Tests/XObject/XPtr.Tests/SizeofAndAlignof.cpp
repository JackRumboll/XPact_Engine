// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XPtr.Tests/SizeofAndAlignof.cpp -- ABI lock smoke test
// (XCoreXObject Rev 4 §6.1 + §11.1 XPACT_XPTR_LAYOUT_TAG).
// =====================================================================

#include "XObject/XObject.h"
#include "XObject/XPtr.h"

#include <cstddef>
#include <cstdint>
#include <iostream>
#include <type_traits>

int main()
{
    using ::XCore::XObject;
    using ::XCore::XPtr;

    int FailureCount = 0;
    auto Check = [&](bool Cond, const char* Diagnostic)
    {
        if (!Cond)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++FailureCount;
        }
    };

    Check(sizeof(XPtr<XObject>)  == 8u, "sizeof(XPtr<XObject>) != 8");
    Check(alignof(XPtr<XObject>) == 8u, "alignof(XPtr<XObject>) != 8");
    Check(offsetof(XPtr<XObject>, Ptr) == 0u, "offsetof(XPtr<XObject>, Ptr) != 0");
    Check(sizeof(XPtr<XObject>::Ptr)   == 8u, "sizeof(XPtr<XObject>::Ptr) != 8");

    // Trait contract: XPtr is trivially copyable + trivially
    // destructible + standard layout (the raw-T*-compatibility
    // invariants).
    Check(std::is_trivially_copyable_v<XPtr<XObject>>,
          "XPtr<XObject> not trivially copyable");
    Check(std::is_trivially_destructible_v<XPtr<XObject>>,
          "XPtr<XObject> not trivially destructible");
    Check(std::is_standard_layout_v<XPtr<XObject>>,
          "XPtr<XObject> not standard layout");

    if (FailureCount == 0)
    {
        std::cout << "XPtr.SizeofAndAlignof: PASS\n";
        return 0;
    }
    std::cerr << "XPtr.SizeofAndAlignof: " << FailureCount << " FAIL(s)\n";
    return 1;
}
