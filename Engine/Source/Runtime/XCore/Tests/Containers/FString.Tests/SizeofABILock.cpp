// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FString.Tests/SizeofABILock.cpp -- sizeof + alignof verification.
// =====================================================================
//
// Verifies the spec ABI locks: sizeof(FString) == 64, alignof == 16,
// sizeof(XCSharpString) == sizeof(void*).
//
// These also exist as static_asserts in FString.h; this test is the
// belt-and-braces runtime check that ALSO drops into the build's
// test exe (so the Section 17 acceptance criteria for ABI locks
// have an artifact other than the compile-time assert).
// =====================================================================

#include "Containers/FString.h"

#include <cstdio>

int main()
{
    if (sizeof(::XCore::FString) != 64)
    {
        std::fprintf(stderr, "FAIL: sizeof(FString) = %zu (expected 64)\n", sizeof(::XCore::FString));
        return 1;
    }
    if (alignof(::XCore::FString) != 16)
    {
        std::fprintf(stderr, "FAIL: alignof(FString) = %zu (expected 16)\n", alignof(::XCore::FString));
        return 1;
    }
    if (sizeof(::XCore::XCSharpString) != sizeof(void*))
    {
        std::fprintf(stderr, "FAIL: sizeof(XCSharpString) = %zu (expected %zu)\n",
            sizeof(::XCore::XCSharpString), sizeof(void*));
        return 1;
    }
    if (alignof(::XCore::XCSharpString) != alignof(void*))
    {
        std::fprintf(stderr, "FAIL: alignof(XCSharpString) = %zu (expected %zu)\n",
            alignof(::XCore::XCSharpString), alignof(void*));
        return 1;
    }
    return 0;
}
