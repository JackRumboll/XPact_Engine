// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// X10_PreciseStackScanning.cpp -- Foundation Prototype X10 acceptance:
// precise stack scanning enumerates 100% of XIL2CPP-emitted XObject
// locals.
// =====================================================================
//
// X10 acceptance (spec §13.2):
//   "precise stack scanning enumerates 100% of XIL2CPP-emitted XObject
//    locals on Win64 + Quest 3 in a synthetic test with 100 nested
//    transpiled functions, each with 1-5 stack-resident XPtrs."
//
// JUDGEMENT CALL (Phase 5.l X10 deferral). The X10 gate requires
// XIL2CPP-emitted code: the precise-stack-scanning protocol relies
// on XHT-emitted stack-root scaffolding (FStackRootMap +
// FStackRootEntry; per Phase 5.e XStackRootScaffolding.Tests/) AND
// the XIL2CPP transpiler-emitted ENTRY/EXIT calls that populate
// the per-function FStackRootEntry tables.
//
// Phase 5.e ships the scaffolding's data structures + the
// FStackRootMapRegistry; the EMITTER that produces the per-function
// FStackRootEntry tables ships as part of System 6 XIL2CPP, which is
// post-XCoreXObject. Without XIL2CPP-emitted code there are no
// stack-resident XPtrs to enumerate -- the test scenario itself is
// not constructable until XIL2CPP ships.
//
// Phase 5.l ships the X10 test SCAFFOLDING (this file's body) +
// a SKIP diagnostic per the dispatch task's "Stub with hardware/
// XIL2CPP markers" instruction.
//
// =====================================================================

#include "../Phase5LCommon.h"

namespace
{
    int g_FailureCount = 0;
}

int main()
{
    Phase5L::ResetAllForTests();

    // X10 lives in this file as a placeholder. When XIL2CPP ships,
    // the test body is:
    //
    //   1. Emit a synthetic chain of 100 nested transpiled functions,
    //      each with 1-5 stack-resident XPtr<XObject> locals + the
    //      FStackRootEntry table that XIL2CPP generates.
    //
    //   2. Inside the deepest function, snapshot the live FStackRootMap
    //      Registry's content + assert that every XPtr from every
    //      level of the call chain appears in the iteration.
    //
    //   3. Verify the count: 100 functions * average 3 XPtrs each = 300
    //      live stack-roots at the deepest point.
    //
    //   4. Return through the chain; verify each function's per-frame
    //      entries are removed by XIL2CPP-emitted exit code.
    //
    // The Phase 5.e XStackRootScaffolding.Tests/ folder already covers
    // the FStackRootMap + Registry surface; X10 wraps it with the
    // XIL2CPP-emitted producer-side correctness once the producer ships.

    P5L_SKIP_AND_PASS(
        "FoundationPrototype.X10_PreciseStackScanning",
        "XIL2CPP-deferred",
        "X10 requires XIL2CPP-emitted FStackRootEntry tables; XIL2CPP "
        "ships in System 6 (post-XCoreXObject). The Phase 5.e "
        "XStackRootScaffolding.Tests/ folder covers the per-function "
        "data-structure half; X10 wraps it with the XIL2CPP-emitted "
        "producer-side correctness gate when System 6 ships.");
}
