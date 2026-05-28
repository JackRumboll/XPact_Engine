// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGCRootSpan.Tests/ConservativeBuildWarning.cpp -- macro expansion test
// (XCoreXObject Rev 4 §5.3 trailing prose; Phase 5.e).
// =====================================================================
//
// XPACT_GC_CONSERVATIVE_WARN(reason) expands to a `#pragma message` on
// sim-path TUs (XPACT_SIMPATH != 0) and to ((void)0) on non-sim-path
// TUs. This test is intentionally minimal: it verifies the macro
// COMPILES on a non-sim-path TU (the common case) and that the macro
// expansion is well-formed on either branch.
//
// The full sim-path-positive smoke test (verifying the #pragma message
// actually appears in the build log) is a build-system-level concern
// out of scope for a runtime unit test. The compile-time existence
// check here is sufficient for the runtime test surface.
//
// PHASE 5.e CONTRACT: the macro MUST always be expandable from a
// callable C++ statement context with no compiler error, regardless
// of XPACT_SIMPATH setting.
//
// =====================================================================

#include "XObject/XPactGCConservativeWarn.h"

#include <iostream>

// Verify the macro expands inside a function body (the typical use
// context for XIL2CPP-generated code).
static void TestMacroInFunctionBody()
{
    XPACT_GC_CONSERVATIVE_WARN(LegacyListOfObject);
    XPACT_GC_CONSERVATIVE_WARN(IL2CPP_GeneratedList);
    XPACT_GC_CONSERVATIVE_WARN(GenericContainer);
}

// Verify the macro can be paired with a statement on either branch.
static void TestMacroWithFollowingStatement()
{
    XPACT_GC_CONSERVATIVE_WARN(SimPathListBackingArray);
    // The macro's expansion ends with `((void)0)` on non-sim-path, or
    // a `_Pragma(...)` directive on sim-path. Either form composes
    // safely with a following statement.
    int Local = 42;
    (void)Local;
}

int main()
{
    TestMacroInFunctionBody();
    TestMacroWithFollowingStatement();

    // The test exists primarily to prove compilation. Failure to
    // compile = failure of the test.
    std::cout << "XGCRootSpan.ConservativeBuildWarning: PASS\n";
    return 0;
}
