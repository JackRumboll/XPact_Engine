// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGCRootSpan.Tests/GetSpanCount.cpp -- span counter diagnostics
// (XCoreXObject Rev 4 §5.3; Phase 5.e).
// =====================================================================
//
// GetSpanCount is an atomic snapshot of the active span count.
// GetConservativeSpanCount is an O(N) locked scan counting only
// kConservative spans.
//
// Verifies:
//   * Both counters start at 0.
//   * AddSpan bumps GetSpanCount.
//   * GetConservativeSpanCount only counts kConservative spans.
//   * RemoveSpan decrements both counters appropriately.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XGCRootSpan.h"
#include "XObject/XObject.h"

#include <iostream>

int main()
{
    using ::XCore::EXGCRootSpanKind;
    using ::XCore::FXObjectArray;
    using ::XCore::XGCRootSpan;
    using ::XCore::XGCRootSpanRegistry;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray::Get().__ResetForTests();
    XGCRootSpanRegistry& Registry = XGCRootSpanRegistry::Get();
    Registry.__ResetForTests();

    int FailureCount = 0;
    auto Check = [&](bool Cond, const char* Diagnostic)
    {
        if (!Cond)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++FailureCount;
        }
    };

    Check(Registry.GetSpanCount() == 0u,
          "baseline GetSpanCount != 0");
    Check(Registry.GetConservativeSpanCount() == 0u,
          "baseline GetConservativeSpanCount != 0");

    // Build typed + conservative spans.
    XObject* SlotArrayA[4] = { nullptr };
    XObject* SlotArrayB[4] = { nullptr };
    XObject* SlotArrayC[4] = { nullptr };

    XGCRootSpan TypedA;
    TypedA.BaseAddress   = SlotArrayA;
    TypedA.ByteLength    = sizeof(SlotArrayA);
    TypedA.ElementStride = sizeof(XObject*);
    TypedA.Kind          = EXGCRootSpanKind::kObject;

    XGCRootSpan ConservativeB;
    ConservativeB.BaseAddress   = SlotArrayB;
    ConservativeB.ByteLength    = sizeof(SlotArrayB);
    ConservativeB.ElementStride = sizeof(XObject*);
    ConservativeB.Kind          = EXGCRootSpanKind::kConservative;

    XGCRootSpan TypedC;
    TypedC.BaseAddress   = SlotArrayC;
    TypedC.ByteLength    = sizeof(SlotArrayC);
    TypedC.ElementStride = sizeof(XObject*);
    TypedC.Kind          = EXGCRootSpanKind::kObject;

    const ::int32 H_A = Registry.AddSpan(TypedA);
    const ::int32 H_B = Registry.AddSpan(ConservativeB);
    const ::int32 H_C = Registry.AddSpan(TypedC);
    Check(H_A >= 0 && H_B >= 0 && H_C >= 0, "AddSpan handle invalid");

    Check(Registry.GetSpanCount() == 3u,
          "GetSpanCount after 3 AddSpan != 3");
    Check(Registry.GetConservativeSpanCount() == 1u,
          "GetConservativeSpanCount != 1 with 1 kConservative span");

    // Add another Conservative span.
    XObject* SlotArrayD[4] = { nullptr };
    XGCRootSpan ConservativeD;
    ConservativeD.BaseAddress   = SlotArrayD;
    ConservativeD.ByteLength    = sizeof(SlotArrayD);
    ConservativeD.ElementStride = sizeof(XObject*);
    ConservativeD.Kind          = EXGCRootSpanKind::kConservative;
    const ::int32 H_D = Registry.AddSpan(ConservativeD);
    Check(H_D >= 0, "Conservative D AddSpan failed");
    Check(Registry.GetSpanCount() == 4u,
          "GetSpanCount after adding D != 4");
    Check(Registry.GetConservativeSpanCount() == 2u,
          "GetConservativeSpanCount != 2 with 2 kConservative spans");

    // Remove the Conservative B; both counters decrement appropriately.
    Registry.RemoveSpan(H_B);
    Check(Registry.GetSpanCount() == 3u,
          "GetSpanCount after Remove(B) != 3");
    Check(Registry.GetConservativeSpanCount() == 1u,
          "GetConservativeSpanCount after Remove(B) != 1");

    // Remove a typed span; GetConservativeSpanCount unaffected.
    Registry.RemoveSpan(H_A);
    Check(Registry.GetSpanCount() == 2u,
          "GetSpanCount after Remove(A) != 2");
    Check(Registry.GetConservativeSpanCount() == 1u,
          "GetConservativeSpanCount after Remove(A) != 1 (typed removal must not change conservative count)");

    // Cleanup.
    Registry.RemoveSpan(H_C);
    Registry.RemoveSpan(H_D);
    Check(Registry.GetSpanCount() == 0u, "post-cleanup GetSpanCount != 0");
    Check(Registry.GetConservativeSpanCount() == 0u,
          "post-cleanup GetConservativeSpanCount != 0");

    Registry.__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XGCRootSpan.GetSpanCount: PASS\n";
        return 0;
    }
    std::cerr << "XGCRootSpan.GetSpanCount: " << FailureCount
              << " FAIL(s)\n";
    return 1;
}
