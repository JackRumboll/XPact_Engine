// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// Phase5LSummary.cpp -- Foundation Prototype acceptance summary
// aggregator.
// =====================================================================
//
// This TU is a stand-alone `int main()` that PRINTS the coverage matrix
// of every Foundation Prototype criterion + every X gate at a glance.
// The summary is structural -- it documents WHICH tests cover WHICH
// gates + the coverage status (live / hardware-required / deferred-
// XIL2CPP / deferred-XLiveCoding / deferred-editor / build-config).
//
// The Phase5LSummary is meant to be run AFTER the individual harness
// TUs; it does NOT itself execute any test. The XBT test runner can
// run it last to produce a digestible end-of-run summary in the test
// log. The TU returns 0 unconditionally (it is informational).
//
// Phase 5.l ships this aggregator as a documentation artifact + a
// machine-readable summary the user can consult to see "what's covered
// live, what's deferred, and why".
//
// =====================================================================

#include <iostream>

namespace
{
    void Row(const char* Gate, const char* Status, const char* TestName,
             const char* Reason)
    {
        std::cout << "  " << Gate << "  | " << Status << " | "
                  << TestName << "\n";
        if (Reason != nullptr && Reason[0] != '\0')
        {
            std::cout << "      "
                      << "                                "
                      << " " << Reason << "\n";
        }
    }
}

