// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XObject.Tests/LayoutVerifyMacro.cpp -- compile-time verification that
// the XPACT_VERIFY_XOBJECT_LAYOUT() macro instantiates clean
// (XCoreXObject Rev 4 §11.3).
// =====================================================================
//
// XHT-emitted .gen.cpp files instantiate XPACT_VERIFY_XOBJECT_LAYOUT()
// at namespace scope to pin the ABI contract. This test mirrors that
// instantiation pattern in a standalone TU so any drift in the macro's
// internal static_asserts surfaces at the Phase 5.a CI gate, not at
// the first downstream XHT-emit regeneration.
//
// The macro's body is entirely static_asserts + a single trailing
// statement; if any pin fails, this TU fails to compile. The main()
// then verifies the macro returned the runtime side properties as a
// belt-and-braces (e.g., sizeof(XObject) == 56 again at runtime; the
// header static_asserts already cover this, but the dual-emit catches
// hypothetical toolchain bugs in offsetof / sizeof computation).
//
// =====================================================================

#include "XObject/XPactVerifyXObjectLayout.h"

#include <iostream>

namespace
{
    // Instantiate the macro at namespace scope (matches the XHT-emit
    // pattern). The macro body is entirely static_asserts +
    // contract-string equality checks; a failure stops compilation.
    XPACT_VERIFY_XOBJECT_LAYOUT();
}

int main()
{
    // Belt-and-braces runtime verification mirroring the macro pins.
    // If we reached main(), every static_assert in
    // XPACT_VERIFY_XOBJECT_LAYOUT() passed at compile time.
    //
    // The sizeof() expressions below are constant-evaluated at
    // compile time; `if constexpr` is the C++17+ idiom for the
    // pattern and silences MSVC C4127 ("conditional expression is
    // constant"; promoted to error under /WX in the XCore test
    // discipline).

    if constexpr (sizeof(::XCore::XObject) != 56)
    {
        std::cerr << "FAIL: sizeof(XObject) != 56 at runtime\n";
        return 1;
    }
    if constexpr (sizeof(::XCore::FXObjectArrayEntry) != 32)
    {
        std::cerr << "FAIL: sizeof(FXObjectArrayEntry) != 32 at runtime\n";
        return 1;
    }
    if constexpr (sizeof(::XCore::Reflect::FStruct) != 120)
    {
        std::cerr << "FAIL: sizeof(FStruct) != 120 (Rev 13.9 cascade) at runtime\n";
        return 1;
    }
    if constexpr (sizeof(::XCore::Reflect::FClass) != 240)
    {
        std::cerr << "FAIL: sizeof(FClass) != 240 (Rev 13.9 cascade) at runtime\n";
        return 1;
    }

    std::cout << "XObject.LayoutVerifyMacro: PASS\n";
    return 0;
}
