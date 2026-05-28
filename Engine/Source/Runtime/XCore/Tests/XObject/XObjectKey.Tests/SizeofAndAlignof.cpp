// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XObjectKey.Tests/SizeofAndAlignof.cpp -- ABI lock smoke test
// (XCoreXObject Rev 4 §6.4 + §11.1 XPACT_XOBJECTKEY_LAYOUT_TAG).
// =====================================================================
//
// The static_asserts in XObjectKey.h ARE the ABI contract; this TU
// confirms they're in force at the test-link site (the same ABI must
// hold in every TU that includes the header).
//
// =====================================================================

#include "XObject/XObjectKey.h"

#include <cstddef>
#include <cstdint>
#include <iostream>

int main()
{
    using ::XCore::XObjectKey;

    int FailureCount = 0;

    auto Check = [&](bool Cond, const char* Diagnostic)
    {
        if (!Cond)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++FailureCount;
        }
    };

    // -----------------------------------------------------------------
    // sizeof + alignof + offsetof contract (mirrors the header asserts
    // at the test-link site).
    // -----------------------------------------------------------------
    Check(sizeof(XObjectKey)  == 8u, "sizeof(XObjectKey) != 8");
    Check(alignof(XObjectKey) == 4u, "alignof(XObjectKey) != 4");

    Check(offsetof(XObjectKey, InternalIndex) == 0u,
          "offsetof(XObjectKey, InternalIndex) != 0");
    Check(offsetof(XObjectKey, SerialNumber)  == 4u,
          "offsetof(XObjectKey, SerialNumber)  != 4");

    Check(sizeof(XObjectKey::InternalIndex) == 4u,
          "sizeof(XObjectKey::InternalIndex) != 4");
    Check(sizeof(XObjectKey::SerialNumber)  == 4u,
          "sizeof(XObjectKey::SerialNumber)  != 4");

    if (FailureCount == 0)
    {
        std::cout << "XObjectKey.SizeofAndAlignof: PASS\n";
        return 0;
    }
    std::cerr << "XObjectKey.SizeofAndAlignof: " << FailureCount << " FAIL(s)\n";
    return 1;
}