int main()
{
    std::cout << "\n";
    std::cout << "====================================================="
                 "=================================\n";
    std::cout << "  XCoreXObject Phase 5.l Foundation Prototype Coverage "
                 "Summary\n";
    std::cout << "====================================================="
                 "=================================\n";
    std::cout << "  Spec: Documents/XCoreXObject.html Rev 4, §13.1 + "
                 "§13.2.\n";
    std::cout << "  Status legend:\n";
    std::cout << "    LIVE       -- executes in CI on Win64; PASS/FAIL "
                 "reported.\n";
    std::cout << "    HARDWARE   -- builds in CI; full acceptance runs "
                 "on Quest 3 ARM64.\n";
    std::cout << "    SCALED     -- scaled-down CI workload; full-scale "
                 "runs on Quest 3.\n";
    std::cout << "    XIL2CPP    -- deferred to System 6 (XIL2CPP "
                 "transpiler).\n";
    std::cout << "    XLIVECODE  -- deferred to System 5 (XLiveCoding "
                 "patch producer).\n";
    std::cout << "    EDITOR     -- deferred to System 10+ (editor "
                 "binary).\n";
    std::cout << "    CONFIG     -- gated on build configuration (e.g., "
                 "XPACT_LEAK_TRACKING_ENABLED).\n";
    std::cout << "\n";

    std::cout << "-----------------------------------------------------"
                 "---------------------------------\n";
    std::cout << "  Master Plan §11 criteria (a)-(i):\n";
    std::cout << "-----------------------------------------------------"
                 "---------------------------------\n";

    Row("(a) GC pause p99 < 5ms        ", "SCALED  ",
        "CriterionA_GCPauseBudget.Tests",
        "5s/1k-alloc-per-100ms CI scale; 60s Quest 3.");
    Row("(b) Full-heap-scan < 50ms     ", "SCALED  ",
        "CriterionB_FullHeapScanFallback.Tests",
        "10k objects CI; 100k Quest 3.");
    Row("(c) Cross-arch determinism    ", "XIL2CPP ",
        "CriterionC_CrossArchDeterminism.Tests (SKIP)",
        "Needs sim-path executor + XIL2CPP + cross-arch CI shard.");
    Row("(d) Single-method hot-patch   ", "XLIVECODE",
        "CriterionD_SingleMethodHotPatch.Tests (SKIP)",
        "Needs XLiveCoding DLL hot-swap producer.");
    Row("(e) Cascade hot-patch         ", "XLIVECODE",
        "CriterionE_CascadeHotPatch.Tests (SKIP)",
        "Needs XLiveCoding cascade protocol producer.");
    Row("(f) Reflection round-trip     ", "LIVE    ",
        "CriterionF_ReflectionRoundTrip.Tests",
        "FProperty Set/Get + Identical surface (XCore-4b primitives).");
    Row("(g) Deterministic codegen     ", "LIVE    ",
        "(static_asserts at §11.3; X1 ABI lock test covers the ABI "
        "shape gate.)", "");
    Row("(h) Fragmentation < 15% / 4hr ", "SCALED  ",
        "CriterionH_FragmentationBudget.Tests",
        "30s soak CI; 4hr Quest 3.");
    Row("(i) Editor cold-start <= 15s  ", "EDITOR  ",
        "CriterionI_EditorColdStart.Tests (SKIP)",
        "Needs editor binary; X7 covers the lazy-CDO precondition.");

    std::cout << "\n";
    std::cout << "-----------------------------------------------------"
                 "---------------------------------\n";
    std::cout << "  XCoreXObject-specific gates X1-X12 + X-*:\n";
    std::cout << "-----------------------------------------------------"
                 "---------------------------------\n";

    Row("X1  sizeof(XObject) == 56     ", "LIVE    ",
        "X1_XObjectSizeofLocks.Tests", "");
    Row("X2  NewObject latency         ", "LIVE    ",
        "X2_NewObjectLatency.Tests",
        "CI 10k iters; spec 1M iters; Quest 3 hardware acceptance.");
    Row("X3  XWeakPtr::Get latency     ", "LIVE    ",
        "X3_XWeakPtrGetLatency.Tests",
        "CI 100k iters; spec 1M iters; Quest 3 hardware acceptance.");
    Row("X4  Write-barrier emit cost   ", "LIVE    ",
        "X4_WriteBarrierCost.Tests",
        "1M-iter; spec ~5 cycles.");
    Row("X5  Card-table saturation     ", "SCALED  ",
        "X5_CardTableSaturation.Tests",
        "1k objects CI; 50k Quest 3.");
    Row("X6  Hot-reload Key resolution ", "LIVE    ",
        "X6_HotReloadXObjectKeySurvives.Tests",
        "10k-instance spec scale; Phase 5.j producer covered.");
    Row("X7  CDO lazy construction     ", "LIVE    ",
        "X7_CDOLazyConstruction.Tests",
        "Timing + 8-thread race; Phase 5.d producer covered.");
    Row("X8  FXObjectArray contention  ", "LIVE    ",
        "X8_FXObjectArrayContention.Tests",
        "4-thread vs single-thread ratio; Phase 5.b producer.");
    Row("X9  Conservative scanning     ", "LIVE    ",
        "X9_ConservativeScanningCorrectness.Tests",
        "5000+5000 mixed entries; Phase 5.e producer covered.");
    Row("X10 Precise stack scanning    ", "XIL2CPP ",
        "X10_PreciseStackScanning.Tests (SKIP)",
        "Needs XIL2CPP-emitted FStackRootEntry tables.");
    Row("X11 Scenario-boundary reclaim ", "LIVE    ",
        "X11_ScenarioBoundaryReclaim.Tests",
        "10-class * 10-instance CI; 100-class * 10-instance spec; "
        "Phase 5.i producer covered.");
    Row("X12 Leak tracker accuracy     ", "CONFIG  ",
        "X12_LeakTrackerAccuracy.Tests",
        "Debug/Development only; XPACT_LEAK_TRACKING_ENABLED gate.");
    Row("X-DET Determinism guards      ", "LIVE    ",
        "XDET_DeterminismGuards.Tests",
        "Happy-path; sim-path negative deferred to IsSimPathTU probe.");
    Row("X-NEW Sim-path NewObject guard", "LIVE    ",
        "XNEW_SimPathNewObjectGuards.Tests",
        "Happy-path; sim-path negative deferred to IsSimPathTU probe.");
    Row("X-INIT Init-phase gate        ", "LIVE    ",
        "XINIT_InitPhaseGate.Tests",
        "Pre-PostStaticInit + post-PostStaticInit branches.");
    Row("X-INIT-OBJ Initializer dtor   ", "LIVE    ",
        "XINITOBJ_FXObjectInitializerDestructor.Tests",
        "Single + 5x; identity assertions verified.");
    Row("X-SCHEMA Schema-vector walk   ", "SCALED  ",
        "XSCHEMA_SchemaVectorPerformance.Tests",
        "10k * 5 refs CI; 100k * 5-10 refs Quest 3.");
    Row("X-INIT-EAGER EagerCDO drain   ", "LIVE    ",
        "XINITEAGER_EagerCDOOrdering.Tests",
        "4-class drain; idempotency; cached CDO.");

    std::cout << "\n";
    std::cout << "====================================================="
                 "=================================\n";
    std::cout << "  End of summary.\n";
    std::cout << "====================================================="
                 "=================================\n";

    return 0;
}
